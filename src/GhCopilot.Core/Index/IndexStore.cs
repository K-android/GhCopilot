using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhCopilot.Core.Index
{
    /// <summary>
    /// Serializable disk cache envelope keyed by proxy count plus loaded plugin versions.
    /// </summary>
    public sealed class IndexCacheEnvelope
    {
        [JsonPropertyName("cacheKey")]
        public string CacheKey { get; set; } = string.Empty;

        [JsonPropertyName("generatedAtUtc")]
        public DateTime GeneratedAtUtc { get; set; }

        [JsonPropertyName("proxyCount")]
        public int ProxyCount { get; set; }

        [JsonPropertyName("components")]
        public List<ComponentRecord> Components { get; set; } = new List<ComponentRecord>();
    }

    /// <summary>
    /// Stores the Tier-1/Tier-2 Grasshopper component index and manages disk cache persistence.
    /// </summary>
    public sealed class IndexStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly List<ComponentRecord> _components;
        private readonly Dictionary<string, ComponentRecord> _byShortId;
        private readonly Dictionary<Guid, ComponentRecord> _byGuid;

        public IndexStore(IEnumerable<ComponentRecord> components, string cacheKey, bool loadedFromCache = false)
        {
            if (components is null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            _components = components.ToList();
            CacheKey = cacheKey ?? string.Empty;
            LoadedFromCache = loadedFromCache;

            _byShortId = new Dictionary<string, ComponentRecord>(StringComparer.OrdinalIgnoreCase);
            _byGuid = new Dictionary<Guid, ComponentRecord>();

            foreach (ComponentRecord item in _components)
            {
                if (!string.IsNullOrWhiteSpace(item.ShortId) && !_byShortId.ContainsKey(item.ShortId))
                {
                    _byShortId[item.ShortId] = item;
                }

                if (item.ComponentGuid != Guid.Empty && !_byGuid.ContainsKey(item.ComponentGuid))
                {
                    _byGuid[item.ComponentGuid] = item;
                }
            }
        }

        public bool LoadedFromCache { get; }

        public string CacheKey { get; }

        public IReadOnlyList<ComponentRecord> AllComponents => _components;

        public IReadOnlyList<string> GetInstalledPluginNames()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ComponentRecord item in _components)
            {
                if (item is null)
                {
                    continue;
                }

                if (item.IsRevitComponent)
                {
                    names.Add("Rhino.Inside.Revit (Revit)");
                    continue;
                }

                if (!string.Equals(item.Source, "plugin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string label = !string.IsNullOrWhiteSpace(item.LibraryName)
                    ? item.LibraryName.Trim()
                    : item.Category.Trim();

                if (!string.IsNullOrWhiteSpace(label))
                {
                    names.Add(label);
                }
            }

            return names.ToList();
        }

        public ComponentRecord? GetByShortId(string shortId)
        {
            if (string.IsNullOrWhiteSpace(shortId))
            {
                return null;
            }

            return _byShortId.TryGetValue(shortId.Trim(), out ComponentRecord? record) ? record : null;
        }

        public ComponentRecord? GetByGuid(Guid componentGuid)
        {
            if (componentGuid == Guid.Empty)
            {
                return null;
            }

            return _byGuid.TryGetValue(componentGuid, out ComponentRecord? record) ? record : null;
        }

        public static string GetDefaultDataDirectory()
        {
            return GhCopilot.Core.Settings.SharedCopilotPaths.GetSharedDirectory();
        }

        public static string GetDefaultCacheFilePath()
        {
            return Path.Combine(GetDefaultDataDirectory(), "index-cache.json");
        }

        public static string GetDefaultDumpFilePath()
        {
            return Path.Combine(GetDefaultDataDirectory(), "index-dump.json");
        }

        /// <summary>
        /// Computes a deterministic cache key from the total proxy count and plugin version fingerprints.
        /// </summary>
        public static string ComputeCacheKey(int proxyCount, IEnumerable<string> libraryVersionFingerprints)
        {
            if (libraryVersionFingerprints is null)
            {
                throw new ArgumentNullException(nameof(libraryVersionFingerprints));
            }

            List<string> sorted = libraryVersionFingerprints
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            string payload = $"{proxyCount}|{string.Join("|", sorted)}";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);

            var hex = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                hex.Append(b.ToString("x2"));
            }

            return hex.ToString();
        }

        /// <summary>
        /// Loads the index from disk cache when the cache key matches, or builds and persists a fresh index via scanner.
        /// </summary>
        public static IndexStore LoadOrBuild(
            string cacheFilePath,
            string expectedCacheKey,
            Func<List<ComponentRecord>> scanner)
        {
            if (string.IsNullOrWhiteSpace(cacheFilePath))
            {
                throw new ArgumentException("Cache file path must not be empty.", nameof(cacheFilePath));
            }

            if (scanner is null)
            {
                throw new ArgumentNullException(nameof(scanner));
            }

            if (File.Exists(cacheFilePath))
            {
                try
                {
                    string json = File.ReadAllText(cacheFilePath, Encoding.UTF8);
                    IndexCacheEnvelope? envelope = JsonSerializer.Deserialize<IndexCacheEnvelope>(json, JsonOptions);
                    if (envelope != null &&
                        string.Equals(envelope.CacheKey, expectedCacheKey, StringComparison.Ordinal) &&
                        envelope.Components != null &&
                        envelope.Components.Count > 0)
                    {
                        return new IndexStore(envelope.Components, expectedCacheKey, loadedFromCache: true);
                    }
                }
                catch (Exception)
                {
                    // Corrupted or unreadable cache file: rebuild cleanly below.
                }
            }

            List<ComponentRecord> fresh = scanner() ?? new List<ComponentRecord>();
            var store = new IndexStore(fresh, expectedCacheKey, loadedFromCache: false);
            store.SaveToFile(cacheFilePath);
            return store;
        }

        /// <summary>
        /// Writes the current index envelope as formatted JSON to disk.
        /// </summary>
        public void SaveToFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must not be empty.", nameof(filePath));
            }

            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var envelope = new IndexCacheEnvelope
            {
                CacheKey = CacheKey,
                GeneratedAtUtc = DateTime.UtcNow,
                ProxyCount = _components.Count,
                Components = _components
            };

            string json = JsonSerializer.Serialize(envelope, JsonOptions);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }
    }
}
