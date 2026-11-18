using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GhCopilot.Core.Index;
using GhCopilot.Core.Llm;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class CompanyLibraryTests
    {
        [Fact]
        public void CompanyLibraryScanner_DiscoversGhUserAndLooseGhFiles_AndTagsCompany()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotCompany_" + Guid.NewGuid().ToString("N"));
            string facadeDir = Path.Combine(tempDir, "Facade");
            Directory.CreateDirectory(facadeDir);

            try
            {
                File.WriteAllText(Path.Combine(facadeDir, "Facade Panelizer.gh"), "<Archive/>");
                File.WriteAllText(Path.Combine(tempDir, "Mullion Tag.ghuser"), "stub-userobject");
                File.WriteAllText(Path.Combine(tempDir, "Readme.txt"), "ignore-me");

                CompanyLibraryScanResult result = CompanyLibraryScanner.ScanFolder(tempDir);

                Assert.True(result.IsReachable);
                Assert.Empty(result.Warnings);
                Assert.Equal(2, result.Items.Count);

                CompanyLibraryFileItem cluster = Assert.Single(result.Items, i => i.Source == "cluster");
                Assert.Equal("Facade Panelizer", cluster.Name);
                Assert.Equal("Company", cluster.Category);
                Assert.Equal("Facade", cluster.SubCategory);
                Assert.NotEqual(Guid.Empty, cluster.DeterministicGuid);

                CompanyLibraryFileItem userObj = Assert.Single(result.Items, i => i.Source == "userobject");
                Assert.Equal("Mullion Tag", userObj.Name);
                Assert.Equal("Shared", userObj.SubCategory);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void CompanyLibraryScanner_SkipsLockedFilesGracefullyWithoutThrowing()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotLocked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string lockedFile = Path.Combine(tempDir, "LockedCluster.gh");
            string readableFile = Path.Combine(tempDir, "HealthyCluster.gh");

            try
            {
                File.WriteAllText(lockedFile, "locked");
                File.WriteAllText(readableFile, "healthy");

                using (var exclusiveLock = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    CompanyLibraryScanResult result = CompanyLibraryScanner.ScanFolder(tempDir);

                    Assert.True(result.IsReachable);
                    Assert.Single(result.Items, i => i.Name == "HealthyCluster");
                    Assert.DoesNotContain(result.Items, i => i.Name == "LockedCluster");
                    Assert.Contains(result.Warnings, w => w.Contains("LockedCluster.gh"));
                }
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void CompanyLibraryScanner_UnreachableOrMissingPath_ReturnsUnreachableWithWarning()
        {
            string missingPath = Path.Combine(Path.GetTempPath(), "NonExistentShare_" + Guid.NewGuid().ToString("N"));

            CompanyLibraryScanResult result = CompanyLibraryScanner.ScanFolder(missingPath, TimeSpan.FromMilliseconds(500));

            Assert.False(result.IsReachable);
            Assert.Empty(result.Items);
            Assert.Contains(result.Warnings, w => w.Contains("does not exist or is unreachable"));
        }

        [Fact]
        public void CompanyLibraryScanner_FingerprintChangesWhenFilesChange()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotFp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                File.WriteAllText(Path.Combine(tempDir, "ClusterA.gh"), "v1");
                CompanyLibraryScanResult first = CompanyLibraryScanner.ScanFolder(tempDir);

                File.WriteAllText(Path.Combine(tempDir, "ClusterB.gh"), "v1");
                CompanyLibraryScanResult second = CompanyLibraryScanner.ScanFolder(tempDir);

                Assert.NotEqual(first.Fingerprint, second.Fingerprint);
                Assert.Equal(2, second.Items.Count);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task CompanyCluster_IsChosenOverNativeEquivalentInEndToEndRetrieveAndValidate()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotSelect_" + Guid.NewGuid().ToString("N"));
            string runLogPath = Path.Combine(tempDir, "runs.jsonl");
            string auditLogPath = Path.Combine(tempDir, "sent-payloads.jsonl");

            try
            {
                var corpus = new List<ComponentRecord>
                {
                    new ComponentRecord
                    {
                        ShortId = "c0001",
                        Name = "Divide Surface",
                        Category = "Surface",
                        SubCategory = "Util",
                        Description = "Subdivide surface into panels",
                        Source = "native",
                        IsCompany = false,
                        IsHydrated = true,
                        Inputs = new List<ParamRecord>
                        {
                            new ParamRecord { Name = "Surface", TypeName = "Surface", Access = "item" }
                        },
                        Outputs = new List<ParamRecord>
                        {
                            new ParamRecord { Name = "Points", TypeName = "Point", Access = "tree" }
                        }
                    },
                    new ComponentRecord
                    {
                        ShortId = "c0002",
                        Name = "Surface Panelizer",
                        Category = "Company",
                        SubCategory = "Facade",
                        Description = "Subdivide surface into facade panels",
                        Source = "cluster",
                        IsCompany = true,
                        IsHydrated = true,
                        Inputs = new List<ParamRecord>
                        {
                            new ParamRecord { Name = "Srf", TypeName = "Surface", Access = "item" }
                        },
                        Outputs = new List<ParamRecord>
                        {
                            new ParamRecord { Name = "Panels", TypeName = "Brep", Access = "list" }
                        }
                    }
                };

                IReadOnlyList<ComponentRecord> shortlist = Retriever.Retrieve(corpus, "subdivide surface into facade panels", maxResults: 10);
                Assert.Equal("c0002", shortlist[0].ShortId);
                Assert.Contains("[COMPANY] Surface Panelizer", shortlist[0].FormatPromptContextLine());

                string? capturedPrompt = null;
                var handler = new StubHttpMessageHandler(async (req, _) =>
                {
                    capturedPrompt = await req.Content!.ReadAsStringAsync();

                    string modelJson = """
                    {
                      "nodes": [
                        { "id": "n1", "ref": "c0002", "x": 300, "y": 200 }
                      ],
                      "wires": [],
                      "script": null,
                      "explanation": "Uses company Surface Panelizer cluster."
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
                    "subdivide surface into facade panels",
                    shortlist,
                    logFilePath: runLogPath,
                    auditLogPath: auditLogPath);

                Assert.True(result.IsSuccess);
                Assert.Equal("c0002", result.Spec!.Nodes[0].Ref);
                Assert.NotNull(capturedPrompt);
                Assert.Contains("[COMPANY] Surface Panelizer", capturedPrompt!);
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
