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
using GhCopilot.Core.Settings;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class GeminiLoopTests
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
        }

        private static string WrapGeminiCandidateText(string text)
        {
            return JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new
                        {
                            parts = new[]
                            {
                                new { text }
                            }
                        }
                    }
                }
            });
        }

        [Fact]
        public void SchemaBuilder_IncludesShortlistEnumAndRequiredProperties()
        {
            List<ComponentRecord> shortlist = CreateShortlist();
            JsonElement schema = SchemaBuilder.BuildResponseJsonSchema(shortlist);

            JsonElement refEnum = schema
                .GetProperty("properties")
                .GetProperty("nodes")
                .GetProperty("items")
                .GetProperty("properties")
                .GetProperty("ref")
                .GetProperty("enum");

            var ids = refEnum.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(new[] { "c0001", "c0002" }, ids);
        }

        [Fact]
        public void PromptBuilder_BuildsCompactShortlistContextLines()
        {
            List<ComponentRecord> shortlist = CreateShortlist();
            string prompt = PromptBuilder.BuildUserPrompt("circle with a radius slider", shortlist);

            Assert.Contains("USER REQUEST:\r\ncircle with a radius slider", prompt.Replace("\r\n", "\r\n"));
            Assert.Contains("c0001 | Number Slider | Params>Input", prompt);
            Assert.Contains("c0002 | Circle | Curve>Primitive | in: Plane(Plane,item) Radius(Number,item) | out: Circle(Circle)", prompt);
        }

        [Fact]
        public async Task GeminiClient_ListModelsAsync_VerifiesConfiguredModel()
        {
            var handler = new StubHttpMessageHandler((req, _) =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.True(req.Headers.TryGetValues("x-goog-api-key", out var headerValues));
                Assert.Equal("secret-test-key", headerValues!.Single());
                Assert.DoesNotContain("secret-test-key", req.RequestUri!.ToString());

                string json = """
                {
                  "models": [
                    { "name": "models/gemini-2.5-flash" },
                    { "name": "models/gemini-3.6-flash" }
                  ]
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            });

            using var http = new HttpClient(handler);
            using var client = new GeminiClient(http);

            (bool available, string okMsg) = await client.VerifyConfiguredModelAsync("secret-test-key", "gemini-3.6-flash");
            Assert.True(available);
            Assert.Contains("verified", okMsg);

            (bool missing, string warnMsg) = await client.VerifyConfiguredModelAsync("secret-test-key", "gemini-nonexistent");
            Assert.False(missing);
            Assert.Contains("Warning:", warnMsg);
        }

        [Fact]
        public async Task GeminiClient_GenerateGraphJsonAsync_SendsResponseJsonSchemaAndHeaderWithoutKeyInUrl()
        {
            string? capturedBody = null;
            var handler = new StubHttpMessageHandler(async (req, _) =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.DoesNotContain("my-secret-key", req.RequestUri!.ToString());
                Assert.Equal("my-secret-key", req.Headers.GetValues("x-goog-api-key").Single());
                capturedBody = await req.Content!.ReadAsStringAsync();

                string validGraph = """
                {"nodes":[{"id":"n1","ref":"c0001","x":150,"y":180,"settings":{"min":1,"max":50,"value":10,"decimals":1}}],"wires":[],"explanation":"Slider"}
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(WrapGeminiCandidateText(validGraph), Encoding.UTF8, "application/json")
                };
            });

            using var http = new HttpClient(handler);
            using var client = new GeminiClient(http);

            JsonElement schema = SchemaBuilder.BuildResponseJsonSchema(CreateShortlist());
            string result = await client.GenerateGraphJsonAsync(
                "my-secret-key",
                "gemini-3.6-flash",
                "sys",
                "user",
                schema);

            Assert.NotNull(capturedBody);
            using JsonDocument sentDoc = JsonDocument.Parse(capturedBody!);
            JsonElement genConfig = sentDoc.RootElement.GetProperty("generationConfig");
            Assert.Equal("application/json", genConfig.GetProperty("responseMimeType").GetString());
            Assert.True(genConfig.TryGetProperty("responseJsonSchema", out _));
            Assert.False(genConfig.TryGetProperty("responseSchema", out _));
            Assert.Contains("\"nodes\"", result);
        }

        [Fact]
        public async Task RepairLoop_ValidResponse_SucceedsOnFirstAttemptAndWritesJsonlLog()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            string logPath = Path.Combine(tempDir, "runs.jsonl");

            try
            {
                string validJson = """
                {
                  "nodes": [
                    { "id": "n1", "ref": "c0001", "x": 150, "y": 180, "settings": { "min": 1, "max": 20, "value": 5, "decimals": 1 } },
                    { "id": "n2", "ref": "c0002", "x": 350, "y": 180 }
                  ],
                  "wires": [
                    { "fromNode": "n1", "fromParam": "", "toNode": "n2", "toParam": "Radius" }
                  ],
                  "explanation": "Creates a circle controlled by a radius slider."
                }
                """;

                var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(WrapGeminiCandidateText(validJson), Encoding.UTF8, "application/json")
                }));

                using var http = new HttpClient(handler);
                using var client = new GeminiClient(http);

                RepairLoopResult result = await RepairLoop.GenerateAndValidateAsync(
                    client,
                    "secret-key",
                    "gemini-3.6-flash",
                    "circle with a radius slider",
                    CreateShortlist(),
                    logFilePath: logPath);

                Assert.True(result.IsSuccess);
                Assert.Equal(1, result.AttemptsUsed);
                Assert.NotNull(result.Spec);
                Assert.Equal(2, result.Spec!.Nodes.Count);
                Assert.Single(result.Spec.Wires);
                Assert.True(File.Exists(logPath));
                string[] logLines = File.ReadAllLines(logPath);
                Assert.Single(logLines);
                Assert.Contains("ValidFirstPass", logLines[0]);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task RepairLoop_MalformedJsonThenValid_RecoversWithinRetryLimit()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            string logPath = Path.Combine(tempDir, "runs.jsonl");

            try
            {
                int call = 0;
                var handler = new StubHttpMessageHandler((_, _) =>
                {
                    call++;
                    string text = call == 1
                        ? "{ broken json !!!"
                        : """
                          {
                            "nodes": [
                              { "id": "n1", "ref": "c0001", "x": 150, "y": 180 },
                              { "id": "n2", "ref": "c0002", "x": 350, "y": 180 }
                            ],
                            "wires": [
                              { "fromNode": "n1", "fromParam": "Number", "toNode": "n2", "toParam": "Radius" }
                            ],
                            "explanation": "Fixed JSON."
                          }
                          """;

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(WrapGeminiCandidateText(text), Encoding.UTF8, "application/json")
                    });
                });

                using var http = new HttpClient(handler);
                using var client = new GeminiClient(http);

                RepairLoopResult result = await RepairLoop.GenerateAndValidateAsync(
                    client,
                    "secret-key",
                    "gemini-3.6-flash",
                    "circle with a radius slider",
                    CreateShortlist(),
                    logFilePath: logPath);

                Assert.True(result.IsSuccess);
                Assert.Equal(2, result.AttemptsUsed);
                Assert.Equal(2, call);
                Assert.Contains("ValidAfterRepair", File.ReadAllText(logPath));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task RepairLoop_SchemaValidButSemanticallyInvalid_StopsAfterMax2Retries()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            string logPath = Path.Combine(tempDir, "runs.jsonl");

            try
            {
                int calls = 0;
                var capturedPrompts = new List<string>();
                var handler = new StubHttpMessageHandler(async (req, _) =>
                {
                    calls++;
                    capturedPrompts.Add(await req.Content!.ReadAsStringAsync());

                    // Schema-valid JSON, but bad parameter name "WrongRadiusInput" every time.
                    string badSemanticJson = """
                    {
                      "nodes": [
                        { "id": "n1", "ref": "c0001", "x": 150, "y": 180 },
                        { "id": "n2", "ref": "c0002", "x": 350, "y": 180 }
                      ],
                      "wires": [
                        { "fromNode": "n1", "fromParam": "Number", "toNode": "n2", "toParam": "WrongRadiusInput" }
                      ],
                      "explanation": "Bad param."
                    }
                    """;

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(WrapGeminiCandidateText(badSemanticJson), Encoding.UTF8, "application/json")
                    };
                });

                using var http = new HttpClient(handler);
                using var client = new GeminiClient(http);

                RepairLoopResult result = await RepairLoop.GenerateAndValidateAsync(
                    client,
                    "secret-key",
                    "gemini-3.6-flash",
                    "circle with a radius slider",
                    CreateShortlist(),
                    logFilePath: logPath);

                Assert.False(result.IsSuccess);
                Assert.Equal(3, calls); // 1 initial + 2 retries
                Assert.Equal(3, result.AttemptsUsed);
                Assert.Contains(result.Errors, e => e.Contains("WrongRadiusInput"));
                // Second prompt must contain the validator error fed back to the model.
                Assert.Contains("WrongRadiusInput", capturedPrompts[1]);
                Assert.Contains("ValidationFailed", File.ReadAllText(logPath));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Theory]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        public async Task GeminiClient_Http429And5xx_ThrowsTransientGeminiApiExceptionWithoutLeakingKey(HttpStatusCode statusCode)
        {
            const string secretKey = "AIzaSy-Secret-Value-9999";
            var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent($"Rate limit or server error echoing {secretKey}", Encoding.UTF8, "text/plain")
            }));

            using var http = new HttpClient(handler);
            using var client = new GeminiClient(http);
            JsonElement schema = SchemaBuilder.BuildResponseJsonSchema(CreateShortlist());

            GeminiApiException ex = await Assert.ThrowsAsync<GeminiApiException>(() =>
                client.GenerateGraphJsonAsync(secretKey, "gemini-3.6-flash", "sys", "user", schema));

            Assert.Equal(statusCode, ex.StatusCode);
            Assert.True(ex.IsTransient);
            Assert.DoesNotContain(secretKey, ex.Message);
            Assert.Contains("[REDACTED]", ex.Message);
        }

        [Fact]
        public async Task RepairLoop_RuntimeRepairPass_FixesRuntimeMessages()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            string logPath = Path.Combine(tempDir, "runs.jsonl");

            try
            {
                string fixedJson = """
                {
                  "nodes": [
                    { "id": "n1", "ref": "c0001", "x": 150, "y": 180, "settings": { "min": 1, "max": 50, "value": 10, "decimals": 1 } },
                    { "id": "n2", "ref": "c0002", "x": 350, "y": 180 }
                  ],
                  "wires": [
                    { "fromNode": "n1", "fromParam": "Number", "toNode": "n2", "toParam": "Radius" }
                  ],
                  "explanation": "Raised minimum radius to avoid zero radius error."
                }
                """;

                var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(WrapGeminiCandidateText(fixedJson), Encoding.UTF8, "application/json")
                }));

                using var http = new HttpClient(handler);
                using var client = new GeminiClient(http);

                var currentSpec = new GraphSpec
                {
                    Nodes = new List<NodeSpec>
                    {
                        new NodeSpec { Id = "n1", Ref = "c0001", X = 150, Y = 180, Settings = new NodeSettings { Min = 0, Max = 50, Value = 0 } },
                        new NodeSpec { Id = "n2", Ref = "c0002", X = 350, Y = 180 }
                    },
                    Wires = new List<WireSpec>
                    {
                        new WireSpec { FromNode = "n1", FromParam = "Number", ToNode = "n2", ToParam = "Radius" }
                    }
                };

                RepairLoopResult result = await RepairLoop.RepairRuntimeMessagesAsync(
                    client,
                    "secret-key",
                    "gemini-3.6-flash",
                    "circle with a radius slider",
                    CreateShortlist(),
                    currentSpec,
                    new[] { "[Error] Circle: Radius must be greater than zero." },
                    logFilePath: logPath);

                Assert.True(result.IsSuccess);
                Assert.NotNull(result.Spec);
                Assert.Contains("RuntimeRepairValid", File.ReadAllText(logPath));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void SecretStore_SavesDpapiEncryptedKeyNotPlainText_AndRoundTrips()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            string keyPath = Path.Combine(tempDir, "api-key.dpapi");
            const string sampleKey = "test-gemini-secret-abc123";

            try
            {
                SecretStore.SaveApiKey(sampleKey, keyPath);
                Assert.True(File.Exists(keyPath));

                byte[] diskBytes = File.ReadAllBytes(keyPath);
                string rawDiskText = Encoding.UTF8.GetString(diskBytes);
                Assert.DoesNotContain(sampleKey, rawDiskText);

                bool loaded = SecretStore.TryLoadApiKey(out string? decrypted, keyPath, allowEnvironmentFallback: false);
                Assert.True(loaded);
                Assert.Equal(sampleKey, decrypted);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void AppSettings_SavesAndLoadsConfiguredModelId()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            string settingsPath = Path.Combine(tempDir, "settings.json");

            try
            {
                var settings = new AppSettings
                {
                    ModelId = "gemini-2.5-pro",
                    SharedLibraryFolder = @"\\server\gh-company",
                    SendCanvasData = true,
                    RequestTimeoutSeconds = 60
                };

                settings.Save(settingsPath);
                AppSettings loaded = AppSettings.Load(settingsPath);

                Assert.Equal("gemini-2.5-pro", loaded.ModelId);
                Assert.Equal(@"\\server\gh-company", loaded.SharedLibraryFolder);
                Assert.True(loaded.SendCanvasData);
                Assert.Equal(60, loaded.RequestTimeoutSeconds);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Phase4SettingsAndSecretStore_SurviveReload_AndNeverExposePlainTextKeyOnDisk()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            string settingsPath = Path.Combine(tempDir, "settings.json");
            string keyPath = Path.Combine(tempDir, "api-key.dpapi");
            const string apiKey = "AIzaSy-Phase4-SecretKey-987654";

            try
            {
                var savedSettings = new AppSettings
                {
                    ModelId = "gemini-3.6-flash",
                    SharedLibraryFolder = @"C:\Company\GhLibrary",
                    SendCanvasData = false
                };
                savedSettings.Save(settingsPath);
                SecretStore.SaveApiKey(apiKey, keyPath);

                // Verify plain-text key is not present in any file in the settings folder.
                foreach (string file in Directory.GetFiles(tempDir))
                {
                    string rawText = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                    Assert.DoesNotContain(apiKey, rawText);
                }

                // Simulate fresh reload after restart.
                AppSettings reloadedSettings = AppSettings.Load(settingsPath);
                Assert.Equal("gemini-3.6-flash", reloadedSettings.ModelId);
                Assert.Equal(@"C:\Company\GhLibrary", reloadedSettings.SharedLibraryFolder);
                Assert.False(reloadedSettings.SendCanvasData);

                bool loaded = SecretStore.TryLoadApiKey(out string? decryptedKey, keyPath, allowEnvironmentFallback: false);
                Assert.True(loaded);
                Assert.Equal(apiKey, decryptedKey);
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
