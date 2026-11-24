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
using GhCopilot.Core.Host;
using GhCopilot.Core.Index;
using GhCopilot.Core.Llm;
using GhCopilot.Core.Rhino;
using GhCopilot.Core.Settings;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class IntegrationUpdatesTests
    {
        [Fact]
        public void B1_ViewportVision_IsOptInByDefault_AndDetectsExplicitRequests()
        {
            var defaults = new AppSettings();
            Assert.False(defaults.AutoVerifyViewportAfterBuild);

            Assert.False(ChatPromptBuilder.ExplicitlyRequestsViewportVision("create a 10x10x10 box"));
            Assert.False(ChatPromptBuilder.ExplicitlyRequestsViewportVision("build a parametric facade grid"));
            Assert.True(ChatPromptBuilder.ExplicitlyRequestsViewportVision("verify my viewport"));
            Assert.True(ChatPromptBuilder.ExplicitlyRequestsViewportVision("what do you see in the screenshot"));
            Assert.True(ChatPromptBuilder.ExplicitlyRequestsViewportVision("inspect scene geometry"));
        }

        [Fact]
        public void B2_PlanFirst_AutoSkipsSimpleSingleOpAndCompanyCluster_UnlessForced()
        {
            var session = new ChatSession();

            Assert.True(ChatPromptBuilder.IsSimpleSingleRhinoOpPrompt("zoom extents"));
            Assert.True(ChatPromptBuilder.IsSimpleSingleRhinoOpPrompt("create a 10x10x10 box"));
            Assert.False(ChatPromptBuilder.IsSimpleSingleRhinoOpPrompt("design a pavilion canopy with columns and roof"));

            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding(
                "create a 10x10x10 box",
                session,
                planFirstEnabled: true,
                forcePlanFirstAlways: false,
                matchesKnownCompanyCluster: false,
                isSingleDirectRhinoOp: ChatPromptBuilder.IsSimpleSingleRhinoOpPrompt("create a 10x10x10 box")));

            Assert.False(ChatPromptBuilder.ShouldPlanBeforeBuilding(
                "build diagrid facade with company panelizer",
                session,
                planFirstEnabled: true,
                forcePlanFirstAlways: false,
                matchesKnownCompanyCluster: true,
                isSingleDirectRhinoOp: false));

            Assert.True(ChatPromptBuilder.ShouldPlanBeforeBuilding(
                "design a pavilion canopy with columns and roof",
                session,
                planFirstEnabled: true,
                forcePlanFirstAlways: false,
                matchesKnownCompanyCluster: false,
                isSingleDirectRhinoOp: ChatPromptBuilder.IsSimpleSingleRhinoOpPrompt("design a pavilion canopy with columns and roof")));

            // Force Plan First overrides auto-skip signals.
            Assert.True(ChatPromptBuilder.ShouldPlanBeforeBuilding(
                "create a 10x10x10 box",
                session,
                planFirstEnabled: true,
                forcePlanFirstAlways: true,
                matchesKnownCompanyCluster: true,
                isSingleDirectRhinoOp: true));

            var corpus = new List<ComponentRecord>
            {
                new ComponentRecord { ShortId = "c1", Name = "Surface Panelizer", IsCompany = true },
                new ComponentRecord { ShortId = "c2", Name = "Circle", IsCompany = false }
            };
            Assert.True(ChatPromptBuilder.HasKnownCompanyClusterMatch(corpus, "apply Surface Panelizer to the roof"));
            Assert.False(ChatPromptBuilder.HasKnownCompanyClusterMatch(corpus, "draw a Circle"));
        }

        [Fact]
        public void B3_HeadlessProtocolAndDiscovery_RoundTripAndPruneStalePids()
        {
            var req = new HeadlessPromptRequest
            {
                Prompt = "create a 10x10x10 box",
                Mode = "rhino",
                SkipPlanFirst = true,
                CaptureViewport = false,
                TimeoutSeconds = 120
            };
            string reqJson = JsonSerializer.Serialize(req);
            HeadlessPromptRequest? reqBack = JsonSerializer.Deserialize<HeadlessPromptRequest>(reqJson);
            Assert.NotNull(reqBack);
            Assert.Equal("create a 10x10x10 box", reqBack!.Prompt);
            Assert.Equal("rhino", reqBack.Mode);
            Assert.True(reqBack.SkipPlanFirst);
            Assert.False(reqBack.CaptureViewport);

            var resp = new HeadlessPromptResponse
            {
                Status = "ok",
                Summary = "Applied directly to Rhino: 1 created.",
                ObjectsCreated = 1,
                PromptTokens = 140,
                ResponseTokens = 55,
                DurationMs = 420,
                ModelId = "gemini-2.5-flash",
                Provider = "Gemini",
                Host = "RhinoInsideRevit"
            };
            resp.Warnings.Add("minor note");
            string respJson = JsonSerializer.Serialize(resp);
            HeadlessPromptResponse? respBack = JsonSerializer.Deserialize<HeadlessPromptResponse>(respJson);
            Assert.NotNull(respBack);
            Assert.Equal("ok", respBack!.Status);
            Assert.Equal(1, respBack.ObjectsCreated);
            Assert.Equal(140, respBack.PromptTokens);
            Assert.Equal("RhinoInsideRevit", respBack.Host);

            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotHeadless_" + Guid.NewGuid().ToString("N"));
            try
            {
                HeadlessEndpointDiscovery.WriteEndpoint(new HeadlessEndpointInfo
                {
                    Pid = 1001,
                    Host = "Rhino8",
                    StartedAtUtc = DateTime.UtcNow.AddMinutes(-1)
                }, tempDir);

                HeadlessEndpointDiscovery.WriteEndpoint(new HeadlessEndpointInfo
                {
                    Pid = 2002,
                    Host = "RhinoInsideRevit",
                    StartedAtUtc = DateTime.UtcNow
                }, tempDir);

                List<HeadlessEndpointInfo> active = HeadlessEndpointDiscovery.ListActiveEndpoints(
                    tempDir,
                    pid => pid == 2002);

                Assert.Single(active);
                Assert.Equal(2002, active[0].Pid);
                Assert.Equal("GhCopilot.Headless.2002", active[0].PipeName);
                Assert.False(File.Exists(HeadlessEndpointDiscovery.GetEndpointFilePath(1001, tempDir)));
                Assert.True(File.Exists(HeadlessEndpointDiscovery.GetEndpointFilePath(2002, tempDir)));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void B4_RhinoActionPlan_ImplementsIActionPlan_AndValidationConvertsToHostResult()
        {
            IActionPlan writePlan = new RhinoActionPlan
            {
                Explanation = "Add box",
                Ops = new List<RhinoActionOp>
                {
                    new RhinoActionOp { Op = "add_box", Width = 10, Depth = 10, Height = 10 }
                }
            };
            Assert.Equal(1, writePlan.OperationCount);
            Assert.True(writePlan.HasWriteOperations);

            IActionPlan readOnlyPlan = new RhinoActionPlan
            {
                Explanation = "Zoom and select",
                Ops = new List<RhinoActionOp>
                {
                    new RhinoActionOp { Op = "select", Target = "all" },
                    new RhinoActionOp { Op = "zoom", Scope = "extents" },
                    new RhinoActionOp { Op = "set_view", ViewName = "Perspective" }
                }
            };
            Assert.Equal(3, readOnlyPlan.OperationCount);
            Assert.False(readOnlyPlan.HasWriteOperations);

            var invalidPlan = new RhinoActionPlan
            {
                Ops = new List<RhinoActionOp>
                {
                    new RhinoActionOp { Op = "add_box", Width = -5, Depth = 10, Height = 10 }
                }
            };
            ActionPlanValidationResult hostValidation = RhinoActionPlanValidator.Validate(invalidPlan).ToHostResult();
            Assert.False(hostValidation.IsValid);
            Assert.NotEmpty(hostValidation.Errors);
        }

        [Fact]
        public async Task B5_LlmClient_ExtractsUsageAcrossProviders_AndWritesTokenAuditLog()
        {
            var handler = new StubHttpMessageHandler((req, _) =>
            {
                string url = req.RequestUri!.ToString();
                if (url.Contains("anthropic.com"))
                {
                    string anthropicJson = JsonSerializer.Serialize(new
                    {
                        content = new[] { new { type = "text", text = """{"nodes":[],"wires":[],"explanation":"Claude"}""" } },
                        usage = new { input_tokens = 210, output_tokens = 64 }
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(anthropicJson, Encoding.UTF8, "application/json")
                    });
                }

                if (url.Contains("openai.com"))
                {
                    string openAiJson = JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { message = new { content = "OpenAI text reply" } } },
                        usage = new { prompt_tokens = 320, completion_tokens = 88 }
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(openAiJson, Encoding.UTF8, "application/json")
                    });
                }

                if (url.Contains("fallback-total"))
                {
                    string geminiTotalOnly = JsonSerializer.Serialize(new
                    {
                        candidates = new[] { new { content = new { parts = new[] { new { text = """{"nodes":[],"wires":[],"explanation":"Gemini fallback"}""" } } } } },
                        usageMetadata = new { promptTokenCount = 100, totalTokenCount = 145 }
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(geminiTotalOnly, Encoding.UTF8, "application/json")
                    });
                }

                string geminiJson = JsonSerializer.Serialize(new
                {
                    candidates = new[] { new { content = new { parts = new[] { new { text = """{"nodes":[],"wires":[],"explanation":"Gemini"}""" } } } } },
                    usageMetadata = new { promptTokenCount = 150, candidatesTokenCount = 42, totalTokenCount = 192 }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(geminiJson, Encoding.UTF8, "application/json")
                });
            });

            using var http = new HttpClient(handler);
            using var client = new LlmClient(http);
            JsonElement schema = SchemaBuilder.BuildResponseJsonSchema(new[]
            {
                new ComponentRecord { ShortId = "c001", Name = "Circle" }
            });

            LlmCallResult geminiCall = await client.GenerateGraphJsonWithUsageAsync("key1", "gemini-2.5-flash", "sys", "usr", schema, provider: "Gemini");
            Assert.Equal(150, geminiCall.Usage.PromptTokens);
            Assert.Equal(42, geminiCall.Usage.ResponseTokens);
            Assert.Equal("Gemini", geminiCall.Usage.Provider);
            Assert.True(geminiCall.Usage.DurationMs >= 0);

            LlmCallResult geminiFallbackCall = await client.GenerateGraphJsonWithUsageAsync("key1", "gemini-2.5-flash", "sys", "usr", schema, provider: "Gemini", customEndpointUrl: "https://fallback-total.test/v1beta");
            Assert.Equal(100, geminiFallbackCall.Usage.PromptTokens);
            Assert.Equal(45, geminiFallbackCall.Usage.ResponseTokens);

            LlmCallResult claudeCall = await client.GenerateGraphJsonWithUsageAsync("key2", "claude-opus-4-6", "sys", "usr", schema, provider: "Anthropic");
            Assert.Equal(210, claudeCall.Usage.PromptTokens);
            Assert.Equal(64, claudeCall.Usage.ResponseTokens);
            Assert.Equal("Anthropic", claudeCall.Usage.Provider);

            LlmCallResult openAiCall = await client.GenerateTextWithUsageAsync("key3", "gpt-4.1", "sys", "usr", provider: "OpenAI");
            Assert.Equal(320, openAiCall.Usage.PromptTokens);
            Assert.Equal(88, openAiCall.Usage.ResponseTokens);
            Assert.Equal("OpenAI", openAiCall.Usage.Provider);

            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotAuditTokens_" + Guid.NewGuid().ToString("N"));
            string auditPath = Path.Combine(tempDir, "sent-payloads.jsonl");
            try
            {
                PayloadAuditLogger.LogTurnCompletion(
                    "Generate",
                    claudeCall.Usage.Provider,
                    claudeCall.Usage.ModelId,
                    sendCanvasDataEnabled: false,
                    systemInstruction: "sys",
                    promptText: "usr",
                    promptTokens: claudeCall.Usage.PromptTokens,
                    responseTokens: claudeCall.Usage.ResponseTokens,
                    durationMs: claudeCall.Usage.DurationMs,
                    filePath: auditPath);

                string line = File.ReadAllLines(auditPath).Single();
                PayloadAuditEntry? entry = JsonSerializer.Deserialize<PayloadAuditEntry>(line);
                Assert.NotNull(entry);
                Assert.Equal("Anthropic", entry!.Provider);
                Assert.Equal("claude-opus-4-6", entry.ModelId);
                Assert.Equal(210, entry.PromptTokens);
                Assert.Equal(64, entry.ResponseTokens);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void B5_AppSettingsAndValidator_ClampAndRespectConfigurableLimits()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotLimits_" + Guid.NewGuid().ToString("N"));
            string settingsPath = Path.Combine(tempDir, "settings.json");

            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(settingsPath, """
                {
                  "retrievalMaxItems": 999,
                  "maxGraphNodes": 5,
                  "autoVerifyViewportAfterBuild": true,
                  "forcePlanFirstAlways": true
                }
                """, Encoding.UTF8);

                AppSettings loaded = AppSettings.Load(settingsPath);
                Assert.Equal(AppSettings.MaxRetrievalMaxItems, loaded.RetrievalMaxItems);
                Assert.Equal(AppSettings.MinMaxGraphNodes, loaded.MaxGraphNodes);
                Assert.True(loaded.AutoVerifyViewportAfterBuild);
                Assert.True(loaded.ForcePlanFirstAlways);

                var shortlist = new Dictionary<string, ComponentRecord>
                {
                    ["c1"] = new ComponentRecord { ShortId = "c1", Name = "Number Slider", IsStandaloneParam = true }
                };
                var spec = new GraphSpec
                {
                    Nodes = new List<NodeSpec>
                    {
                        new NodeSpec { Id = "n1", Ref = "c1", X = 100, Y = 100 },
                        new NodeSpec { Id = "n2", Ref = "c1", X = 200, Y = 100 },
                        new NodeSpec { Id = "n3", Ref = "c1", X = 300, Y = 100 }
                    }
                };

                ValidationResult tooMany = GraphValidator.Validate(spec, shortlist, externalNodes: null, maxNodeCount: 2);
                Assert.False(tooMany.IsValid);
                Assert.Contains(tooMany.Errors, e => e.Code == ValidationErrorCode.TooManyNodes);

                ValidationResult withinLimit = GraphValidator.Validate(spec, shortlist, externalNodes: null, maxNodeCount: 5);
                Assert.True(withinLimit.IsValid);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void B6_SharedCopilotPaths_AndCompanyEndpointDefaults_ResolveCorrectly()
        {
            string tempHome = Path.Combine(Path.GetTempPath(), "GhCopilotHome_" + Guid.NewGuid().ToString("N"));
            string? previousHome = Environment.GetEnvironmentVariable(SharedCopilotPaths.HomeEnvVarName);

            try
            {
                Directory.CreateDirectory(tempHome);
                Environment.SetEnvironmentVariable(SharedCopilotPaths.HomeEnvVarName, tempHome);

                Assert.Equal(tempHome, SharedCopilotPaths.GetSharedDirectory());
                Assert.Equal(Path.Combine(tempHome, "settings.json"), AppSettings.GetDefaultSettingsPath());
                Assert.Equal(Path.Combine(tempHome, "api-key.dpapi"), SecretStore.GetDefaultKeyFilePath());
                Assert.Equal(Path.Combine(tempHome, "sent-payloads.jsonl"), PayloadAuditLogger.GetDefaultAuditLogPath());

                File.WriteAllText(
                    SharedCopilotPaths.GetCompanyDefaultsPath(),
                    """{ "companyEndpointUrl": "https://proxy.internal:5050/api/v1/llm" }""",
                    Encoding.UTF8);

                AppSettings loadedFromDefaults = AppSettings.Load();
                Assert.Equal("https://proxy.internal:5050/api/v1/llm", loadedFromDefaults.CompanyEndpointUrl);
                Assert.Equal("https://proxy.internal:5050/api/v1/llm", loadedFromDefaults.GetEffectiveEndpointUrl());

                loadedFromDefaults.CustomEndpointUrl = "https://custom.override.test/v1beta";
                Assert.Equal("https://custom.override.test/v1beta", loadedFromDefaults.GetEffectiveEndpointUrl());
            }
            finally
            {
                Environment.SetEnvironmentVariable(SharedCopilotPaths.HomeEnvVarName, previousHome);
                if (Directory.Exists(tempHome)) Directory.Delete(tempHome, true);
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
