using System;
using System.Collections.Generic;
using System.IO;
using GhCopilot.Core.Index;
using Xunit;

namespace GhCopilot.Tests
{
    public sealed class IndexAndRetrieverTests
    {
        [Fact]
        public void IndexStore_LoadOrBuild_SecondStartupUsesDiskCache_AndInvalidatesWhenKeyChanges()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GhCopilotTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string cachePath = Path.Combine(tempDir, "index-cache.json");

            try
            {
                string keyV1 = IndexStore.ComputeCacheKey(2, new[] { "libA:1.0.0", "libB:2.1.0" });
                int scanCalls = 0;

                IndexStore first = IndexStore.LoadOrBuild(cachePath, keyV1, () =>
                {
                    scanCalls++;
                    return new List<ComponentRecord>
                    {
                        new ComponentRecord { ShortId = "c0001", ComponentGuid = Guid.NewGuid(), Name = "Addition" },
                        new ComponentRecord { ShortId = "c0002", ComponentGuid = Guid.NewGuid(), Name = "Circle" }
                    };
                });

                Assert.False(first.LoadedFromCache);
                Assert.Equal(1, scanCalls);
                Assert.True(File.Exists(cachePath));

                // Second startup with same cache key must hit disk cache without invoking scanner.
                IndexStore second = IndexStore.LoadOrBuild(cachePath, keyV1, () =>
                {
                    scanCalls++;
                    return new List<ComponentRecord>();
                });

                Assert.True(second.LoadedFromCache);
                Assert.Equal(1, scanCalls);
                Assert.Equal(2, second.AllComponents.Count);
                Assert.Equal("Addition", second.GetByShortId("c0001")?.Name);

                // Changing plugin version invalidates the cache and triggers a rescan.
                string keyV2 = IndexStore.ComputeCacheKey(2, new[] { "libA:1.0.1", "libB:2.1.0" });
                IndexStore third = IndexStore.LoadOrBuild(cachePath, keyV2, () =>
                {
                    scanCalls++;
                    return new List<ComponentRecord>
                    {
                        new ComponentRecord { ShortId = "c0001", ComponentGuid = Guid.NewGuid(), Name = "Addition" },
                        new ComponentRecord { ShortId = "c0002", ComponentGuid = Guid.NewGuid(), Name = "Circle" }
                    };
                });

                Assert.False(third.LoadedFromCache);
                Assert.Equal(2, scanCalls);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Fact]
        public void Retriever_BM25_RanksRelevantComponentsAndBoostsCompanyItems()
        {
            var corpus = new List<ComponentRecord>
            {
                new ComponentRecord
                {
                    ShortId = "c0001",
                    Name = "Addition",
                    Category = "Maths",
                    SubCategory = "Operators",
                    Description = "Perform mathematical addition",
                    IsCompany = false
                },
                new ComponentRecord
                {
                    ShortId = "c0002",
                    Name = "Native Facade Grid",
                    Category = "Surface",
                    SubCategory = "Util",
                    Description = "Subdivide surface into facade panels",
                    IsCompany = false
                },
                new ComponentRecord
                {
                    ShortId = "c0003",
                    Name = "Facade Panelizer",
                    Category = "Company",
                    SubCategory = "Facade",
                    Description = "Subdivide surface into facade panels with mullion offsets",
                    Source = "cluster",
                    IsCompany = true
                },
                new ComponentRecord
                {
                    ShortId = "c0004",
                    Name = "Number Slider",
                    Category = "Params",
                    SubCategory = "Input",
                    Description = "Numeric slider input",
                    IsCompany = false
                }
            };

            IReadOnlyList<ComponentRecord> shortlist = Retriever.Retrieve(corpus, "create facade panels", maxResults: 3);

            Assert.Equal(3, shortlist.Count);
            Assert.Equal("c0003", shortlist[0].ShortId);
            Assert.True(shortlist[0].IsCompany);
            Assert.Contains(shortlist, c => c.ShortId == "c0002");
        }

        [Fact]
        public void ComponentRecord_FormatPromptContextLine_MatchesCompactTextFormat()
        {
            var record = new ComponentRecord
            {
                ShortId = "c117",
                Name = "Facade Panelizer",
                Category = "Company",
                SubCategory = "Facade",
                IsCompany = true,
                Inputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Srf", TypeName = "Surface", Access = "item" },
                    new ParamRecord { Name = "U", TypeName = "Integer", Access = "item" }
                },
                Outputs = new List<ParamRecord>
                {
                    new ParamRecord { Name = "Panels", TypeName = "Brep", Access = "list" }
                }
            };

            string line = record.FormatPromptContextLine();

            Assert.Equal("c117 | [COMPANY] Facade Panelizer | Company>Facade | in: Srf(Surface,item) U(Integer,item) | out: Panels(Brep,list)", line);
        }
    }
}
