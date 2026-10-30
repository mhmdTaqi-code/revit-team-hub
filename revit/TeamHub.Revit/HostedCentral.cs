using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace TeamHub.Revit
{
    /// <summary>
    /// Projects whose central model lives on the Team Hub server ("hub:" central
    /// path): no Windows file share needed. Each member keeps a copy of central
    /// in Documents\TeamHub\&lt;project&gt;\central; the central file and its _backup
    /// folder travel as one zip, one revision per sync.
    /// </summary>
    public static class HostedCentral
    {
        const int Chunk = 2 * 1024 * 1024;

        public static bool IsHosted(string centralPath) =>
            centralPath != null && centralPath.StartsWith("hub:", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// My copy of central. Revit writes the central's full path INSIDE the
        /// file and every local syncs to that path, so it must be the same on
        /// every PC: C:\Users\Public\Documents\TeamHub\p&lt;project id&gt;\central\&lt;name&gt;.rvt
        /// (a per-user folder would send a teammate's sync to my user folder).
        /// </summary>
        public static string CentralFile(long pid, string hubPath)
        {
            var root = Environment.GetEnvironmentVariable("TEAMHUB_CENTRAL_ROOT") // self-test only
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "TeamHub");
            var dir = Path.Combine(root, $"p{pid}", "central");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, Ui.SafeName(hubPath.Substring(4)));
        }

        // Packages downloaded ahead of time, in the background (central file -> rev, zip).
        static readonly ConcurrentDictionary<string, (int rev, byte[] zip)> Ready = new ConcurrentDictionary<string, (int, byte[])>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Download the newest package now (background), so the next round only swaps files.</summary>
        public static async Task<int> Prefetch(HubClient hub, long pid, string centralFile)
        {
            var info = await Info(hub, pid).ConfigureAwait(false);
            if (info.rev == 0 || info.rev <= LocalRev(centralFile)) return 0;
            if (Ready.TryGetValue(centralFile, out var have) && have.rev >= info.rev) return have.rev;
            var zip = await Download(hub, pid, info.rev, info.size, null).ConfigureAwait(false);
            Ready[centralFile] = (info.rev, zip);
            Log.Info($"central r{info.rev} downloaded ahead ({zip.Length / 1024} KB)");
            return info.rev;
        }

        public static bool HasReady(string centralFile, int newerThan) =>
            Ready.TryGetValue(centralFile, out var r) && r.rev > newerThan;

        static async Task<(int rev, long size)> Info(HubClient hub, long pid)
        {
            var r = await hub.Get($"/api/projects/{pid}/revit/central").ConfigureAwait(false);
            if (!r.Ok) throw new IOException("ما كدرت أوصل للسيرفر: " + r.Error);
            return (r.Data["central"]["rev"].GetValue<int>(), r.Data["central"]["size"].GetValue<long>());
        }

        static async Task<byte[]> Download(HubClient hub, long pid, int rev, long size, IProgress<double> progress)
        {
            using var ms = new MemoryStream();
            long offset = 0;
            while (offset < size)
            {
                var c = await hub.Get($"/api/projects/{pid}/revit/central?rev={rev}&offset={offset}&length={Chunk}").ConfigureAwait(false);
                if (!c.Ok) throw new IOException("تحميل الملف الرئيسي انقطع: " + c.Error);
                var bytes = Convert.FromBase64String(c.Data["data"].GetValue<string>());
                if (bytes.Length == 0) break;
                ms.Write(bytes, 0, bytes.Length);
                offset += bytes.Length;
                progress?.Report(size > 0 ? (double)offset / size : 1);
            }
            return ms.ToArray();
        }

        static string RevFile(string centralFile) => Path.Combine(Path.GetDirectoryName(centralFile), "rev.txt");
        static string BackupFolder(string centralFile) =>
            Path.Combine(Path.GetDirectoryName(centralFile), Path.GetFileNameWithoutExtension(centralFile) + "_backup");

        /// <summary>Which server revision my copy of central is.</summary>
        public static int LocalRev(string centralFile)
        {
            try { return File.Exists(centralFile) && int.TryParse(File.ReadAllText(RevFile(centralFile)).Trim(), out var r) ? r : 0; }
            catch { return 0; }
        }

        public static void SetLocalRev(string centralFile, int rev) => File.WriteAllText(RevFile(centralFile), rev.ToString());

        /// <summary>Bring my copy of central up to the server's newest revision. Returns that revision.</summary>
        public static async Task<int> EnsureLatest(HubClient hub, long pid, string centralFile, IProgress<double> progress = null)
        {
            var (rev, size) = await Info(hub, pid).ConfigureAwait(false);
            var local = LocalRev(centralFile);
            if (rev == 0 || rev <= local) return local;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Already downloaded in the background? Then this is only a quick swap.
            byte[] zip = Ready.TryRemove(centralFile, out var ready) && ready.rev == rev
                ? ready.zip
                : await Download(hub, pid, rev, size, progress).ConfigureAwait(false);
            using (var ms = new MemoryStream(zip)) Unpack(ms, centralFile);
            SetLocalRev(centralFile, rev);
            Log.Info($"central r{rev} in place ({zip.Length / 1024} KB, {sw.ElapsedMilliseconds} ms)");
            return rev;
        }

        static void Unpack(Stream zip, string centralFile)
        {
            var dir = Path.GetDirectoryName(centralFile);
            var backup = BackupFolder(centralFile);
            // Extract next to it first, then swap: a half-written central must never exist.
            var tmp = Path.Combine(dir, ".incoming");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            using (var a = new ZipArchive(zip, ZipArchiveMode.Read)) a.ExtractToDirectory(tmp, true);
            var incomingRvt = Directory.GetFiles(tmp, "*.rvt").FirstOrDefault() ?? throw new IOException("الحزمة ما بيها ملف .rvt");
            if (File.Exists(centralFile)) File.Delete(centralFile);
            File.Move(incomingRvt, centralFile);
            // _backup: replace file by file. Revit can hold its session log
            // (.slog) open, and that file isn't part of the package anyway.
            Directory.CreateDirectory(backup);
            foreach (var old in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
            {
                if (old.EndsWith(".slog", StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(old);
            }
            var incomingBak = Directory.GetDirectories(tmp, "*_backup").FirstOrDefault();
            if (incomingBak != null)
            {
                foreach (var f in Directory.GetFiles(incomingBak, "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(backup, Path.GetRelativePath(incomingBak, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(f, dest, true);
                }
            }
            Directory.Delete(tmp, true);
        }

        /// <summary>central.rvt + its _backup folder as one zip.</summary>
        public static byte[] Pack(string centralFile)
        {
            using var ms = new MemoryStream();
            using (var a = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                Add(a, centralFile, Path.GetFileName(centralFile));
                var backup = BackupFolder(centralFile);
                if (Directory.Exists(backup))
                {
                    var name = Path.GetFileName(backup);
                    foreach (var f in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
                    {
                        // The session log is Revit's own record of who had the file open:
                        // it stays open while Revit works and isn't needed to rebuild central.
                        if (f.EndsWith(".slog", StringComparison.OrdinalIgnoreCase)) continue;
                        Add(a, f, name + "/" + Path.GetRelativePath(backup, f).Replace('\\', '/'));
                    }
                }
            }
            return ms.ToArray();
        }

        // Read even while Revit has the file open (CreateEntryFromFile would not).
        static void Add(ZipArchive a, string file, string entryName)
        {
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = a.CreateEntry(entryName, CompressionLevel.Fastest).Open();
            src.CopyTo(dst);
        }

        /// <summary>Upload a new revision (needs the sync turn, except the very first). Returns the new revision.</summary>
        public static async Task<int> Upload(HubClient hub, long pid, string centralFile, int baseRev)
        {
            var pkg = Pack(centralFile);
            string uploadId = null;
            var total = Math.Max(1, (pkg.Length + Chunk - 1) / Chunk);
            for (var i = 0; i < total; i++)
            {
                var len = Math.Min(Chunk, pkg.Length - i * Chunk);
                var r = await hub.Post($"/api/projects/{pid}/revit/central/chunk", new
                {
                    index = i,
                    upload_id = uploadId,
                    final = i == total - 1,
                    base_rev = baseRev,
                    file = Path.GetFileName(centralFile),
                    data = Convert.ToBase64String(pkg, i * Chunk, len),
                }).ConfigureAwait(false);
                if (!r.Ok) throw new IOException($"رفع الملف الرئيسي ما صار ({r.Status} {r.Error})");
                uploadId = r.Data?["upload_id"]?.GetValue<string>() ?? uploadId;
                if (r.Data?["done"]?.GetValue<bool>() == true)
                {
                    var rev = r.Data["central"]["rev"].GetValue<int>();
                    SetLocalRev(centralFile, rev);
                    Log.Info($"central r{rev} uploaded ({pkg.Length / 1024} KB)");
                    return rev;
                }
            }
            throw new IOException("رفع الملف الرئيسي ما كمل");
        }
    }
}
