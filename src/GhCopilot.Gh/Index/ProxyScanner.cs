using System;
using System.Collections.Generic;
using System.Linq;
using GhCopilot.Core.Index;
using GhCopilot.Core.Settings;
using Grasshopper;
using Grasshopper.Kernel;

namespace GhCopilot.Gh.Index
{
    /// <summary>
    /// Scans Grasshopper's ComponentServer.ObjectProxies and configured company library folder
    /// into lightweight Tier-1 ComponentRecord entries without calling CreateInstance().
    /// </summary>
    public static class ProxyScanner
    {
        /// <summary>
        /// Computes a cache key from proxy count, loaded library versions, and company library folder fingerprint.
        /// </summary>
        public static string BuildCurrentSessionCacheKey()
        {
            AppSettings settings = AppSettings.Load();
            CompanyLibraryScanResult companyScan = UserObjectFolderLoader.SyncCompanyFolder(settings.SharedLibraryFolder);

            GH_ComponentServer? server = Instances.ComponentServer;
            int proxyCount = server?.ObjectProxies?.Count ?? 0;
            var fingerprints = new List<string>
            {
                $"company:{companyScan.Fingerprint}"
            };

            if (server?.Libraries != null)
            {
                foreach (GH_AssemblyInfo lib in server.Libraries)
                {
                    if (lib != null)
                    {
                        fingerprints.Add($"{lib.Id}:{lib.Name}:{lib.Version}");
                    }
                }
            }

            return IndexStore.ComputeCacheKey(proxyCount, fingerprints);
        }

        /// <summary>
        /// Enumerates all non-obsolete, visible ObjectProxies plus company library proxies into deterministic Tier-1 ComponentRecord items.
        /// </summary>
        public static List<ComponentRecord> ScanCurrentSession()
        {
            AppSettings settings = AppSettings.Load();
            UserObjectFolderLoader.SyncCompanyFolder(settings.SharedLibraryFolder);

            GH_ComponentServer? server = Instances.ComponentServer;
            var librariesById = new Dictionary<Guid, GH_AssemblyInfo>();
            if (server?.Libraries != null)
            {
                foreach (GH_AssemblyInfo lib in server.Libraries)
                {
                    if (lib != null && lib.Id != Guid.Empty && !librariesById.ContainsKey(lib.Id))
                    {
                        librariesById[lib.Id] = lib;
                    }
                }
            }

            var rawProxies = new List<(IGH_ObjectProxy Proxy, string Category, string SubCategory, string Name, Guid Guid)>();
            var seenGuids = new HashSet<Guid>();

            if (server?.ObjectProxies != null)
            {
                foreach (IGH_ObjectProxy proxy in server.ObjectProxies)
                {
                    AddProxyIfValid(proxy, seenGuids, rawProxies);
                }
            }

            foreach (IGH_ObjectProxy companyProxy in UserObjectFolderLoader.GetCompanyProxiesSnapshot())
            {
                AddProxyIfValid(companyProxy, seenGuids, rawProxies);
            }

            var sorted = rawProxies
                .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SubCategory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Guid)
                .ToList();

            var records = new List<ComponentRecord>(sorted.Count);
            for (int i = 0; i < sorted.Count; i++)
            {
                IGH_ObjectProxy proxy = sorted[i].Proxy;
                bool isCompany = UserObjectFolderLoader.IsCompanyProxy(proxy, settings.SharedLibraryFolder);
                string source = DetermineSource(proxy, librariesById);
                Type? proxyType = proxy.Type;
                bool isStandaloneParam = proxyType != null &&
                                         typeof(IGH_Param).IsAssignableFrom(proxyType) &&
                                         !typeof(IGH_Component).IsAssignableFrom(proxyType);

                records.Add(new ComponentRecord
                {
                    ShortId = $"c{(i + 1):D4}",
                    ComponentGuid = proxy.Guid,
                    Name = proxy.Desc?.Name ?? string.Empty,
                    NickName = proxy.Desc?.NickName ?? string.Empty,
                    Category = proxy.Desc?.Category ?? string.Empty,
                    SubCategory = proxy.Desc?.SubCategory ?? string.Empty,
                    Description = proxy.Desc?.Description ?? string.Empty,
                    Source = source,
                    IsCompany = isCompany,
                    IsStandaloneParam = isStandaloneParam,
                    IsHydrated = false
                });
            }

            return records;
        }

        private static void AddProxyIfValid(
            IGH_ObjectProxy? proxy,
            HashSet<Guid> seenGuids,
            List<(IGH_ObjectProxy Proxy, string Category, string SubCategory, string Name, Guid Guid)> target)
        {
            if (proxy is null || proxy.Obsolete || proxy.Exposure == GH_Exposure.hidden || proxy.Guid == Guid.Empty)
            {
                return;
            }

            if (!seenGuids.Add(proxy.Guid))
            {
                return;
            }

            string category = proxy.Desc?.Category ?? string.Empty;
            string subCategory = proxy.Desc?.SubCategory ?? string.Empty;
            string name = proxy.Desc?.Name ?? string.Empty;
            target.Add((proxy, category, subCategory, name, proxy.Guid));
        }

        private static string DetermineSource(IGH_ObjectProxy proxy, Dictionary<Guid, GH_AssemblyInfo> librariesById)
        {
            if (proxy.Type != null && string.Equals(proxy.Type.Name, "GH_Cluster", StringComparison.OrdinalIgnoreCase))
            {
                return "cluster";
            }

            if (proxy.Kind == GH_ObjectType.UserObject)
            {
                return "userobject";
            }

            if (proxy.LibraryGuid != Guid.Empty &&
                librariesById.TryGetValue(proxy.LibraryGuid, out GH_AssemblyInfo? lib) &&
                lib.IsCoreLibrary)
            {
                return "native";
            }

            return "plugin";
        }
    }
}
