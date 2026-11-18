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
using GhCopilot.Core.Settings;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class CanvasAwarenessTests
    {
        private static CanvasSnapshot CreateSampleSnapshotWithNeighbor()
        {
            return new CanvasSnapshot
            {
                Nodes = new List<CanvasNodeSnapshot>
                {
                    new CanvasNodeSnapshot
                    {
                        Id = "s1",
                        InstanceGuid = Guid.NewGuid(),
                        ComponentGuid = Guid.NewGuid(),
                        ShortId = "c0001",
                        Name = "Number Slider",
                        X = 120,
                        Y = 180,
                        IsSelected = true,
                        Settings = new NodeSettings { Min = 0, Max = 50, Value = 0, Decimals = 1 }
                    },
                    new CanvasNodeSnapshot
                    {
                        Id = "s2",
                        InstanceGuid = Guid.NewGuid(),
                        ComponentGuid = Guid.NewGuid(),
                        ShortId = "c0002",
                        Name = "Circle",
                        X = 320,
                        Y = 180,
                        IsSelected = true,
                        RuntimeMessages = new List<CanvasRuntimeMessage>
                        {
                            new CanvasRuntimeMessage { Level = "Error", Message = "Circle radius must be greater than zero." }
                        }
                    },
                    new CanvasNodeSnapshot
                    {
                        Id = "ctx1",
                        InstanceGuid = Guid.NewGuid(),
                        ComponentGuid = Guid.NewGuid(),
                        ShortId = "c0003",
                        Name = "Extrude",
                        X = 520,
                        Y = 180,
                        IsSelected = false
                    }
                },
                Wires = new List<CanvasWireSnapshot>
                {
                    new CanvasWireSnapshot { FromNode = "s1", FromParam = "", ToNode = "s2", ToParam = "Radius" },
                    new CanvasWireSnapshot { FromNode = "s2", FromParam = "Circle", ToNode = "ctx1", ToParam = "Base" }
                }
            };
        }

        [Fact]
        public void CanvasSnapshot_FormatForPrompt_RespectsSendCanvasDataToggle_AndIncludesRuntimeMessages()
        {
            CanvasSnapshot snapshot = CreateSampleSnapshotWithNeighbor();

            string selectedOnly = snapshot.FormatForPrompt(includeNonSelectedContext: false);
            Assert.Contains("s1 [selected]", selectedOnly);
            Assert.Contains("s2 [selected]", selectedOnly);
            Assert.Contains("[Error] Circle radius must be greater than zero.", selectedOnly);
            Assert.Contains("s1. -> s2.Radius", selectedOnly);
            Assert.DoesNotContain("ctx1", selectedOnly);
            Assert.DoesNotContain("Extrude", selectedOnly);

            string withContext = snapshot.FormatForPrompt(includeNonSelectedContext: true);
            Assert.Contains("ctx1 [neighbor-context]", withContext);
            Assert.Contains("Extrude", withContext);
            Assert.Contains("s2.Circle -> ctx1.Base", withContext);
        }

        [Fact]
        public void PayloadAuditLogger_WritesExactSentPayloadToJsonl()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            string auditPath = Path.Combine(tempDir, "sent-payloads.jsonl");

            try
            {
                PayloadAuditLogger.LogSentPayload(
                    action: "ExplainSelection",
                    modelId: "gemini-3.6-flash",
                    sendCanvasDataEnabled: false,
                    systemInstruction: "Explain clearly.",
                    promptText: "NODES:\ns1 [selected] | Circle",
                    filePath: auditPath);

                Assert.True(File.Exists(auditPath));
                string[] lines = File.ReadAllLines(auditPath);
                Assert.Single(lines);

                PayloadAuditEntry? entry = JsonSerializer.Deserialize<PayloadAuditEntry>(lines[0]);
                Assert.NotNull(entry);
                Assert.Equal("ExplainSelection", entry!.Action);
                Assert.Equal("gemini-3.6-flash", entry.ModelId);
                Assert.False(entry.SendCanvasDataEnabled);
                Assert.Equal("Explain clearly.", entry.SystemInstruction);
                Assert.Contains("s1 [selected] | Circle", entry.PromptText);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task ExplainSelection_CallsGeminiTextEndpointAndGeneratesExplanation()
        {
            CanvasSnapshot snapshot = CreateSampleSnapshotWithNeighbor();
            string prompt = PromptBuilder.BuildExplainSelectionPrompt(snapshot, "Why is it red?", includeNonSelectedContext: false);

            Assert.Contains("Why is it red?", prompt);
            Assert.DoesNotContain("Extrude", prompt);

            string? capturedRequestJson = null;
            var handler = new StubHttpMessageHandler(async (req, _) =>
            {
                capturedRequestJson = await req.Content!.ReadAsStringAsync();
                string responseJson = JsonSerializer.Serialize(new
                {
                    candidates = new[]
                    {
                        new
                        {
                            content = new
                            {
                                parts = new[]
                                {
                                    new { text = "The slider value is 0, causing Circle to fail with a zero-radius error." }
                                }
                            }
                        }
                    }
                });

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

            using var http = new HttpClient(handler);
            using var client = new GeminiClient(http);

            string explanation = await client.GenerateTextAsync(
                "secret-key",
                "gemini-3.6-flash",
                PromptBuilder.BuildExplainSystemInstruction(),
                prompt);

            Assert.Contains("zero-radius error", explanation);
            Assert.NotNull(capturedRequestJson);
            Assert.DoesNotContain("responseJsonSchema", capturedRequestJson!);
        }

        [Fact]
        public async Task FixSelection_ValidatesReplacementGraphAndLogsAuditEntry()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            string runLogPath = Path.Combine(tempDir, "runs.jsonl");
            string auditLogPath = Path.Combine(tempDir, "sent-payloads.jsonl");

            try
            {
                CanvasSnapshot snapshot = CreateSampleSnapshotWithNeighbor();
                var shortlist = new List<ComponentRecord>
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
                        Name = "Circle",
                        Category = "Curve",
                        SubCategory = "Primitive",
                        IsHydrated = true,
                        Inputs = new List<ParamRecord>
                        {
                            new ParamRecord { Name = "Plane", NickName = "P", TypeName = "Plane", Access = "item", Optional = true },
                            new ParamRecord { Name = "Radius", NickName = "R", TypeName = "Number", Access = "item" }
                        },
                        Outputs = new List<ParamRecord>
                        {
                            new ParamRecord { Name = "Circle", NickName = "C", TypeName = "Circle", Access = "item" }
                        }
                    }
                };

                string replacementJson = """
                {
                  "nodes": [
                    { "id": "n1", "ref": "c0001", "x": 120, "y": 180, "settings": { "min": 1, "max": 50, "value": 5, "decimals": 1 } },
                    { "id": "n2", "ref": "c0002", "x": 320, "y": 180 }
                  ],
                  "wires": [
                    { "fromNode": "n1", "fromParam": "", "toNode": "n2", "toParam": "Radius" }
                  ],
                  "explanation": "Raised slider minimum and value to 5 so Circle has a positive radius."
                }
                """;

                string envelope = JsonSerializer.Serialize(new
                {
                    candidates = new[]
                    {
                        new { content = new { parts = new[] { new { text = replacementJson } } } }
                    }
                });

                var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(envelope, Encoding.UTF8, "application/json")
                }));

                using var http = new HttpClient(handler);
                using var client = new GeminiClient(http);

                RepairLoopResult result = await RepairLoop.FixSelectionAndValidateAsync(
                    client,
                    "secret-key",
                    "gemini-3.6-flash",
                    snapshot,
                    userHint: "Fix zero radius",
                    includeNonSelectedContext: false,
                    shortlist,
                    logFilePath: runLogPath,
                    auditLogPath: auditLogPath);

                Assert.True(result.IsSuccess);
                Assert.NotNull(result.Spec);
                Assert.Equal(2, result.Spec!.Nodes.Count);
                Assert.True(File.Exists(auditLogPath));
                string auditText = File.ReadAllText(auditLogPath);
                Assert.Contains("FixSelection", auditText);
                Assert.DoesNotContain("Extrude", auditText);
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
