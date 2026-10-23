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
                if (Since > 240) { W($"TIMEOUT at stage {_stage}"); _stage = -1; return; }
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

        void AddWall(Document doc, double y)
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
            using var t = new Transaction(doc, "Team Hub test wall");
            t.Start();
            Wall.Create(doc, Line.CreateBound(new XYZ(0, y, 0), new XYZ(20, y, 0)), level.Id, false);
            t.Commit();
            _wallAt = DateTime.UtcNow;
        }

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
                {
                    // Revit can't hold a central and its local in one session: close central first.
                    _doc.Close(false);
                    OpenLocal(uiapp, central, S("local"));
                    Next("central closed, local opened");
                    break;
                }
                case 3:
                    if (App.Live.Session != null) Next("live session started");
                    break;
                case 4:
                    if (Since < 8) break;
                    AddWall(_doc, 0);
                    Next($"added wall #1 (walls={Walls(_doc)})");
                    break;
                case 5:
                    if (App.Live.Session.LastSyncUtc > _wallAt)
                        Next($"SYNCED wall #1 {(App.Live.Session.LastSyncUtc - _wallAt).TotalSeconds:0.0}s after the edit (sync took {App.Live.Session.LastDuration.TotalSeconds:0.0}s)");
                    break;
                case 6:
                    if (Walls(_doc) >= 2) Next($"SEES B's wall (walls={Walls(_doc)}) — received automatically");
                    break;
                case 7:
                    W("DONE");
                    _stage = -1;
                    break;
            }
        }

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
                    var r = App.Hub.Get("/api/projects").GetAwaiter().GetResult();
                    var p = r.Data?["projects"]?.AsArray().LastOrDefault(x => x?["kind"]?.GetValue<string>() == "revit");
                    if (p == null || !File.Exists(central)) break;
                    _pid = p["id"].GetValue<long>();
                    Next($"found project {_pid}");
                    break;
                }
                case 2:
                    if (Since < 4) break; // let A finish closing its central
                    OpenLocal(uiapp, central, S("local"));
                    Next($"local opened (walls={Walls(_doc)})");
                    break;
                case 3:
                    if (App.Live.Session != null) Next("live session started");
                    break;
                case 4:
                    if (Walls(_doc) >= 1) Next($"SEES A's wall (walls={Walls(_doc)}) — received automatically");
                    break;
                case 5:
                    if (Since < 3) break;
                    AddWall(_doc, 10);
                    Next($"added wall #2 (walls={Walls(_doc)})");
                    break;
                case 6:
                    if (App.Live.Session.LastSyncUtc > _wallAt)
                        Next($"SYNCED wall #2 {(App.Live.Session.LastSyncUtc - _wallAt).TotalSeconds:0.0}s after the edit (sync took {App.Live.Session.LastDuration.TotalSeconds:0.0}s)");
                    break;
                case 7:
                    W("DONE");
                    _stage = -1;
                    break;
            }
        }
    }
}
