using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SarahsToolkit.Services
{
    public class DuplicateFile
    {
        public string Path { get; set; }
        public long Size { get; set; }
    }

    public class DuplicateGroup
    {
        public List<DuplicateFile> Files { get; set; } = new List<DuplicateFile>();
        public long SizeEach => Files.Count > 0 ? Files[0].Size : 0;
        public long Reclaimable => SizeEach * Math.Max(0, Files.Count - 1);
    }

    public class DuplicateScanResult
    {
        public List<DuplicateGroup> Groups { get; set; } = new List<DuplicateGroup>();
        public long FilesScanned { get; set; }
        public long SkippedFiles { get; set; }
        public long TotalReclaimable => Groups.Sum(g => g.Reclaimable);
    }

    /// <summary>
    /// Finds identical files under Downloads and Documents. Two-stage:
    /// group by size first (cheap), then SHA-256 only the same-size files.
    /// Empty files are skipped (noise, not clutter) and symlinks/junctions
    /// are never followed, so one file can't count twice through two paths.
    /// Deletion always keeps the first file per group and moves the rest to
    /// the Recycle Bin — nothing is gone for good.
    /// </summary>
    public class DuplicateFinderService
    {
        public static List<string> DefaultRoots()
        {
            var roots = new List<string>();
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!string.IsNullOrEmpty(docs) && Directory.Exists(docs)) roots.Add(docs);
            if (Directory.Exists(downloads)) roots.Add(downloads);
            return roots;
        }

        public Task<DuplicateScanResult> ScanAsync(IProgress<string> log, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var result = new DuplicateScanResult();
                var bySize = new Dictionary<long, List<string>>();
                foreach (var root in DefaultRoots())
                {
                    foreach (var file in SafeEnumerate(root, ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        long len;
                        try { len = new FileInfo(file).Length; }
                        catch { result.SkippedFiles++; continue; }
                        if (len == 0) continue;
                        if (!bySize.TryGetValue(len, out var list)) bySize[len] = list = new List<string>();
                        list.Add(file);
                        result.FilesScanned++;
                        if (result.FilesScanned % 2000 == 0)
                            log?.Report($"Scanned {result.FilesScanned} files…");
                    }
                }
                foreach (var kv in bySize.Where(kv => kv.Value.Count > 1))
                {
                    var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (var file in kv.Value)
                    {
                        ct.ThrowIfCancellationRequested();
                        string hash;
                        try { hash = HashFile(file); }
                        catch { result.SkippedFiles++; continue; }
                        if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = new List<string>();
                        list.Add(file);
                    }
                    foreach (var hg in byHash.Where(hg => hg.Value.Count > 1))
                    {
                        var files = hg.Value.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                            .Select(p => new DuplicateFile { Path = p, Size = kv.Key }).ToList();
                        result.Groups.Add(new DuplicateGroup { Files = files });
                    }
                }
                result.Groups = result.Groups.OrderByDescending(g => g.Reclaimable).ToList();
                return result;
            }, ct);
        }

        private static IEnumerable<string> SafeEnumerate(string root, CancellationToken ct)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                List<string> subdirs, files;
                try
                {
                    // ToList() forces enumeration inside the try: the lazy
                    // enumerators would otherwise throw outside it.
                    subdirs = Directory.EnumerateDirectories(dir).ToList();
                    files = Directory.EnumerateFiles(dir).ToList();
                }
                catch { continue; } // access denied etc: skip the whole dir
                foreach (var f in files)
                {
                    FileAttributes attr;
                    try { attr = File.GetAttributes(f); }
                    catch { continue; }
                    if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    yield return f;
                }
                foreach (var d in subdirs)
                {
                    FileAttributes attr;
                    try { attr = File.GetAttributes(d); }
                    catch { continue; }
                    if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    stack.Push(d);
                }
            }
        }

        private static string HashFile(string path)
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        /// <summary>
        /// Keeps the first file per group, moves the rest to the Recycle Bin.
        /// Returns (deleted, failed, bytesFreed).
        /// </summary>
        public (int deleted, int failed, long bytesFreed) DeleteGroups(IEnumerable<DuplicateGroup> groups)
        {
            int deleted = 0, failed = 0;
            long bytes = 0;
            foreach (var g in groups)
            {
                foreach (var f in g.Files.Skip(1))
                {
                    if (SendToRecycleBin(f.Path)) { deleted++; bytes += f.Size; }
                    else failed++;
                }
            }
            return (deleted, failed, bytes);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        private static bool SendToRecycleBin(string path)
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = 0x3, // FO_DELETE
                pFrom = path + "\0\0",
                pTo = null,
                fFlags = 0x40 | 0x10, // FOF_ALLOWUNDO | FOF_NOCONFIRMATION
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null,
            };
            try { return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted; }
            catch { return false; }
        }
    }
}
