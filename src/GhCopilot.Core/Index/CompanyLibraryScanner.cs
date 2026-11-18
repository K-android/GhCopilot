using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace GhCopilot.Core.Index
{
    public sealed class CompanyLibraryFileItem
    {
        public string FilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "Company";
        public string SubCategory { get; set; } = "Shared";
        public string Description { get; set; } = string.Empty;
        public string Source { get; set; } = "cluster";
        public Guid DeterministicGuid { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }

    public sealed class CompanyLibraryScanResult
    {
        public bool IsReachable { get; set; } = true;
        public string FolderPath { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public List<CompanyLibraryFileItem> Items { get; set; } = new List<CompanyLibraryFileItem>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Scans a configured shared company folder (local or UNC) for .ghuser user objects and loose .gh/.ghx clusters
    /// with bounded timeouts and lock resilience so slow/offline network shares never freeze Grasshopper.
    /// </summary>
    public static class CompanyLibraryScanner
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(2500);

        public static CompanyLibraryScanResult ScanFolder(string? folderPath, TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return new CompanyLibraryScanResult
                {
                    IsReachable = true,
                    FolderPath = string.Empty,
                    Fingerprint = "none"
                };
            }

            string normalizedFolder = folderPath!.Trim();
            TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;

            Task<CompanyLibraryScanResult> scanTask = Task.Run(() => ScanFolderInternal(normalizedFolder));
            try
            {
                if (!scanTask.Wait(effectiveTimeout))
                {
                    return new CompanyLibraryScanResult
                    {
                        IsReachable = false,
                        FolderPath = normalizedFolder,
                        Fingerprint = $"unreachable:{normalizedFolder}",
                        Warnings = new List<string>
                        {
                            $"Shared library folder '{normalizedFolder}' timed out after {effectiveTimeout.TotalMilliseconds:F0}ms."
                        }
                    };
                }

                return scanTask.GetAwaiter().GetResult();
            }
            catch (AggregateException ex)
            {
                return new CompanyLibraryScanResult
                {
                    IsReachable = false,
                    FolderPath = normalizedFolder,
                    Fingerprint = $"error:{normalizedFolder}",
                    Warnings = new List<string>
                    {
                        $"Shared library folder '{normalizedFolder}' failed: {ex.InnerException?.Message ?? ex.Message}"
                    }
                };
            }
        }

        private static CompanyLibraryScanResult ScanFolderInternal(string folderPath)
        {
            var result = new CompanyLibraryScanResult
            {
                FolderPath = folderPath
            };

            if (!Directory.Exists(folderPath))
            {
                result.IsReachable = false;
                result.Fingerprint = $"missing:{folderPath}";
                result.Warnings.Add($"Shared library folder '{folderPath}' does not exist or is unreachable.");
                return result;
            }

            result.IsReachable = true;
            var candidateFiles = new List<string>();
            EnumerateSupportedFilesSafe(folderPath, candidateFiles, result.Warnings);
            candidateFiles.Sort(StringComparer.OrdinalIgnoreCase);

            var fingerprintEntries = new List<string>();

            foreach (string filePath in candidateFiles)
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext != ".ghuser" && ext != ".gh" && ext != ".ghx")
                {
                    continue;
                }

                if (!CanOpenForRead(filePath, out string? lockError))
                {
                    result.Warnings.Add($"Skipped locked/unreadable file '{filePath}': {lockError}");
                    continue;
                }

                string relPath = GetRelativePathSafe(folderPath, filePath);
                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
                string source = ext == ".ghuser" ? "userobject" : "cluster";
                string subCategory = InferSubCategory(relPath);
                string baseName = Path.GetFileNameWithoutExtension(filePath);

                result.Items.Add(new CompanyLibraryFileItem
                {
                    FilePath = filePath,
                    RelativePath = relPath,
                    Name = baseName,
                    Category = "Company",
                    SubCategory = subCategory,
                    Description = ext == ".ghuser"
                        ? $"Company user object ({relPath})"
                        : $"Company cluster file ({relPath})",
                    Source = source,
                    DeterministicGuid = ComputeDeterministicGuid($"{source}:{relPath.ToLowerInvariant()}"),
                    LastWriteTimeUtc = lastWriteUtc
                });

                fingerprintEntries.Add($"{relPath.ToLowerInvariant()}|{lastWriteUtc.Ticks}");
            }

            result.Fingerprint = ComputeFingerprint(folderPath, fingerprintEntries);
            return result;
        }

        private static void EnumerateSupportedFilesSafe(string rootFolder, List<string> files, List<string> warnings)
        {
            var pendingDirs = new Stack<string>();
            pendingDirs.Push(rootFolder);

            while (pendingDirs.Count > 0)
            {
                string currentDir = pendingDirs.Pop();
                try
                {
                    foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                    {
                        pendingDirs.Push(subDir);
                    }

                    foreach (string file in Directory.EnumerateFiles(currentDir))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext == ".ghuser" || ext == ".gh" || ext == ".ghx")
                        {
                            files.Add(file);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    warnings.Add($"Unable to enumerate '{currentDir}': {ex.Message}");
                }
            }
        }

        private static bool CanOpenForRead(string filePath, out string? error)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string GetRelativePathSafe(string baseFolder, string filePath)
        {
            string normalizedBase = Path.GetFullPath(baseFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;
            string fullFile = Path.GetFullPath(filePath);

            if (fullFile.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            {
                return fullFile.Substring(normalizedBase.Length).Replace('\\', '/');
            }

            return Path.GetFileName(fullFile);
        }

        private static string InferSubCategory(string relativePath)
        {
            int slash = relativePath.IndexOf('/');
            if (slash > 0)
            {
                return relativePath.Substring(0, slash);
            }

            return "Shared";
        }

        public static Guid ComputeDeterministicGuid(string key)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(key ?? string.Empty);
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            var guidBytes = new byte[16];
            Array.Copy(hash, 0, guidBytes, 0, 16);
            return new Guid(guidBytes);
        }

        private static string ComputeFingerprint(string folderPath, List<string> entries)
        {
            string payload = $"{folderPath.ToLowerInvariant()}|{string.Join(";", entries)}";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);

            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
    }
}
