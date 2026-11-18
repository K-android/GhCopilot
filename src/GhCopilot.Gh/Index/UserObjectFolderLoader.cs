using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using GhCopilot.Core.Index;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GhCopilot.Gh.Index
{
    /// <summary>
    /// Loads company .ghuser user objects and loose .gh/.ghx clusters from the configured shared folder,
    /// registers the folder with Grasshopper, and provides fallback IGH_ObjectProxy instantiation.
    /// </summary>
    public static class UserObjectFolderLoader
    {
        private static readonly object SyncLock = new object();
        private static readonly Dictionary<Guid, IGH_ObjectProxy> CompanyProxiesByGuid = new Dictionary<Guid, IGH_ObjectProxy>();
        private static readonly Dictionary<Guid, CompanyLibraryFileItem> CompanyItemsByGuid = new Dictionary<Guid, CompanyLibraryFileItem>();
        private static CompanyLibraryScanResult _lastScanResult = new CompanyLibraryScanResult();

        public static CompanyLibraryScanResult LastScanResult
        {
            get
            {
                lock (SyncLock)
                {
                    return _lastScanResult;
                }
            }
        }

        public static CompanyLibraryScanResult SyncCompanyFolder(string? folderPath)
        {
            CompanyLibraryScanResult scan = CompanyLibraryScanner.ScanFolder(folderPath);

            lock (SyncLock)
            {
                _lastScanResult = scan;
                CompanyProxiesByGuid.Clear();
                CompanyItemsByGuid.Clear();

                if (!scan.IsReachable || string.IsNullOrWhiteSpace(scan.FolderPath))
                {
                    return scan;
                }

                TryRegisterGrasshopperFolder(Folders.UserObjectFolders, scan.FolderPath);
                TryRegisterGrasshopperFolder(Folders.ClusterFolders, scan.FolderPath);

                GH_ComponentServer? server = Instances.ComponentServer;

                foreach (CompanyLibraryFileItem item in scan.Items)
                {
                    try
                    {
                        IGH_ObjectProxy proxy = CreateProxyForItem(item);
                        CompanyProxiesByGuid[proxy.Guid] = proxy;
                        CompanyItemsByGuid[proxy.Guid] = item;

                        if (server != null && server.EmitObjectProxy(proxy.Guid) is null)
                        {
                            server.AddProxy(proxy);
                        }
                    }
                    catch (Exception ex)
                    {
                        scan.Warnings.Add($"Failed to load company item '{item.FilePath}': {ex.Message}");
                    }
                }
            }

            return scan;
        }

        public static IGH_ObjectProxy? TryGetCompanyProxy(Guid componentGuid)
        {
            if (componentGuid == Guid.Empty)
            {
                return null;
            }

            lock (SyncLock)
            {
                return CompanyProxiesByGuid.TryGetValue(componentGuid, out IGH_ObjectProxy? proxy) ? proxy : null;
            }
        }

        public static bool IsCompanyProxy(IGH_ObjectProxy proxy, string? configuredFolder)
        {
            if (proxy is null)
            {
                return false;
            }

            lock (SyncLock)
            {
                if (CompanyProxiesByGuid.ContainsKey(proxy.Guid))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(configuredFolder) && !string.IsNullOrWhiteSpace(proxy.Location))
            {
                string normalizedFolder = Path.GetFullPath(configuredFolder!.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                try
                {
                    string normalizedLoc = Path.GetFullPath(proxy.Location);
                    if (normalizedLoc.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Ignore malformed proxy.Location paths.
                }
            }

            return false;
        }

        public static List<IGH_ObjectProxy> GetCompanyProxiesSnapshot()
        {
            lock (SyncLock)
            {
                return CompanyProxiesByGuid.Values.ToList();
            }
        }

        private static IGH_ObjectProxy CreateProxyForItem(CompanyLibraryFileItem item)
        {
            if (string.Equals(item.Source, "userobject", StringComparison.OrdinalIgnoreCase))
            {
                var userObj = new GH_UserObject(item.FilePath);
                if (userObj.Data == null || userObj.Data.Length == 0)
                {
                    userObj.ReadFromFile();
                }

                var desc = new GH_InstanceDescription(
                    string.IsNullOrWhiteSpace(userObj.Description?.Name) ? item.Name : userObj.Description!.Name,
                    string.IsNullOrWhiteSpace(userObj.Description?.NickName) ? item.Name : userObj.Description!.NickName,
                    string.IsNullOrWhiteSpace(userObj.Description?.Description) ? item.Description : userObj.Description!.Description,
                    string.IsNullOrWhiteSpace(userObj.Description?.Category) ? item.Category : userObj.Description!.Category,
                    string.IsNullOrWhiteSpace(userObj.Description?.SubCategory) ? item.SubCategory : userObj.Description!.SubCategory);

                Bitmap icon = userObj.Icon ?? new Bitmap(24, 24);

                return new CompanyFileObjectProxy(
                    guid: item.DeterministicGuid,
                    location: item.FilePath,
                    kind: GH_ObjectType.UserObject,
                    type: typeof(GH_Cluster),
                    desc: desc,
                    icon: icon,
                    exposure: userObj.Exposure == GH_Exposure.hidden ? GH_Exposure.primary : userObj.Exposure,
                    factory: () =>
                    {
                        var freshUserObj = new GH_UserObject(item.FilePath);
                        if (freshUserObj.Data == null || freshUserObj.Data.Length == 0)
                        {
                            freshUserObj.ReadFromFile();
                        }

                        IGH_DocumentObject obj = freshUserObj.InstantiateObject();
                        if (obj is null)
                        {
                            throw new InvalidOperationException($"GH_UserObject '{item.FilePath}' instantiated null.");
                        }

                        return obj;
                    });
            }

            var clusterDesc = new GH_InstanceDescription(
                item.Name,
                item.Name,
                item.Description,
                item.Category,
                item.SubCategory);

            return new CompanyFileObjectProxy(
                guid: item.DeterministicGuid,
                location: item.FilePath,
                kind: GH_ObjectType.CompiledObject,
                type: typeof(GH_Cluster),
                desc: clusterDesc,
                icon: new Bitmap(24, 24),
                exposure: GH_Exposure.primary,
                factory: () =>
                {
                    var cluster = new GH_Cluster();
                    cluster.CreateFromFilePath(item.FilePath);
                    if (string.IsNullOrWhiteSpace(cluster.Name))
                    {
                        cluster.Name = item.Name;
                    }
                    if (string.IsNullOrWhiteSpace(cluster.NickName))
                    {
                        cluster.NickName = item.Name;
                    }
                    return cluster;
                });
        }

        private static void TryRegisterGrasshopperFolder(IList<string>? folders, string folderPath)
        {
            if (folders is null || string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            foreach (string existing in folders)
            {
                if (string.Equals(existing, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            try
            {
                folders.Add(folderPath);
            }
            catch (Exception)
            {
                // Ignore if Grasshopper folder collection is read-only or locked.
            }
        }

        private sealed class CompanyFileObjectProxy : IGH_ObjectProxy
        {
            private readonly Func<IGH_DocumentObject> _factory;

            public CompanyFileObjectProxy(
                Guid guid,
                string location,
                GH_ObjectType kind,
                Type type,
                IGH_InstanceDescription desc,
                Bitmap icon,
                GH_Exposure exposure,
                Func<IGH_DocumentObject> factory)
            {
                Guid = guid;
                Location = location;
                Kind = kind;
                Type = type;
                Desc = desc;
                Icon = icon;
                Exposure = exposure;
                _factory = factory;
            }

            public string Location { get; }
            public Guid LibraryGuid => Guid.Empty;
            public bool SDKCompliant => true;
            public bool Obsolete => false;
            public Type Type { get; }
            public GH_ObjectType Kind { get; }
            public Guid Guid { get; }
            public Bitmap Icon { get; }
            public IGH_InstanceDescription Desc { get; }
            public GH_Exposure Exposure { get; set; }

            public IGH_DocumentObject CreateInstance() => _factory();

            public IGH_ObjectProxy DuplicateProxy()
            {
                return new CompanyFileObjectProxy(Guid, Location, Kind, Type, Desc, Icon, Exposure, _factory);
            }
        }
    }
}
