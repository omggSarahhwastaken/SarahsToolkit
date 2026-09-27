using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SarahsToolkit.Models;

namespace SarahsToolkit.Services
{
    public class CleanupProgress
    {
        public string Category { get; set; }
        public string CurrentFile { get; set; }
        public long FilesDeleted { get; set; }
        public long BytesFreed { get; set; }
        public long SkippedFiles { get; set; }
        public string SkipKind { get; set; }
    }

    public class ScanResult
    {
        public long Bytes { get; set; }
        public long Files { get; set; }
    }

    public class CleanupService
    {
        public List<CleanupCategory> LoadCategories()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "cleanup.json");
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<CleanupCategory>>(File.ReadAllText(path), opts)
                   ?? new List<CleanupCategory>();
        }

        public Task<long> ScanAsync(CleanupCategory cat)
        {
            return Task.Run(() => MeasureCategory(cat).bytes);
        }

        /// <summary>
        /// Measures a category's size AND file count (for determinate progress).
        /// </summary>
        public Task<ScanResult> ScanDetailedAsync(CleanupCategory cat)
        {
            return Task.Run(() =>
            {
                var (bytes, files) = MeasureCategory(cat);
                return new ScanResult { Bytes = bytes, Files = files };
            });
        }

        public Task<long> CleanAsync(IEnumerable<CleanupCategory> cats, IProgress<CleanupProgress> progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                long totalBytes = 0;
                long totalFiles = 0;
                long totalSkipped = 0;
                string lastSkipKind = "";

                foreach (var cat in cats)
                {
                    ct.ThrowIfCancellationRequested();
                    Report(progress, cat.Name, "", totalFiles, totalBytes, totalSkipped, lastSkipKind);

                    if (cat.Special == "recyclebin")
                    {
                        PowerShellRunner.RunScript("Clear-RecycleBin -Force -ErrorAction SilentlyContinue", 2);
                        continue;
                    }

                    if (cat.StopServices != null && cat.StopServices.Count > 0)
                    {
                        string names = string.Join(",", cat.StopServices.Select(n => "'" + n + "'"));
                        PowerShellRunner.RunScript("Stop-Service -Name " + names + " -Force -ErrorAction SilentlyContinue", 1);
                    }

                    try
                    {
                        foreach (var rawPath in cat.Paths)
                        {
                            foreach (var search in ResolveSearchPaths(rawPath))
                            {
                                foreach (var file in SafeEnumerateFiles(search.Dir, search.Pattern))
                                {
                                    ct.ThrowIfCancellationRequested();
                                    try
                                    {
                                        long len = new FileInfo(file).Length;
                                        DeleteWithAclRetry(file);
                                        totalFiles++;
                                        totalBytes += len;
                                        if (totalFiles % 50 == 0)
                                            Report(progress, cat.Name, file, totalFiles, totalBytes, totalSkipped, lastSkipKind);
                                    }
                                    catch (Exception ex)
                                    {
                                        // in use, access denied, or other -> count it with the
                                        // reason instead of silently swallowing it
                                        totalSkipped++;
                                        lastSkipKind = ex is UnauthorizedAccessException ? "access denied"
                                            : ex is IOException ? "in use" : "error";
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        // Always restart category services, even if the run was
                        // cancelled or blew up mid-category.
                        if (cat.StartServices != null && cat.StartServices.Count > 0)
                        {
                            string names = string.Join(",", cat.StartServices.Select(n => "'" + n + "'"));
                            PowerShellRunner.RunScript("Start-Service -Name " + names + " -ErrorAction SilentlyContinue", 1);
                        }
                    }
                }

                Report(progress, "Done", "", totalFiles, totalBytes, totalSkipped, lastSkipKind);
                return totalBytes;
            }, ct);
        }

        /// <summary>
        /// Deletes a file, retrying once after resetting its ACL when access is
        /// denied (updaters and crash handlers often leave temp files with
        /// restrictive DACLs that even admins can't delete directly).
        /// </summary>
        private static void DeleteWithAclRetry(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (UnauthorizedAccessException)
            {
                var fi = new FileInfo(file);
                var acl = fi.GetAccessControl();
                acl.SetAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, AccessControlType.Allow));
                fi.SetAccessControl(acl);
                File.Delete(file);
            }
        }

        private static void Report(IProgress<CleanupProgress> progress, string category, string file,
            long files, long bytes, long skipped, string skipKind)
        {
            progress.Report(new CleanupProgress
            {
                Category = category,
                CurrentFile = file,
                FilesDeleted = files,
                BytesFreed = bytes,
                SkippedFiles = skipped,
                SkipKind = skipKind
            });
        }

        private static (long bytes, long files) MeasureCategory(CleanupCategory cat)
        {
            if (cat.Special == "recyclebin") return (0, 0);
            long total = 0;
            long files = 0;
            foreach (var rawPath in cat.Paths)
            {
                foreach (var search in ResolveSearchPaths(rawPath))
                {
                    foreach (var file in SafeEnumerateFiles(search.Dir, search.Pattern))
                    {
                        try { total += new FileInfo(file).Length; files++; }
                        catch { }
                    }
                }
            }
            return (total, files);
        }

        private struct SearchPath
        {
            public string Dir;
            public string Pattern;
        }

        private static List<SearchPath> ResolveSearchPaths(string rawPath)
        {
            string expanded = Environment.ExpandEnvironmentVariables(rawPath);
            string dirPart;
            string pattern;
            if (expanded.EndsWith("\\*"))
            {
                dirPart = expanded.Substring(0, expanded.Length - 2);
                pattern = "*";
            }
            else
            {
                dirPart = Path.GetDirectoryName(expanded);
                pattern = Path.GetFileName(expanded);
                if (string.IsNullOrEmpty(pattern)) pattern = "*";
            }
            if (string.IsNullOrEmpty(dirPart)) return new List<SearchPath>();
            return ResolveDirectories(dirPart)
                .Select(d => new SearchPath { Dir = d, Pattern = pattern })
                .ToList();
        }

        // Expands wildcard segments in the middle of a path (e.g. Firefox profiles).
        private static IEnumerable<string> ResolveDirectories(string dirPart)
        {
            int wc = dirPart.IndexOf('*');
            int q = dirPart.IndexOf('?');
            if (q >= 0 && (wc < 0 || q < wc)) wc = q;
            if (wc < 0)
            {
                if (Directory.Exists(dirPart)) yield return dirPart;
                yield break;
            }
            int sepBefore = dirPart.LastIndexOf('\\', wc);
            string parentPart = sepBefore < 0 ? "" : dirPart.Substring(0, sepBefore);
            string rest = dirPart.Substring(sepBefore + 1);
            int sepAfter = rest.IndexOf('\\');
            string wcSegment = sepAfter < 0 ? rest : rest.Substring(0, sepAfter);
            string remainder = sepAfter < 0 ? "" : rest.Substring(sepAfter + 1);
            foreach (var parent in ResolveDirectories(parentPart))
            {
                string[] matches;
                try { matches = Directory.GetDirectories(parent, wcSegment); }
                catch { continue; }
                foreach (var m in matches)
                {
                    string full = string.IsNullOrEmpty(remainder) ? m : m + "\\" + remainder;
                    foreach (var d in ResolveDirectories(full)) yield return d;
                }
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string current = stack.Pop();
                string[] subdirs = Array.Empty<string>();
                string[] files = Array.Empty<string>();
                try { subdirs = Directory.GetDirectories(current); }
                catch { }
                try { files = Directory.GetFiles(current, pattern); }
                catch { }
                foreach (var f in files) yield return f;
                foreach (var d in subdirs) stack.Push(d);
            }
        }
    }
}
