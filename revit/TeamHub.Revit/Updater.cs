using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TeamHub.Revit
{
    /// <summary>
    /// Keeps Team Hub up to date by itself. When Revit starts (and every few
    /// hours) it asks the team's server for the newest version; if it's newer,
    /// it downloads it and puts it in place quietly. Revit has the running copy
    /// loaded already, so the new one starts the next time Revit opens.
    /// Works for both ways of installing: the .addin add-in and the pyRevit extension.
    /// </summary>
    public static class Updater
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        static Timer _timer;
        static string _installed; // version already put in place this session
        static int _busy;

        /// <summary>pyRevit: the extension folder (set by teamhub_load.py).</summary>
        static string ExtensionDir =>
            AppDomain.CurrentDomain.GetData("TeamHub.Revit.ExtensionDir") as string;

        public static void Start()
        {
            CleanupOld();
            _timer = new Timer(_ => { _ = Check(); }, null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));
        }

        public static bool Newer(string a, string b)
        {
            var pa = (a ?? "0").Split('.');
            var pb = (b ?? "0").Split('.');
            for (var i = 0; i < 3; i++)
            {
                int.TryParse(i < pa.Length ? pa[i] : "0", out var x);
                int.TryParse(i < pb.Length ? pb[i] : "0", out var y);
                if (x != y) return x > y;
            }
            return false;
        }

        public static async Task Check()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var server = HubClient.Normalize(App.Settings?.Server);
                if (string.IsNullOrEmpty(server)) return;
                using var res = await Http.GetAsync(server + "/api/revit/update").ConfigureAwait(false);
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return; // this server hands out no add-in
                res.EnsureSuccessStatusCode();
                var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                var info = JsonNode.Parse(text);
                var version = info?["version"]?.GetValue<string>();
                if (!Newer(version, App.Version) || version == _installed) return;

                var ext = ExtensionDir;
                if (ext != null && Directory.Exists(ext) && info["pyrevit"] != null)
                {
                    var zip = await Http.GetByteArrayAsync(server + info["pyrevit"].GetValue<string>()).ConfigureAwait(false);
                    InstallExtension(zip, ext);
                }
                else
                {
                    var zip = await Http.GetByteArrayAsync(server + info["addin"].GetValue<string>()).ConfigureAwait(false);
                    InstallAddinDll(zip);
                }
                _installed = version;
                Log.Info($"update {App.Version} -> {version} installed; starts with the next Revit session");
                App.Live?.AddActivity(null, null, $"تحدّث Team Hub لـ {version}. يشتغل من تفتح Revit المرة الجاية.");
                if (App.Pane != null)
                {
                    App.Pane.Warning = $"تحدّث Team Hub لـ {version} ✓ — سكّر Revit وافتحه حتى يشتغل الجديد.";
                    App.Pane.Refresh();
                }
            }
            catch (Exception ex) { Log.Error("update check", ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        // pyRevit: replace the extension's files. Nothing there is locked (the
        // DLL was read into memory), so files are simply overwritten.
        static void InstallExtension(byte[] zipBytes, string ext)
        {
            const string prefix = "TeamHub-pyRevit/TeamHub.extension/";
            using var a = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            var entries = a.Entries.Where(e => e.FullName.StartsWith(prefix) && e.Name.Length > 0).ToList();
            if (!entries.Any(e => e.FullName.EndsWith("bin/TeamHub.Revit.dll"))) throw new InvalidDataException("update package has no DLL");
            foreach (var e in entries)
            {
                var dest = Path.Combine(ext, e.FullName.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                var tmp = dest + ".new";
                e.ExtractToFile(tmp, true);
                File.Move(tmp, dest, true);
            }
        }

        // .addin: Revit has the DLL open, but an open file can be renamed. Move
        // it aside and put the new one in its place; the old copy is deleted
        // the next time Revit starts.
        static void InstallAddinDll(byte[] zipBytes)
        {
            var current = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(current) || !File.Exists(current)) throw new IOException("can't find the installed add-in");
            using var a = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            var entry = a.Entries.FirstOrDefault(e => e.FullName.EndsWith("/TeamHub.Revit.dll")) ?? throw new InvalidDataException("update package has no DLL");
            var dir = Path.GetDirectoryName(current);
            var tmp = Path.Combine(dir, "TeamHub.Revit.dll.new");
            entry.ExtractToFile(tmp, true);
            var aside = Path.Combine(dir, $"TeamHub.Revit.old-{DateTime.Now:yyyyMMddHHmmss}.dll");
            File.Move(current, aside);
            File.Move(tmp, current);
        }

        static void CleanupOld()
        {
            try
            {
                var loc = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(loc)) return;
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(loc), "TeamHub.Revit.old-*.dll"))
                {
                    try { File.Delete(f); } catch { /* still in use by another Revit */ }
                }
            }
            catch { }
        }
    }
}
