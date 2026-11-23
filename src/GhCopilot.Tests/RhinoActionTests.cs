using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Host;
using GhCopilot.Core.Index;
using GhCopilot.Core.Llm;
using GhCopilot.Core.Rhino;
using GhCopilot.Core.Settings;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class RhinoActionTests
    {
        [Fact]
        public void RhinoActionSchema_BuildsValidObjectSchemaWithOpsAndExplanation()
        {
            JsonElement schema = RhinoActionSchema.Build();

            Assert.Equal(JsonValueKind.Object, schema.ValueKind);
            Assert.Equal("object", schema.GetProperty("type").GetString());

            JsonElement props = schema.GetProperty("properties");
            Assert.True(props.TryGetProperty("ops", out JsonElement opsProp));
            Assert.True(props.TryGetProperty("explanation", out JsonElement explanationProp));
            Assert.Equal("array", opsProp.GetProperty("type").GetString());
            Assert.Equal("string", explanationProp.GetProperty("type").GetString());

            JsonElement opEnum = opsProp.GetProperty("items").GetProperty("properties").GetProperty("op").GetProperty("enum");
            var enumValues = new List<string>();
            foreach (JsonElement item in opEnum.EnumerateArray())
            {
                enumValues.Add(item.GetString()!);
            }

            Assert.Equal(RhinoActionPlanValidator.SupportedOpNames.Count, enumValues.Count);
            Assert.Contains("add_box", enumValues);
            Assert.Contains("set_layer", enumValues);
            Assert.Contains("set_color", enumValues);
            Assert.Contains("set_display_mode", enumValues);
            Assert.Contains("array_linear", enumValues);
            Assert.Contains("zoom", enumValues);

            var required = new List<string>();
            foreach (JsonElement req in schema.GetProperty("required").EnumerateArray())
            {
                required.Add(req.GetString()!);
            }
            Assert.Contains("ops", required);
            Assert.Contains("explanation", required);
        }

        [Fact]
        public void Validator_AcceptsValidCreateTransformStyleAndViewportPlan()
        {
            var plan = new RhinoActionPlan
            {
                Explanation = "Create architectural bay with columns, array them, color red, and zoom shaded.",
                Ops = new List<RhinoActionOp>
                {
                    new RhinoActionOp { Op = "create_layer", Layer = "Architecture::Columns", Color = new[] { 16, 185, 129 }, Visible = true },
                    new RhinoActionOp { Op = "add_box", Origin = new[] { 0.0, 0.0, 0.0 }, Width = 20.0, Depth = 10.0, Height = 1.5, Layer = "Architecture", Name = "Plinth" },
                    new RhinoActionOp { Op = "add_cylinder", Origin = new[] { 2.0, 2.0, 1.5 }, Radius = 0.5, Height = 4.0, Capped = true, Layer = "Architecture::Columns" },
                    new RhinoActionOp { Op = "array_linear", Target = "last_created", Count = 4, Vector = new[] { 5.0, 0.0, 0.0 } },
                    new RhinoActionOp { Op = "set_color", Target = "layer:Architecture::Columns", Color = new[] { 220, 38, 38 } },
                    new RhinoActionOp { Op = "set_layer", Target = "selected", Layer = "Architecture::Modified" },
                    new RhinoActionOp { Op = "rotate", Target = "type:brep", AngleDegrees = 45.0, Center = new[] { 0.0, 0.0, 0.0 }, Axis = new[] { 0.0, 0.0, 1.0 } },
                    new RhinoActionOp { Op = "scale", Target = "name:Plinth", Factor = 1.25 },
                    new RhinoActionOp { Op = "set_display_mode", DisplayMode = "Shaded" },
                    new RhinoActionOp { Op = "set_view", ViewName = "Perspective" },
                    new RhinoActionOp { Op = "set_camera", CameraLocation = new[] { 40.0, -40.0, 30.0 }, CameraTarget = new[] { 10.0, 5.0, 3.0 } },
                    new RhinoActionOp { Op = "zoom", Scope = "extents" }
                }
            };

            RhinoActionValidationResult validation = RhinoActionPlanValidator.Validate(plan);

            Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
            Assert.Empty(validation.Errors);
        }

        [Fact]
        public void Validator_RejectsUnsupportedOp_NegativeRadius_NaNCoord_ExcessiveArrayCount_InvalidRgb_InvalidTarget_IdenticalCamera()
        {
            var plan = new RhinoActionPlan
            {
                Ops = new List<RhinoActionOp>
                {
                    new RhinoActionOp { Op = "nuke_everything" },
                    new RhinoActionOp { Op = "add_sphere", Center = new[] { double.NaN, 0.0, 0.0 }, Radius = -3.0 },
                    new RhinoActionOp { Op = "array_linear", Count = 500, Vector = new[] { 10.0, 0.0, 0.0 } },
                    new RhinoActionOp { Op = "set_color", Target = "bogus_selector", Color = new[] { 300, -1, 128 } },
                    new RhinoActionOp { Op = "set_camera", CameraLocation = new[] { 5.0, 5.0, 5.0 }, CameraTarget = new[] { 5.0, 5.0, 5.0 } }
                }
            };

            RhinoActionValidationResult validation = RhinoActionPlanValidator.Validate(plan);

            Assert.False(validation.IsValid);
            Assert.Contains(validation.Errors, e => e.Contains("Unsupported", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("Radius", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("finite", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("200", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("RGB", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("Invalid target", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(validation.Errors, e => e.Contains("identical", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(CopilotWorkMode.Auto, "create a box 10x10x10", false, false, CopilotWorkMode.RhinoDirect)]
        [InlineData(CopilotWorkMode.Auto, "create a circle with a radius slider", false, false, CopilotWorkMode.Grasshopper)]
        [InlineData(CopilotWorkMode.Auto, "create a box 10x10x10", true, false, CopilotWorkMode.Grasshopper)]
        [InlineData(CopilotWorkMode.Auto, "switch viewport to shaded and zoom extents", true, false, CopilotWorkMode.RhinoDirect)]
        [InlineData(CopilotWorkMode.Auto, "create layer Walls and paint selected red", true, false, CopilotWorkMode.RhinoDirect)]
        [InlineData(CopilotWorkMode.RhinoDirect, "create a circle with a radius slider", true, true, CopilotWorkMode.RhinoDirect)]
        [InlineData(CopilotWorkMode.Grasshopper, "switch viewport to shaded", false, false, CopilotWorkMode.Grasshopper)]
        [InlineData(CopilotWorkMode.Auto, "color it red", false, true, CopilotWorkMode.Grasshopper)]
        public void ResolveEffectiveMode_RoutesAutoRhinoAndGrasshopperCorrectly(
            CopilotWorkMode selected,
            string prompt,
            bool isGhOpen,
            bool useRhinoSelection,
            CopilotWorkMode expected)
        {
            CopilotWorkMode actual = RhinoPromptBuilder.ResolveEffectiveMode(selected, prompt, isGhOpen, useRhinoSelection);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void RhinoPromptBuilder_IncludesViewportAndRedactsGuids()
        {
            var session = new ChatSession();
            session.CompleteTurn("hi", "hello from aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var viewport = new RhinoViewportSnapshot
            {
                HasActiveDocument = true,
                DocumentName = "TowerModel.3dm",
                ModelUnits = "Meters",
                ActiveViewName = "Perspective",
                DisplayMode = "Shaded",
                Projection = "Perspective",
                CameraLocation = "(35.00, -45.00, 25.00)",
                CameraTarget = "(0.00, 0.00, 10.00)",
                CameraDirection = "(-0.60, 0.70, -0.38)",
                TotalVisibleObjects = 4,
                SelectedObjectCount = 1,
                VisibleLayers = new List<string> { "Default (4)" }
            };

            string payload = RhinoPromptBuilder.BuildPayload(
                session,
                viewport,
                "move selected up 5m",
                new[] { "Extrusion \"Core\" (layer=Default)" });

            Assert.Contains("RHINO VIEWPORT & SCENE CONTEXT:", payload);
            Assert.Contains("TowerModel.3dm", payload);
            Assert.Contains("Units: Meters", payload);
            Assert.Contains("DisplayMode=Shaded", payload);
            Assert.Contains("LAST CREATED OBJECTS IN THIS SESSION", payload);
            Assert.Contains("Extrusion \"Core\"", payload);
            Assert.Contains("NEW USER MESSAGE:", payload);
            Assert.DoesNotContain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", payload);
            Assert.Contains("[guid omitted]", payload);

            string sys = RhinoPromptBuilder.BuildSystem();
            Assert.Contains("add_box", sys);
            Assert.Contains("set_display_mode", sys);
            Assert.Contains("last_created", sys);
        }

        [Theory]
        [InlineData("zoom extents", false)]
        [InlineData("switch to shaded", false)]
        [InlineData("hide selected", false)]
        [InlineData("color selected red", false)]
        [InlineData("create layer Walls", false)]
        [InlineData("rename to Column", false)]
        [InlineData("show all hidden objects", false)]
        [InlineData("select all curves", false)]
        [InlineData("what is in my viewport?", true)]
        [InlineData("what layers are visible?", true)]
        [InlineData("how many objects are selected?", true)]
        public void ChatPromptBuilder_IsConversationalOnlyPrompt_DistinguishesRhinoCommandsFromQuestions(
            string prompt,
            bool expectedConversational)
        {
            Assert.Equal(expectedConversational, ChatPromptBuilder.IsConversationalOnlyPrompt(prompt));
        }

        [Fact]
        public void AppSettings_DefaultTimeoutIs180_AndUpgradesLegacy45SecondSetting()
        {
            var fresh = new AppSettings();
            Assert.Equal(180, fresh.RequestTimeoutSeconds);
            Assert.True(fresh.PlanFirstMode);

            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTimeout_" + Guid.NewGuid().ToString("N"));
            string settingsPath = Path.Combine(tempDir, "settings.json");
            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(settingsPath, "{\"provider\":\"Gemini\",\"modelId\":\"gemini-2.5-flash\",\"requestTimeoutSeconds\":45}", Encoding.UTF8);
                AppSettings upgraded = AppSettings.Load(settingsPath);
                Assert.Equal(180, upgraded.RequestTimeoutSeconds);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void PlanFirst_DetectsNewRequests_Approvals_AndFollowUpAlterations()
        {
            var session = new ChatSession();

            Assert.True(ChatPromptBuilder.ShouldPlanBeforeBuilding("design a pavilion canopy with columns", session, planFirstEnabled: true));
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("design a pavilion canopy with columns", session, planFirstEnabled: false));
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("hi there", session, planFirstEnabled: true));

            Assert.True(ChatPromptBuilder.IsApprovalOrBuildConfirmation("yes"));
            Assert.True(ChatPromptBuilder.IsApprovalOrBuildConfirmation("looks good, build it"));
            Assert.True(ChatPromptBuilder.IsApprovalOrBuildConfirmation("[Approve & Build] Yes, approved — build the planned design now."));
            Assert.False(ChatPromptBuilder.IsApprovalOrBuildConfirmation("create a twisted tower"));

            session.PendingPlanSummary = "Step 1: create 20x20x10 plinth; Step 2: 4 columns";
            session.CompleteTurn("design a pavilion", session.PendingPlanSummary);
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("Approve & Build", session, planFirstEnabled: true));
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("yes", session, planFirstEnabled: true));

            string effective = ChatPromptBuilder.BuildEffectiveExecutionPrompt("yes", session);
            Assert.Contains("USER APPROVED THIS PLAN", effective);
            Assert.Contains("Step 1: create 20x20x10 plinth", effective);
            Assert.Contains("design a pavilion", effective);

            string retrieval = ChatPromptBuilder.BuildRetrievalQuery(session, "yes");
            Assert.Contains("plinth", retrieval);

            session.PendingPlanSummary = string.Empty;
            session.HasBuiltInCurrentSession = true;
            session.LastExecutedSummary = "Built pavilion plinth and 4 columns";
            Assert.True(ChatPromptBuilder.IsFollowUpAlterationPrompt("now make it taller and color it red"));
            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding("now make it taller and color it red", session, planFirstEnabled: true));

            string followUp = ChatPromptBuilder.BuildEffectiveExecutionPrompt("now make it taller", session);
            Assert.Contains("FOLLOW-UP ALTERATION TO EXISTING WORK", followUp);
            Assert.Contains("Built pavilion plinth", followUp);

            string planSystem = ChatPromptBuilder.BuildPlanAndClarifySystem(CopilotWorkMode.RhinoDirect);
            Assert.Contains("```diagram", planSystem);
        }

        [Fact]
        public void RhinoViewportSnapshot_BuildDiffSummary_ReportsBeforeAfterChanges_AndIgnoresImageInJson()
        {
            var before = new RhinoViewportSnapshot
            {
                HasActiveDocument = true,
                TotalVisibleObjects = 2,
                ActiveViewName = "Perspective",
                DisplayMode = "Wireframe",
                SceneBoundingBox = "(0.00, 0.00, 0.00)..(5.00, 5.00, 5.00)"
            };

            var after = new RhinoViewportSnapshot
            {
                HasActiveDocument = true,
                TotalVisibleObjects = 6,
                SelectedObjectCount = 1,
                ActiveViewName = "Perspective",
                DisplayMode = "Shaded",
                SceneBoundingBox = "(0.00, 0.00, 0.00)..(24.00, 24.00, 15.00)",
                ViewportImageBase64Png = "AAAA"
            };

            string diff = RhinoViewportSnapshot.BuildDiffSummary(before, after);
            Assert.Contains("6 visible object(s) (+4 new)", diff);
            Assert.Contains("Selected=1", diff);
            Assert.Contains("Perspective (Shaded)", diff);

            string json = JsonSerializer.Serialize(after);
            Assert.DoesNotContain("ViewportImageBase64Png", json);
            Assert.DoesNotContain("AAAA", json);
        }

        [Fact]
        public void GraphPatchValidator_AllowsTextUpdateOnPythonScriptNodes_AndRejectsUnsafeScriptCode()
        {
            var snapshot = new CanvasSnapshot
            {
                Nodes = new List<CanvasNodeSnapshot>
                {
                    new CanvasNodeSnapshot
                    {
                        Id = "s1",
                        Name = "Python 3 Script",
                        ShortId = "py3",
                        Outputs = new List<ParamRecord> { new ParamRecord { Name = "geo", NickName = "geo", TypeName = "Geometry" } }
                    }
                }
            };
            var shortlist = new List<ComponentRecord>
            {
                new ComponentRecord { ShortId = "py3", Name = "Python 3 Script" }
            };

            var validPatch = new GraphPatch
            {
                Explanation = "Double sphere radius",
                Ops = new List<GraphPatchOp>
                {
                    new GraphPatchOp
                    {
                        Op = "set_param",
                        NodeId = "s1",
                        Settings = new NodeSettings { Text = "import Rhino.Geometry as rg\ngeo = rg.Sphere(rg.Point3d.Origin, 10.0)" }
                    }
                }
            };
            Assert.True(GraphPatchValidator.Validate(validPatch, snapshot, shortlist).IsValid);

            var unsafePatch = new GraphPatch
            {
                Explanation = "Unsafe import",
                Ops = new List<GraphPatchOp>
                {
                    new GraphPatchOp
                    {
                        Op = "set_param",
                        NodeId = "s1",
                        Settings = new NodeSettings { Text = "import os\ngeo = None" }
                    }
                }
            };
            PatchValidationResult bad = GraphPatchValidator.Validate(unsafePatch, snapshot, shortlist);
            Assert.False(bad.IsValid);
            Assert.Contains(bad.Errors, e => e.Contains("Unsafe", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task GeminiClient_SupportsMultimodalImageBase64Attachment()
        {
            string? capturedBody = null;
            int callCount = 0;

            var handler = new StubHttpHandler(req =>
            {
                callCount++;
                capturedBody = req.Content != null ? req.Content.ReadAsStringAsync().Result : null;
                string responseJson = JsonSerializer.Serialize(new
                {
                    candidates = new[]
                    {
                        new { content = new { parts = new[] { new { text = "I see a shaded box in Perspective." } } } }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

            using var http = new HttpClient(handler);
            using var client = new GeminiClient(http);
            string reply = await client.GenerateTextAsync(
                "AIzaSy-TestKey-1234",
                "gemini-2.5-flash",
                "Inspect viewport.",
                "What do you see?",
                TimeSpan.FromSeconds(10),
                CancellationToken.None,
                "Gemini",
                null,
                "iVBORw0KGgo=");

            Assert.Equal(1, callCount);
            Assert.Equal("I see a shaded box in Perspective.", reply);
            Assert.NotNull(capturedBody);
            Assert.Contains("inline_data", capturedBody);
            Assert.Contains("image/png", capturedBody);
            Assert.Contains("iVBORw0KGgo=", capturedBody);
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(_responder(request));
        }
    }
}
