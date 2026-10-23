using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TeamHub.Revit
{
    /// <summary>
    /// End-to-end test, only active when the TEAMHUB_SELFTEST environment variable
    /// points to a JSON script. Two Revit sessions ("A" creates the project and a
    /// wall, "B" joins and adds a wall) prove that edits travel both ways on their
    /// own, and log how long it took. Never runs in normal use.
    /// </summary>
    public class SelfTest : IExternalEventHandler
    {
        JsonNode _cfg;
        string _out;
        int _stage;
        DateTime _stageAt = DateTime.UtcNow;
        DateTime _wallAt;
        ExternalEvent _event;
        Timer _timer;
        long _pid;
        Document _doc;

        public static void Init()
        {
            var path = Environment.GetEnvironmentVariable("TEAMHUB_SELFTEST");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var t = new SelfTest { _cfg = JsonNode.Parse(File.ReadAllText(path)) };
            t._out = t._cfg["out"].GetValue<string>();
            App.Settings.NoSave = true;
            t._event = ExternalEvent.Create(t);
            t._timer = new Timer(_ => t._event.Raise(), null, 3000, 1000);
            t.W($"init role={t.Role} revit={App.RevitVersion}");
        }

        string Role => _cfg["role"].GetValue<string>();
        string S(string key) => _cfg[key]?.GetValue<string>();
        public string GetName() => "Team Hub self-test";

        void W(string msg)
        {
            try { File.AppendAllText(_out, $"{DateTime.Now:HH:mm:ss.fff} [{Role}] {msg}{Environment.NewLine}"); } catch { }
        }

        void Next(string msg = null)
        {
            if (msg != null) W(msg);
            _stage++;
            _stageAt = DateTime.UtcNow;
        }

        double Since => (DateTime.UtcNow - _stageAt).TotalSeconds;

        int Walls(Document doc) => new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();

        public void Execute(UIApplication uiapp)
        {
            try
            {
                if (_stage < 0) return;
                if (Since > 900) { W($"TIMEOUT at stage {_stage}"); _stage = -1; return; }
                if (Role == "A") StepA(uiapp); else StepB(uiapp);
            }
            catch (Exception ex)
            {
                W($"FAIL stage {_stage}: {ex.GetType().Name}: {ex.Message}");
                _stage = -1;
            }
        }

        bool Login()
        {
            var hub = new HubClient(S("server"), null);
            var r = hub.Post("/api/login", new { username = S("user"), password = S("pass"), client = "revit" }).GetAwaiter().GetResult();
            if (!r.Ok) { W("login failed " + r.Error); return false; }
            App.Settings.Server = hub.Server;
            App.Settings.Token = r.Data["token"].GetValue<string>();
            App.Settings.UserId = r.Data["user"]["id"].GetValue<long>();
            App.Settings.DisplayName = r.Data["user"]["display_name"].GetValue<string>();
            App.Settings.Role = r.Data["user"]["role"].GetValue<string>();
            App.Settings.Live = true;
            App.Settings.IdleSeconds = _cfg["idle"]?.GetValue<double>() ?? 5;
            hub.Token = App.Settings.Token;
            App.SetHub(hub);
            return true;
        }

        void OpenLocal(UIApplication uiapp, string central, string local)
        {
            if (File.Exists(local)) File.Delete(local);
            WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(central),
                                            ModelPathUtils.ConvertUserVisiblePathToModelPath(local));
            App.Settings.LocalFiles[local.ToLowerInvariant()] = _pid;
            var opts = new OpenOptions();
            opts.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
            _doc = uiapp.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(local), opts, false).Document;
        }

        Wall AddWall(Document doc, double y)
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
            using var t = new Transaction(doc, "Team Hub test wall");
            t.Start();
            var w = Wall.Create(doc, Line.CreateBound(new XYZ(0, y, 0), new XYZ(20, y, 0)), level.Id, false);
            t.Commit();
            _wallAt = DateTime.UtcNow;
            return w;
        }

        // ------------------------------------------------------------ helpers shared by both roles

        // Each role appends to its own file and reads the other's: no write races.
        string BusFile(string role) => S("out") + "." + role + ".bus";

        void Post(string key) => File.AppendAllText(BusFile(Role), $"{DateTime.UtcNow.Ticks} {key}{Environment.NewLine}");

        (DateTime at, string value)? Find(string prefix)
        {
            var f = BusFile(Role == "A" ? "B" : "A");
            if (!File.Exists(f)) return null;
            foreach (var line in File.ReadAllLines(f))
            {
                var i = line.IndexOf(' ');
                if (i > 0 && line.Substring(i + 1).StartsWith(prefix))
                    return (new DateTime(long.Parse(line.Substring(0, i)), DateTimeKind.Utc), line.Substring(i + 1 + prefix.Length));
            }
            return null;
        }

        DateTime? Seen(string key) => Find(key)?.at;
        string SeenValue(string prefix) => Find(prefix)?.value;
        double MsSince(DateTime? t) => t == null ? -1 : (DateTime.UtcNow - t.Value).TotalMilliseconds;
        void Check(bool ok, string what) => W((ok ? "PASS " : "FAIL ") + what);

        static string R(double v) => Math.Round(v, 3).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

        // Every wall's exact line (start/end x, y, z), sorted: the whole model's walls.
        string Fingerprint(Document doc)
        {
            var parts = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Select(w =>
            {
                var c = (w.Location as LocationCurve)?.Curve;
                if (c == null) return "?";
                var a = c.GetEndPoint(0);
                var b = c.GetEndPoint(1);
                return $"({R(a.X)},{R(a.Y)},{R(a.Z)})-({R(b.X)},{R(b.Y)},{R(b.Z)})";
            }).OrderBy(x => x, StringComparer.Ordinal);
            return string.Join(" ", parts);
        }

        Wall WallAt(Document doc, double y) => new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
            .FirstOrDefault(w => (w.Location as LocationCurve)?.Curve is Curve c && Math.Abs(c.GetEndPoint(0).Y - y) < 0.001);

        bool SyncedSince(DateTime t) => App.Live.Session.LastSyncUtc > t;

        // Wait (up to a minute) for something the other side did; then report it.
        bool Arrived(string key, Func<bool> ok, string what)
        {
            var at = Seen(key);
            if (at == null) return false;
            var good = ok();
            if (!good && MsSince(at) < 60000) return false;
            Check(good, $"{what} {MsSince(at) / 1000:0.0}s after it happened (walls={Walls(_doc)})");
            return true;
        }

        // ------------------------------------------------------------ A: creates the project
        void StepA(UIApplication uiapp)
        {
            var central = S("central");
            switch (_stage)
            {
                case 0:
                    if (Login()) Next("logged in");
                    break;
                case 1:
                {
                    if (File.Exists(central)) File.Delete(central);
                    var doc = uiapp.Application.NewProjectDocument(UnitSystem.Metric);
                    using (var t = new Transaction(doc, "level"))
                    {
                        t.Start();
                        if (!new FilteredElementCollector(doc).OfClass(typeof(Level)).Any()) Level.Create(doc, 0);
                        t.Commit();
                    }
                    doc.EnableWorksharing("Shared Levels and Grids", "Workset1");
                    var sao = new SaveAsOptions { OverwriteExistingFile = true };
                    sao.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                    doc.SaveAs(central, sao);
                    var r = App.Hub.Post("/api/projects", new { name = "اختبار Revit", kind = "revit", central_path = central, revit_version = App.RevitVersion })
                                  .GetAwaiter().GetResult();
                    _pid = r.Data["project"]["id"].GetValue<long>();
                    _doc = doc;
                    Next($"central created + project {_pid} registered");
                    break;
                }
                case 2:
                    // Revit can't hold a central and its local in one session: close central first.
                    _doc.Close(false);
                    OpenLocal(uiapp, central, S("local"));
                    Post("A:ready");
                    Next("central closed, local opened");
                    break;
                case 3:
                    if (App.Live.Session != null && Seen("B:live") != null) Next("live session started, B is live too");
                    break;
                case 4:
                    AddWall(_doc, 0);
                    Post("A:added");
                    Next($"added wall at y=0 (walls={Walls(_doc)})");
                    break;
                case 5:
                    if (SyncedSince(_wallAt)) Next($"synced my wall {(App.Live.Session.LastSyncUtc - _wallAt).TotalSeconds:0.0}s after drawing it");
                    break;
                case 6:
                    if (Arrived("B:added", () => WallAt(_doc, 10) != null, "B's new wall reached A")) Next();
                    break;
                case 7:
                {
                    var w = WallAt(_doc, 0);
                    using (var t = new Transaction(_doc, "move wall"))
                    {
                        t.Start();
                        ElementTransformUtils.MoveElement(_doc, w.Id, new XYZ(0, 5, 0));
                        t.Commit();
                    }
                    _wallAt = DateTime.UtcNow;
                    Post("A:moved");
                    Next("moved my wall y=0 -> y=5");
                    break;
                }
                case 8:
                    if (SyncedSince(_wallAt)) Next("synced the move");
                    break;
                case 9:
                    if (Arrived("B:deleted", () => WallAt(_doc, 10) == null, "B's delete reached A")) Next();
                    break;
                case 10:
                    Post("A:go");
                    AddWall(_doc, 30);
                    Next("drew wall y=30 at the same moment as B draws y=40");
                    break;
                case 11:
                {
                    var ok = WallAt(_doc, 30) != null && WallAt(_doc, 40) != null && WallAt(_doc, 5) != null && Walls(_doc) == 3;
                    if (!ok && Since < 90) break;
                    Check(ok, $"both simultaneous walls on A after {Since:0.0}s (walls={Walls(_doc)})");
                    Next();
                    break;
                }
                case 12:
                {
                    if (Since < 3) break;
                    var theirs = SeenValue("B:fp=");
                    if (theirs == null && Since < 60) break;
                    var mine = Fingerprint(_doc);
                    Check(theirs == mine, "WHOLE MODEL identical on A and B (every wall's exact x/y/z)");
                    W("A walls: " + mine);
                    if (theirs != mine) W("B walls: " + theirs);
                    W("DONE");
                    _stage = -1;
                    break;
                }
            }
        }

        // ------------------------------------------------------------ B: joins
        void StepB(UIApplication uiapp)
        {
            var central = S("central");
            switch (_stage)
            {
                case 0:
                    if (Login()) Next("logged in");
                    break;
                case 1:
                {
                    if (Seen("A:ready") == null) break;
                    var r = App.Hub.Get("/api/projects").GetAwaiter().GetResult();
                    var p = r.Data?["projects"]?.AsArray().LastOrDefault(x => x?["kind"]?.GetValue<string>() == "revit");
                    if (p == null || !File.Exists(central)) break;
                    _pid = p["id"].GetValue<long>();
                    Next($"found project {_pid}");
                    break;
                }
                case 2:
                    OpenLocal(uiapp, central, S("local"));
                    Next($"local opened (walls={Walls(_doc)})");
                    break;
                case 3:
                    if (App.Live.Session == null) break;
                    Post("B:live");
                    Next("live session started");
                    break;
                case 4:
                    if (Arrived("A:added", () => WallAt(_doc, 0) != null, "A's new wall reached B")) Next();
                    break;
                case 5:
                    AddWall(_doc, 10);
                    Post("B:added");
                    Next("added wall at y=10");
                    break;
                case 6:
                    if (SyncedSince(_wallAt)) Next($"synced my wall {(App.Live.Session.LastSyncUtc - _wallAt).TotalSeconds:0.0}s after drawing it");
                    break;
                case 7:
                    if (Arrived("A:moved", () => WallAt(_doc, 5) != null && WallAt(_doc, 0) == null, "A's move reached B (wall exactly at y=5)")) Next();
                    break;
                case 8:
                {
                    var w = WallAt(_doc, 10);
                    using (var t = new Transaction(_doc, "delete wall"))
                    {
                        t.Start();
                        _doc.Delete(w.Id);
                        t.Commit();
                    }
                    _wallAt = DateTime.UtcNow;
                    Post("B:deleted");
                    Next("deleted my wall y=10");
                    break;
                }
                case 9:
                    if (SyncedSince(_wallAt)) Next("synced the delete");
                    break;
                case 10:
                    if (Seen("A:go") == null) break;
                    AddWall(_doc, 40);
                    Next("drew wall y=40 at the same moment as A draws y=30");
                    break;
                case 11:
                {
                    var ok = WallAt(_doc, 30) != null && WallAt(_doc, 40) != null && WallAt(_doc, 5) != null && Walls(_doc) == 3;
                    if (!ok && Since < 90) break;
                    Check(ok, $"both simultaneous walls on B after {Since:0.0}s (walls={Walls(_doc)})");
                    Post("B:fp=" + Fingerprint(_doc));
                    W("B walls: " + Fingerprint(_doc));
                    W("DONE");
                    _stage = -1;
                    break;
                }
            }
        }
    }
}
