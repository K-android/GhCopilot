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
using GhCopilot.Core.Index;
using GhCopilot.Core.Llm;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class PythonScriptFallbackTests
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
                    Outputs = new List<ParamRecord>
                    {
                        new ParamRecord { Name = "Number", NickName = "N", TypeName = "Number", Access = "item" }
                    }
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
                    Outputs = new List<ParamRecord>
                    {
                        new ParamRecord { Name = "Result", NickName = "R", TypeName = "Number", Access = "item" }
                    }
                }
            };
        }

        [Fact]
        public void SchemaBuilder_IncludesScriptDefinition()
        {
            JsonElement schema = SchemaBuilder.BuildResponseJsonSchema(CreateShortlist());
            JsonElement scriptProp = schema.GetProperty("properties").GetProperty("script");

            Assert.Equal("array", scriptProp.GetProperty("type").ValueKind.ToString().ToLowerInvariant());
            Assert.True(scriptProp.GetProperty("properties").TryGetProperty("code", out _));
            Assert.True(scriptProp.GetProperty("properties").TryGetProperty("inputs", out _));
            Assert.True(scriptProp.GetProperty("properties").TryGetProperty("outputs", out _));
        }

        [Fact]
        public void GraphValidator_ValidSpiralStaircaseScript_PassesValidation()
        {
            var shortlist = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (ComponentRecord c in CreateShortlist())
            {
                shortlist[c.ShortId] = c;
            }

            var spec = new GraphSpec
            {
                Explanation = "Generates a parametric spiral staircase using Rhino.Geometry.",
                Nodes = new List<NodeSpec>
                {
                    new NodeSpec
                    {
                        Id = "n1",
                        Ref = "c0001",
                        X = 140,
                        Y = 180,
                        Settings = new NodeSettings { Min = 6, Max = 40, Value = 18, Decimals = 0, Nickname = "Steps" }
                    }
                },
                Wires = new List<WireSpec>
                {
                    new WireSpec { FromNode = "n1", FromParam = "", ToNode = "script", ToParam = "steps" }
                },
                Script = new ScriptSpec
                {
                    Code = """
                    import Rhino.Geometry as rg
                    import math

                    steps_count = int(steps) if steps else 16
                    treads = []
                    for i in range(steps_count):
                        angle = i * (2.0 * math.pi / 12.0)
                        pt = rg.Point3d(math.cos(angle) * 2.0, math.sin(angle) * 2.0, i * 0.2)
                        plane = rg.Plane(pt, rg.Vector3d(math.cos(angle), math.sin(angle), 0), rg.Vector3d(-math.sin(angle), math.cos(angle), 0))
                        treads.append(rg.Box(plane, rg.Interval(-0.6, 0.6), rg.Interval(-0.15, 0.15), rg.Interval(0, 0.05)))
                    """,
                    Inputs = new List<ScriptInputSpec>
                    {
                        new ScriptInputSpec { Name = "steps", Type = "int", Access = "item" }
                    },
                    Outputs = new List<ScriptOutputSpec>
                    {
                        new ScriptOutputSpec { Name = "treads", Type = "Box" }
                    }
                }
            };

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Theory]
        [InlineData("import os\na = os.listdir('.')")]
        [InlineData("import subprocess\nsubprocess.run(['cmd'])")]
        [InlineData("f = open('C:/secret.txt', 'r')")]
        [InlineData("import System.IO\na = 1")]
        [InlineData("x = eval('1+1')")]
        public void GraphValidator_UnsafeScriptImportsOrCalls_ReturnsUnsafeScriptCodeError(string unsafeCode)
        {
            var shortlist = new Dictionary<string, ComponentRecord>();
            var spec = new GraphSpec
            {
                Script = new ScriptSpec
                {
                    Code = unsafeCode,
                    Inputs = new List<ScriptInputSpec>(),
                    Outputs = new List<ScriptOutputSpec>
                    {
                        new ScriptOutputSpec { Name = "a", Type = "Generic" }
                    }
                }
            };

            ValidationResult result = GraphValidator.Validate(spec, shortlist);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Code == ValidationErrorCode.UnsafeScriptCode);
        }

        [Fact]
        public async Task RepairLoop_WhenNativeWiringFailsTwice_RoutesToScriptFallbackAndSucceeds()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            string runLogPath = Path.Combine(tempDir, "runs.jsonl");
            string auditLogPath = Path.Combine(tempDir, "sent-payloads.jsonl");

            try
            {
                int call = 0;
                var capturedPrompts = new List<string>();

                var handler = new StubHttpMessageHandler(async (req, _) =>
                {
                    call++;
                    capturedPrompts.Add(await req.Content!.ReadAsStringAsync());

                    string modelJson = call <= 2
                        ? """
                          {
                            "nodes": [
                              { "id": "n1", "ref": "c0001", "x": 150, "y": 180 },
                              { "id": "n2", "ref": "c0002", "x": 350, "y": 180 }
                            ],
                            "wires": [
                              { "fromNode": "n1", "fromParam": "Number", "toNode": "n2", "toParam": "NonExistentParam" }
                            ],
                            "script": null,
                            "explanation": "Attempted native wiring."
                          }
                          """
                        : """
                          {
                            "nodes": [],
                            "wires": [],
                            "script": {
                              "code": "import Rhino.Geometry as rg\nimport math\nstairs = [rg.Point3d(math.cos(i * 0.4) * 3.0, math.sin(i * 0.4) * 3.0, i * 0.25) for i in range(20)]",
                              "inputs": [],
                              "outputs": [
                                { "name": "stairs", "type": "Point3d" }
                              ]
                            },
                            "explanation": "Created spiral staircase points using Python 3 fallback."
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

                RepairLoopResult result = await RepairLoop.GenerateAndValidateAsync(
                    client,
                    "secret-key",
                    "gemini-3.6-flash",
                    "make a spiral staircase",
                    CreateShortlist(),
                    logFilePath: runLogPath,
                    auditLogPath: auditLogPath);

                Assert.True(result.IsSuccess);
                Assert.Equal(3, result.AttemptsUsed);
                Assert.NotNull(result.Spec);
                Assert.NotNull(result.Spec!.Script);
                Assert.Single(result.Spec.Script!.Outputs);
                Assert.Equal("stairs", result.Spec.Script.Outputs[0].Name);
                // Attempt 3 prompt must have been routed through BuildScriptFallbackPrompt.
                Assert.Contains("Native component wiring failed validation twice", capturedPrompts[2]);
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
