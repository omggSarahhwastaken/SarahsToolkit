using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            return Task.Run(() => MeasureCategory(cat));
        }

        public Task<long> CleanAsync(IEnumerable<CleanupCategory> cats, IProgress<CleanupProgress> progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                long totalBytes = 0;
                long totalFiles = 0;

                foreach (var cat in cats)
                {
                    ct.ThrowIfCancellationRequested();
                    Report(progress, cat.Name, "", totalFiles, totalBytes);

                    if (cat.Special == "recyclebin")
                    {
                        PowerShellRunner.RunScript("Clear-RecycleBin -Force -ErrorAction SilentlyContinue", 2);
                        continue;
                    }

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
                                    File.Delete(file);
                                    totalFiles++;
                                    totalBytes += len;
                                    if (totalFiles % 50 == 0)
                                        Report(progress, cat.Name, file, totalFiles, totalBytes);
                                }
                                catch
                                {
                                    // in use or access denied -> skip
                                }
                            }
                        }
                    }
                }

                Report(progress, "Done", "", totalFiles, totalBytes);
                return totalBytes;
            }, ct);
        }

        private static void Report(IProgress<CleanupProgress> progress, string category, string file, long files, long bytes)
        {
            progress.Report(new CleanupProgress
            {
                Category = category,
                CurrentFile = file,
                FilesDeleted = files,
                BytesFreed = bytes
            });
        }

        private static long MeasureCategory(CleanupCategory cat)
        {
            if (cat.Special == "recyclebin") return 0;
            long total = 0;
            foreach (var rawPath in cat.Paths)
            {
                foreach (var search in ResolveSearchPaths(rawPath))
                {
                    foreach (var file in SafeEnumerateFiles(search.Dir, search.Pattern))
                    {
                        try { total += new FileInfo(file).Length; }
                        catch { }
                    }
                }
            }
            return total;
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
