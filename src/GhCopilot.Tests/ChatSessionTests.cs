using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Index;
using GhCopilot.Core.Llm;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class ChatSessionTests
    {
        [Theory]
        [InlineData(true, "accepted; changes kept")]
        [InlineData(false, "rejected; this turn's graph changes rolled back")]
        public void PreviewDecision_AnnotatesTurnWithoutEvictingHistory(bool accepted, string outcome)
        {
            var session = new ChatSession();
            session.RecordPreviewDecision(accepted);
            Assert.Empty(session.Messages);
            for (int i = 0; i < 6; i++) session.CompleteTurn("request " + i, "response " + i);
            session.RecordPreviewDecision(accepted);
            Assert.Equal(12, session.Messages.Count);
            Assert.Empty(session.Summary);
            Assert.Equal("request 0", session.Messages[0].Content);
            Assert.Contains(outcome, session.Messages[11].Content);
            session.CompleteTurn("next", "done");
            Assert.Contains(outcome, session.Messages[9].Content);
        }

        [Fact]
        public void CompleteTurn_KeepsSixWholeTurnsWithoutChangingTheirContents()
        {
            var session = new ChatSession();
            IReadOnlyList<ChatMessage> view = session.Messages;
            for (int i = 0; i < 7; i++) session.CompleteTurn("user " + i + "\n  exact", "assistant " + i);

            Assert.Same(view, session.Messages);
            Assert.Equal(12, view.Count);
            Assert.Equal("user 1\n  exact", view[0].Content);
            for (int i = 0; i < 6; i++)
            {
                Assert.Equal("user", view[i * 2].Role);
                Assert.Equal("assistant", view[i * 2 + 1].Role);
                Assert.Equal("assistant " + (i + 1), view[i * 2 + 1].Content);
            }
            Assert.Contains("LOSSY SUMMARY", session.Summary);
            Assert.Contains("user 0 exact", session.Summary);
            Assert.Contains("assistant 0", session.Summary);
            Assert.Throws<NotSupportedException>(() => ((IList<ChatMessage>)view).Add(new ChatMessage("user", "injected")));
        }

        [Fact]
        public void SixTurns_DoNotCreateSummary_AndNullCommitIsAtomic()
        {
            var session = new ChatSession();
            for (int i = 0; i < 6; i++) session.CompleteTurn("u", "a");
            Assert.Empty(session.Summary);
            Assert.Throws<ArgumentNullException>(() => session.CompleteTurn("u", null!));
            Assert.Throws<ArgumentNullException>(() => session.CompleteTurn(null!, "a"));
            Assert.Equal(12, session.Messages.Count);
            Assert.Empty(session.Summary);
        }

        [Fact]
        public void Summary_IsDeterministicAndBoundedAcrossManyLongTurns()
        {
            var first = new ChatSession();
            var second = new ChatSession();
            for (int i = 0; i < 1000; i++)
            {
                string text = i + "\r\n" + new string('x', 5000);
                first.CompleteTurn(text, text);
                second.CompleteTurn(text, text);
                Assert.True(first.Summary.Length <= ChatSession.MaxSummaryCharacters);
            }
            Assert.Equal(first.Summary, second.Summary);
            Assert.Contains("994 older turns", first.Summary);
            Assert.Contains("993", first.Summary);
            Assert.Contains("...", first.Summary);
            Assert.Equal(12, first.Messages.Count);
        }

        [Fact]
        public void NodeAliases_AreStableUniqueAndIndependentOfSpawnTracking()
        {
            var session = new ChatSession();
            Guid first = Guid.NewGuid(), second = Guid.NewGuid();
            Assert.Equal("n1", session.GetNodeId(first));
            Assert.Equal("n2", session.GetNodeId(second));
            session.SpawnedNodeIds.Add(first);
            session.SpawnedNodeIds.Remove(first);
            session.CompleteTurn("u", "a");
            Assert.Equal("n1", session.GetNodeId(first));
            Assert.Empty(session.SpawnedNodeIds);
        }

        [Fact]
        public void Clear_ResetsAllSessionStateAndAliasCounter()
        {
            var session = new ChatSession();
            Guid id = Guid.NewGuid();
            session.GetNodeId(id);
            session.SpawnedNodeIds.Add(id);
            session.LastSpawnedNodeIds.Add(id);
            session.LastMessages.Add("runtime error");
            for (int i = 0; i < 8; i++) session.CompleteTurn("u", "a");
            session.Clear();
            Assert.Empty(session.Messages);
            Assert.Empty(session.Summary);
            Assert.Empty(session.SpawnedNodeIds);
            Assert.Empty(session.LastSpawnedNodeIds);
            Assert.Empty(session.LastMessages);
            Assert.Equal("n1", session.GetNodeId(Guid.NewGuid()));
            Assert.Equal("n2", session.GetNodeId(id));
        }

        [Fact]
        public void Payload_OrdersContextAndEndsWithExactNewUser_NotShortlist()
        {
            var session = new ChatSession();
            for (int i = 0; i < 7; i++) session.CompleteTurn("question " + i, "answer " + i);
            string payload = ChatPromptBuilder.Build(session, null, new[] { "validator detail" }, "new question\n", Shortlist());
            string[] sections = { "SESSION SUMMARY:", "RECENT MESSAGES", "COMPACT GRAPH JSON:", "VALIDATOR / RUNTIME MESSAGES:", "NEW USER MESSAGE:" };
            int previous = -1;
            foreach (string section in sections)
            {
                int current = payload.IndexOf(section, StringComparison.Ordinal);
                Assert.True(current > previous);
                previous = current;
            }
            Assert.Contains("\"role\":\"user\",\"content\":\"question 1\"", payload);
            Assert.Contains("\"role\":\"assistant\",\"content\":\"answer 6\"", payload);
            Assert.EndsWith("NEW USER MESSAGE:" + Environment.NewLine + "new question\n", payload);
            Assert.DoesNotContain("SHORTLIST COMPONENTS:", payload);
            Assert.DoesNotContain("ShortlistOnlyName", payload);
            Assert.Equal(12, session.Messages.Count); // Building never commits the new user.
        }

        [Fact]
        public void Graph_ContainsOnlyCompactMetadata_AndRemapsWiresAndRuntime()
        {
            var session = new ChatSession();
            var snapshot = SampleSnapshot();
            string payload = ChatPromptBuilder.Build(session, snapshot, new[] { "validator detail" }, "continue", Shortlist());
            using JsonDocument document = ParseGraph(payload);
            JsonElement root = document.RootElement;
            Assert.Equal(new[] { "nodes", "wires" }, root.EnumerateObject().Select(p => p.Name));
            JsonElement first = root.GetProperty("nodes")[0];
            Assert.Equal(new[] { "id", "type", "name", "settings", "inputs", "outputs" }, first.EnumerateObject().Select(p => p.Name));
            Assert.Equal("n1", first.GetProperty("id").GetString());
            Assert.Equal("c001", first.GetProperty("type").GetString());
            Assert.Equal(5, first.GetProperty("settings").GetProperty("value").GetDouble());
            Assert.Equal("Radius", root.GetProperty("nodes")[1].GetProperty("inputs")[0].GetProperty("name").GetString());
            JsonElement wire = root.GetProperty("wires")[0];
            Assert.Equal("n1", wire.GetProperty("fromNode").GetString());
            Assert.Equal("n2", wire.GetProperty("toNode").GetString());
            Assert.Equal("graft", wire.GetProperty("targetMapping").GetString());
            Assert.True(wire.GetProperty("simplify").GetBoolean());
            Assert.DoesNotContain("instanceGuid", payload);
            Assert.DoesNotContain("componentGuid", payload);
            Assert.DoesNotContain("isSelected", payload);
            Assert.DoesNotContain("category", payload);
            foreach (CanvasNodeSnapshot node in snapshot.Nodes)
            {
                Assert.DoesNotContain(node.InstanceGuid.ToString(), payload);
                Assert.DoesNotContain(node.ComponentGuid.ToString(), payload);
            }
            Assert.Contains("\"node\":\"n2\",\"level\":\"Error\"", payload);
            Assert.Contains("radius invalid", payload);
            Assert.Contains("validator detail", payload);
        }

        [Fact]
        public void GraphAliases_SurviveSnapshotReorderAndSnapshotIdChanges()
        {
            var session = new ChatSession();
            CanvasSnapshot snapshot = SampleSnapshot();
            ChatPromptBuilder.Build(session, snapshot, null, "first", Shortlist());
            Guid firstGuid = snapshot.Nodes[0].InstanceGuid;
            snapshot.Nodes.Reverse();
            snapshot.Nodes[0].Id = "different-local-id";
            snapshot.Wires.Clear();
            using JsonDocument graph = ParseGraph(ChatPromptBuilder.Build(session, snapshot, null, "second", Shortlist()));
            Assert.Equal("n2", graph.RootElement.GetProperty("nodes")[0].GetProperty("id").GetString());
            Assert.Equal("n1", session.GetNodeId(firstGuid));
        }

        [Fact]
        public void Graph_RedactsGuidsInFreeTextAndHandlesExternalWires()
        {
            CanvasSnapshot snapshot = SampleSnapshot();
            Guid external = Guid.NewGuid();
            snapshot.Nodes[0].Name = external.ToString();
            snapshot.Nodes[0].Settings!.Text = external.ToString("N");
            snapshot.ExternalIncomingWires.Add(new CanvasExternalWire
            {
                ExternalInstanceGuid = external, ExternalParamName = "Value", SelectedNodeId = "s2", SelectedParamName = "Radius"
            });
            snapshot.ExternalOutgoingWires.Add(new CanvasExternalWire
            {
                ExternalInstanceGuid = external, ExternalParamName = "Input", SelectedNodeId = "s2", SelectedParamName = "Circle"
            });
            string payload = ChatPromptBuilder.Build(new ChatSession(), snapshot, null, "continue", Shortlist());
            Assert.DoesNotContain(external.ToString(), payload);
            Assert.DoesNotContain(external.ToString("N"), payload);
            using JsonDocument graph = ParseGraph(payload);
            Assert.Equal(3, graph.RootElement.GetProperty("wires").GetArrayLength());
            Assert.Equal("n3", graph.RootElement.GetProperty("wires")[1].GetProperty("fromNode").GetString());
            Assert.Equal("n3", graph.RootElement.GetProperty("wires")[2].GetProperty("toNode").GetString());
        }

        [Fact]
        public void MissingInstanceGuids_UseDistinctStableSnapshotAliases()
        {
            var session = new ChatSession();
            CanvasSnapshot snapshot = SampleSnapshot();
            foreach (CanvasNodeSnapshot node in snapshot.Nodes) node.InstanceGuid = Guid.Empty;
            string first = ChatPromptBuilder.Build(session, snapshot, null, "continue", Shortlist());
            string second = ChatPromptBuilder.Build(session, snapshot, null, "continue", Shortlist());
            Assert.Equal(first, second);
            using JsonDocument graph = ParseGraph(first);
            Assert.Equal("n1", graph.RootElement.GetProperty("nodes")[0].GetProperty("id").GetString());
            Assert.Equal("n2", graph.RootElement.GetProperty("nodes")[1].GetProperty("id").GetString());
        }

        [Fact]
        public void Diagnostics_DefaultToSessionOrUseExplicitOverride()
        {
            var session = new ChatSession();
            session.LastMessages.Add("session diagnostic");
            Assert.Contains("session diagnostic", ChatPromptBuilder.Build(session, null, null, "continue", Shortlist()));
            string explicitMessages = ChatPromptBuilder.Build(session, null, new[] { "explicit diagnostic" }, "continue", Shortlist());
            Assert.DoesNotContain("session diagnostic", explicitMessages);
            Assert.Contains("explicit diagnostic", explicitMessages);
            Assert.DoesNotContain("session diagnostic", ChatPromptBuilder.Build(session, null, Array.Empty<string>(), "continue", Shortlist()));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void System_UsesSharedInstructionsAndFormatter_WithExplicitOutputMode(bool patchOnly)
        {
            IReadOnlyList<ComponentRecord> shortlist = Shortlist();
            string system = ChatPromptBuilder.BuildSystem(shortlist, patchOnly);
            Assert.StartsWith(PromptBuilder.BuildSystemInstruction(), system);
            Assert.Contains(shortlist[0].FormatPromptContextLine(), system);
            Assert.Contains("SHORTLIST COMPONENTS:", system);
            Assert.Equal(patchOnly, system.Contains("PATCH ONLY:"));
            Assert.Contains(patchOnly ? "GraphPatch" : "Answer conversational questions", system);
            Assert.DoesNotContain(shortlist[0].ComponentGuid.ToString(), system);
        }

        [Theory]
        [InlineData("", "", null, 0)]
        [InlineData("a", "", null, 1)]
        [InlineData("ab", "cd", null, 1)]
        [InlineData("ab", "cd", "e", 2)]
        [InlineData("1234", "5678", "9012", 3)]
        public void EstimateTokens_CeilingsCombinedCharactersOverFour(string system, string payload, string? schema, int expected)
        {
            Assert.Equal(expected, ChatPromptBuilder.EstimateTokens(system, payload, schema));
        }

        [Fact]
        public void Build_IncludesMatchingRecipesBeforeNewUserMessage()
        {
            var recipes = new[]
            {
                new RecipeRecord
                {
                    Name = "CircleRecipe",
                    Description = "Circle from radius slider",
                    Spec = new GraphSpec { Explanation = "Builds circle" }
                }
            };

            string payload = ChatPromptBuilder.Build(new ChatSession(), null, null, "make a circle", Shortlist(), recipes);
            int recipesPos = payload.IndexOf("MATCHING SAVED RECIPES", StringComparison.Ordinal);
            int userPos = payload.IndexOf("NEW USER MESSAGE:", StringComparison.Ordinal);
            Assert.True(recipesPos > 0);
            Assert.True(userPos > recipesPos);
            Assert.Contains("CircleRecipe", payload);
            Assert.EndsWith("NEW USER MESSAGE:" + Environment.NewLine + "make a circle", payload);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void BuildValidationRepairPrompt_IncludesErrorsAndRedactsGuids(bool patchOnly)
        {
            Guid secretId = Guid.NewGuid();
            string repaired = ChatPromptBuilder.BuildValidationRepairPrompt(
                "BASE PAYLOAD",
                $"{{\"id\":\"{secretId}\"}}",
                new[] { "Invalid port 'Foo'" },
                patchOnly);

            Assert.StartsWith("BASE PAYLOAD", repaired);
            Assert.Contains("FAILED VALIDATION", repaired);
            Assert.Contains("Invalid port 'Foo'", repaired);
            Assert.DoesNotContain(secretId.ToString(), repaired);
            Assert.Contains("[guid omitted]", repaired);
            Assert.Equal(patchOnly, repaired.Contains("OMIT unused properties"));
        }

        [Fact]
        public void System_ExplainsNoOpJsonResponsesForConversationalQuestions()
        {
            string patchSystem = ChatPromptBuilder.BuildSystem(Shortlist(), patchOnly: true);
            string createSystem = ChatPromptBuilder.BuildSystem(Shortlist(), patchOnly: false);
            Assert.Contains("OMIT unused properties", patchSystem);
            Assert.Contains("\"ops\":[]", patchSystem);
            Assert.Contains("\"nodes\":[],\"wires\":[]", createSystem);
        }

        [Theory]
        [InlineData("hi", true)]
        [InlineData("Hello!", true)]
        [InlineData("hey there", true)]
        [InlineData("what can you do?", true)]
        [InlineData("thanks", true)]
        [InlineData("who are you?", true)]
        [InlineData("create a circle with a radius slider", false)]
        [InlineData("change radius to 25", false)]
        [InlineData("add a panel", false)]
        [InlineData("extrude the curve", false)]
        [InlineData("wire n1 to n2", false)]
        [InlineData("make a twisted tower", false)]
        [InlineData("inspect this file and build further on it", false)]
        [InlineData("extend this definition", false)]
        public void IsConversationalOnlyPrompt_DistinguishesChatFromCadRequests(string prompt, bool expectedConversational)
        {
            Assert.Equal(expectedConversational, ChatPromptBuilder.IsConversationalOnlyPrompt(prompt));
        }

        [Theory]
        [InlineData("inspect this file and build further on it", true)]
        [InlineData("explain the whole canvas", true)]
        [InlineData("check my open definition", true)]
        [InlineData("change radius to 20", false)]
        public void RequestsWholeCanvasInspection_DetectsWholeFilePrompts(string prompt, bool expected)
        {
            Assert.Equal(expected, ChatPromptBuilder.RequestsWholeCanvasInspection(prompt));
        }

        [Fact]
        public void BuildConversationalSystemAndPayload_ProvideLightContextAndRedactGuids()
        {
            string sys = ChatPromptBuilder.BuildConversationalSystem();
            Assert.Contains("plain text", sys, StringComparison.OrdinalIgnoreCase);

            var session = new ChatSession();
            session.CompleteTurn("hi", "hello!");
            CanvasSnapshot snap = SampleSnapshot();
            RhinoViewportSnapshot viewport = SampleViewport();
            string payload = ChatPromptBuilder.BuildConversationalPayload(session, snap, "how are you?", viewport);
            Assert.Contains("ACTIVE CANVAS CONTEXT", payload);
            Assert.Contains("\"id\":\"n1\"", payload);
            Assert.Contains("\"name\":\"Slider\"", payload);
            Assert.Contains("RHINO VIEWPORT & SCENE CONTEXT:", payload);
            Assert.Contains("Perspective", payload);
            Assert.DoesNotContain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", payload);
            Assert.Contains("[guid omitted]", payload);
            Assert.EndsWith("NEW USER MESSAGE:" + Environment.NewLine + "how are you?", payload);
            foreach (CanvasNodeSnapshot n in snap.Nodes)
            {
                Assert.DoesNotContain(n.InstanceGuid.ToString(), payload);
            }
        }

        [Fact]
        public void RhinoViewportSnapshot_FormatForPrompt_AndMainBuildInjection_WorkCleanly()
        {
            Assert.Equal("No active Rhino document.", new RhinoViewportSnapshot().FormatForPrompt());

            RhinoViewportSnapshot viewport = SampleViewport();
            string formatted = viewport.FormatForPrompt();
            Assert.Contains("Document: TowerStudy.3dm, Units: Meters", formatted);
            Assert.Contains("ActiveViewport: Perspective (Perspective, DisplayMode=Shaded)", formatted);
            Assert.Contains("VisibleObjects: 4 (Brep=2, Curve=2)", formatted);
            Assert.Contains("SelectedObjects: 1 (Curve=1)", formatted);
            Assert.Contains("VisibleLayers: Default (1), Facade::Fins (3)", formatted);

            string payload = ChatPromptBuilder.Build(
                new ChatSession(),
                SampleSnapshot(),
                null,
                "extend this facade",
                Shortlist(),
                null,
                viewport);
            Assert.Contains("RHINO VIEWPORT & SCENE CONTEXT:", payload);
            Assert.Contains("TowerStudy.3dm", payload);
            Assert.DoesNotContain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", payload);
        }

        [Fact]
        public void Builders_RejectInvalidRequiredArguments()
        {
            var session = new ChatSession();
            Assert.Throws<ArgumentNullException>(() => ChatPromptBuilder.Build(null!, null, null, "u", Shortlist()));
            Assert.Throws<ArgumentNullException>(() => ChatPromptBuilder.Build(session, null, null, "u", null!));
            Assert.Throws<ArgumentException>(() => ChatPromptBuilder.Build(session, null, null, " \r\n", Shortlist()));
            Assert.Throws<ArgumentNullException>(() => ChatPromptBuilder.BuildSystem(null!, false));
            Assert.Throws<ArgumentNullException>(() => ChatPromptBuilder.EstimateTokens(null!, "p"));
            Assert.Throws<ArgumentNullException>(() => ChatPromptBuilder.EstimateTokens("s", null!));
        }

        private static JsonDocument ParseGraph(string payload)
        {
            string marker = "COMPACT GRAPH JSON:" + Environment.NewLine;
            int start = payload.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            int end = payload.IndexOf(Environment.NewLine, start, StringComparison.Ordinal);
            return JsonDocument.Parse(payload.Substring(start, end - start));
        }

        private static IReadOnlyList<ComponentRecord> Shortlist() => new[]
        {
            new ComponentRecord { ShortId = "c001", Name = "ShortlistOnlyName", ComponentGuid = Guid.NewGuid() }
        };

        private static RhinoViewportSnapshot SampleViewport() => new RhinoViewportSnapshot
        {
            HasActiveDocument = true,
            DocumentName = "TowerStudy.3dm",
            ModelUnits = "Meters",
            ActiveViewName = "Perspective",
            DisplayMode = "Shaded",
            Projection = "Perspective",
            CameraLocation = "(40.00, -35.00, 25.00)",
            CameraTarget = "(0.00, 0.00, 10.00)",
            CameraDirection = "(-0.71, 0.62, -0.32)",
            ViewportNames = new List<string> { "Perspective", "Top", "Front", "Right" },
            TotalVisibleObjects = 4,
            SelectedObjectCount = 1,
            ObjectCountsByType = new Dictionary<string, int> { { "Brep", 2 }, { "Curve", 2 } },
            SelectedCountsByType = new Dictionary<string, int> { { "Curve", 1 } },
            VisibleLayers = new List<string> { "Default (1)", "Facade::Fins (3)" },
            SceneBoundingBox = "(-10.00, -10.00, 0.00)..(10.00, 10.00, 50.00) (size 20.00x20.00x50.00)",
            SelectedBoundingBox = "(-5.00, -5.00, 0.00)..(5.00, 5.00, 0.00) (size 10.00x10.00x0.00)",
            SampleObjects = new List<string> { "[Selected] | Curve | layer=Facade::Fins | object aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" }
        };

        private static CanvasSnapshot SampleSnapshot() => new CanvasSnapshot
        {
            Nodes = new List<CanvasNodeSnapshot>
            {
                new CanvasNodeSnapshot
                {
                    Id = "s1", InstanceGuid = Guid.NewGuid(), ComponentGuid = Guid.NewGuid(), ShortId = "c001",
                    Name = "Slider", X = 100, Y = 200, Category = "private-category",
                    Settings = new NodeSettings { Min = 0, Max = 10, Value = 5, Decimals = 0 },
                    Outputs = new List<ParamRecord> { new ParamRecord { Name = "Number", TypeName = "Number" } }
                },
                new CanvasNodeSnapshot
                {
                    Id = "s2", InstanceGuid = Guid.NewGuid(), ComponentGuid = Guid.NewGuid(), ShortId = "c002", Name = "Circle",
                    Inputs = new List<ParamRecord> { new ParamRecord { Name = "Radius", TypeName = "Number" } },
                    RuntimeMessages = new List<CanvasRuntimeMessage> { new CanvasRuntimeMessage { Level = "Error", Message = "radius invalid" } }
                }
            },
            Wires = new List<CanvasWireSnapshot>
            {
                new CanvasWireSnapshot { FromNode = "s1", FromParam = "Number", ToNode = "s2", ToParam = "Radius", TargetMapping = "graft", Simplify = true }
            }
        };
    }
}