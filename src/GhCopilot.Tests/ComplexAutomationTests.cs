using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Index;
using GhCopilot.Core.Llm;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class ComplexAutomationTests
    {
        private static List<ComponentRecord> CreateShortlist()
        {
            return new List<ComponentRecord>
            {
                new ComponentRecord
                {
                    ShortId = "c0001",
                    Name = "Number Slider",
                    Category = "Params",
                    SubCategory = "Input",
                    IsStandaloneParam = true,
                    IsHydrated = true,
                    Outputs = new List<ParamRecord> { new ParamRecord { Name = "Number", NickName = "N", TypeName = "Number", Access = "item" } }
                },
                new ComponentRecord
                {
                    ShortId = "c0002",
                    Name = "Addition",
                    Category = "Maths",
                    SubCategory = "Operators",
                    IsHydrated = true,
                    Inputs = new List<ParamRecord>
                    {
                        new ParamRecord { Name = "A", NickName = "A", TypeName = "Number", Access = "item" },
                        new ParamRecord { Name = "B", NickName = "B", TypeName = "Number", Access = "item" }
                    },
                    Outputs = new List<ParamRecord> { new ParamRecord { Name = "Result", NickName = "R", TypeName = "Number", Access = "item" } }
                },
                new ComponentRecord
                {
                    ShortId = "c0003",
                    Name = "Circle",
                    Category = "Curve",
                    SubCategory = "Primitive",
                    IsHydrated = true,
                    Inputs = new List<ParamRecord>
                    {
                        new ParamRecord { Name = "Plane", NickName = "P", TypeName = "Plane", Access = "item", Optional = true },
                        new ParamRecord { Name = "Radius", NickName = "R", TypeName = "Number", Access = "item" }
                    },
                    Outputs = new List<ParamRecord> { new ParamRecord { Name = "Circle", NickName = "C", TypeName = "Circle", Access = "item" } }
                }
            };
        }

        [Fact]
        public void GraphAutoLayout_AssignsTopologicalLayersAndEliminatesOverlaps()
        {
            // All nodes stacked at overlapping (0,0) coordinates initially.
            var spec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec { Id = "n1", Ref = "c0001", X = 0, Y = 0 },
                    new NodeSpec { Id = "n2", Ref = "c0001", X = 0, Y = 0 },
                    new NodeSpec { Id = "n3", Ref = "c0002", X = 0, Y = 0 },
                    new NodeSpec { Id = "n4", Ref = "c0003", X = 0, Y = 0 }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec { FromNode = "n1", FromParam = "", ToNode = "n3", ToParam = "A" },
                    new WireSpec { FromNode = "n2", FromParam = "", ToNode = "n3", ToParam = "B" },
                    new WireSpec { FromNode = "n3", FromParam = "Result", ToNode = "n4", ToParam = "Radius" }
                }
            };

            Assert.True(GraphAutoLayout.NeedsAutoLayout(spec));

            GraphAutoLayout.ApplyLayeredLayout(spec);

            Assert.False(GraphAutoLayout.NeedsAutoLayout(spec));
            Assert.Equal(spec.Nodes[0].X, spec.Nodes[1].X);
            Assert.NotEqual(spec.Nodes[0].Y, spec.Nodes[1].Y);
            Assert.True(spec.Nodes[2].X > spec.Nodes[0].X);
            Assert.True(spec.Nodes[3].X > spec.Nodes[2].X);
        }

        [Fact]
        public void GraphValidator_AllowsExternalSelectionWiresAndValidatesDataTreeModifiers()
        {
            var shortlist = CreateShortlist().ToDictionary(c => c.ShortId, StringComparer.OrdinalIgnoreCase);

            var selectedSnapshot = new CanvasSnapshot
            {
                Nodes = new List<CanvasNodeSnapshot>
                {
                    new CanvasNodeSnapshot
                    {
                        Id = "s1",
                        InstanceGuid = Guid.NewGuid(),
                        Name = "Number Slider",
                        IsSelected = true,
                        Outputs = new List<ParamRecord>
                        {
                            new ParamRecord { Name = "Number", TypeName = "Number", Access = "item" }
                        }
                    }
                }
            };

            var externalNodes = selectedSnapshot.BuildExternalSelectionRecordMap();

            var validSpec = new GraphSpec
            {
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec
                    {
                        Id = "n1",
                        Ref = "c0003",
                        X = 360,
                        Y = 180,
                        Settings = new NodeSettings
                        {
                            SimplifyInputs = new List<string> { "Radius" }
                        }
                    }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec
                    {
                        FromNode = "$selected",
                        FromParam = "Number",
                        ToNode = "n1",
                        ToParam = "Radius",
                        TargetMapping = "graft",
                        Simplify = true
                    }
                }
            };

            ValidationResult ok = GraphValidator.Validate(validSpec, shortlist, externalNodes);
            Assert.True(ok.IsValid);

            // Invalid targetMapping must fail validation.
            validSpec.Wires[0].TargetMapping = "invalid_mode";
            ValidationResult badMapping = GraphValidator.Validate(validSpec, shortlist, externalNodes);
            Assert.False(badMapping.IsValid);
        }

        [Fact]
        public void RecipeStore_CreatesRecipeFromCanvasSnapshot_SavesLoadsAndRetrievesForPrompt()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotRecipe_" + Guid.NewGuid().ToString("N"));
            string recipesPath = Path.Combine(tempDir, "recipes.json");

            try
            {
                var snapshot = new CanvasSnapshot
                {
                    Nodes = new List<CanvasNodeSnapshot>
                    {
                        new CanvasNodeSnapshot
                        {
                            Id = "s1",
                            ShortId = "c0001",
                            Name = "Number Slider",
                            X = 100,
                            Y = 100,
                            IsSelected = true,
                            Settings = new NodeSettings { Min = 1, Max = 50, Value = 10, Decimals = 1 }
                        },
                        new CanvasNodeSnapshot
                        {
                            Id = "s2",
                            ShortId = "c0003",
                            Name = "Circle",
                            X = 100,
                            Y = 100,
                            IsSelected = true
                        }
                    },
                    Wires = new List<CanvasWireSnapshot>
                    {
                        new CanvasWireSnapshot
                        {
                            FromNode = "s1",
                            FromParam = "",
                            ToNode = "s2",
                            ToParam = "Radius",
                            TargetMapping = "graft"
                        }
                    }
                };

                RecipeRecord recipe = RecipeStore.CreateFromCanvasSnapshot(
                    "Facade Ring Pattern",
                    "Creates concentric facade ring circles from a radius slider",
                    snapshot);

                Assert.Equal(2, recipe.Spec.Nodes.Count);
                Assert.Single(recipe.Spec.Wires);
                Assert.Equal("graft", recipe.Spec.Wires[0].TargetMapping);
                // AutoLayout separated overlapping nodes into distinct layers.
                Assert.True(recipe.Spec.Nodes[1].X > recipe.Spec.Nodes[0].X);

                RecipeStore.SaveRecipe(recipe, recipesPath);
                List<RecipeRecord> loaded = RecipeStore.LoadAll(recipesPath);
                Assert.Single(loaded);

                IReadOnlyList<RecipeRecord> matches = RecipeStore.RetrieveMatchingRecipes(loaded, "build facade ring circles", maxRecipes: 2);
                Assert.Single(matches);
                Assert.Equal("Facade Ring Pattern", matches[0].Name);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task GeminiClient_MultiProviderByok_FormatsAnthropicAndOpenAiRequestsWithoutKeyLeak()
        {
            string? capturedAnthropicUrl = null;
            string? capturedAnthropicKeyHeader = null;
            string? capturedOpenAiUrl = null;
            string? capturedOpenAiAuthHeader = null;

            var handler = new StubHttpMessageHandler((req, _) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("anthropic.com"))
                {
                    capturedAnthropicUrl = url;
                    capturedAnthropicKeyHeader = req.Headers.GetValues("x-api-key").Single();
                    string anthropicResponse = JsonSerializer.Serialize(new
                    {
                        content = new[]
                        {
                            new { type = "text", text = """{"nodes":[],"wires":[],"explanation":"Claude test"}""" }
                        }
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(anthropicResponse, Encoding.UTF8, "application/json")
                    });
                }

                capturedOpenAiUrl = url;
                capturedOpenAiAuthHeader = req.Headers.GetValues("Authorization").Single();
                string openAiResponse = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new { message = new { content = """{"nodes":[],"wires":[],"explanation":"OpenAI test"}""" } }
                    }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(openAiResponse, Encoding.UTF8, "application/json")
                });
            });

            using var http = new HttpClient(handler);
            using var client = new GeminiClient(http);
            JsonElement schema = SchemaBuilder.BuildResponseJsonSchema(CreateShortlist());

            string claudeJson = await client.GenerateGraphJsonAsync(
                "sk-ant-test-key",
                "claude-opus-4-6",
                "sys",
                "user",
                schema,
                provider: "Anthropic");

            Assert.Contains("Claude test", claudeJson);
            Assert.Equal("https://api.anthropic.com/v1/messages", capturedAnthropicUrl);
            Assert.Equal("sk-ant-test-key", capturedAnthropicKeyHeader);

            string openAiJson = await client.GenerateGraphJsonAsync(
                "sk-openai-test-key",
                "gpt-4.1",
                "sys",
                "user",
                schema,
                provider: "OpenAI");

            Assert.Contains("OpenAI test", openAiJson);
            Assert.Equal("https://api.openai.com/v1/chat/completions", capturedOpenAiUrl);
            Assert.Equal("Bearer sk-openai-test-key", capturedOpenAiAuthHeader);
        }

        [Fact]
        public async Task RepairLoop_AppliesAutoLayoutAndIncludesMatchingRecipesAndSelectedContext()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotAuto_" + Guid.NewGuid().ToString("N"));
            string runLog = Path.Combine(tempDir, "runs.jsonl");
            string auditLog = Path.Combine(tempDir, "sent-payloads.jsonl");

            try
            {
                string? sentPrompt = null;
                var handler = new StubHttpMessageHandler(async (req, _) =>
                {
                    sentPrompt = await req.Content!.ReadAsStringAsync();

                    // Return stacked (0,0) nodes wired to $selected to verify AutoLayout + $selected validation.
                    string modelJson = """
                    {
                      "nodes": [
                        { "id": "n1", "ref": "c0003", "x": 0, "y": 0 }
                      ],
                      "wires": [
                        { "fromNode": "$selected", "fromParam": "Number", "toNode": "n1", "toParam": "Radius", "targetMapping": "graft" }
                      ],
                      "explanation": "Wired circle radius to selected slider with graft."
                    }
                    """;

                    string envelope = JsonSerializer.Serialize(new
                    {
                        candidates = new[]
                        {
                            new { content = new { parts = new[] { new { text = modelJson } } } }
                        }
                    });

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(envelope, Encoding.UTF8, "application/json")
                    };
                });

                using var http = new HttpClient(handler);
                using var client = new GeminiClient(http);

                var selectedContext = new CanvasSnapshot
                {
                    Nodes = new List<CanvasNodeSnapshot>
                    {
                        new CanvasNodeSnapshot
                        {
                            Id = "s1",
                            InstanceGuid = Guid.NewGuid(),
                            Name = "Radius Slider",
                            IsSelected = true,
                            Outputs = new List<ParamRecord> { new ParamRecord { Name = "Number", TypeName = "Number", Access = "item" } }
                        }
                    }
                };

                var recipes = new List<RecipeRecord>
                {
                    new RecipeRecord
                    {
                        Name = "Circle Pattern",
                        Description = "Circle from selected radius",
                        Spec = new GraphSpec { Explanation = "Sample circle recipe" }
                    }
                };

                RepairLoopResult result = await RepairLoop.GenerateAndValidateAsync(
                    client,
                    "secret-key",
                    "gemini-3.6-flash",
                    "create circle from selected slider",
                    CreateShortlist(),
                    logFilePath: runLog,
                    auditLogPath: auditLog,
                    matchingRecipes: recipes,
                    selectedContext: selectedContext,
                    autoLayout: true);

                Assert.True(result.IsSuccess);
                Assert.NotNull(sentPrompt);
                Assert.Contains("SELECTED CANVAS OUTPUTS AVAILABLE FOR WIRING", sentPrompt!);
                Assert.Contains("MATCHING SAVED RECIPES", sentPrompt!);
                Assert.Equal("graft", result.Spec!.Wires[0].TargetMapping);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

            public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _responder(request, cancellationToken);
            }
        }
    }
}
