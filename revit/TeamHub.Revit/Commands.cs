using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Control = System.Windows.Controls.Control;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace TeamHub.Revit
{
    // ------------------------------------------------------------------ small form window

    /// <summary>A simple right-to-left form: labelled fields or a list, OK/Cancel.</summary>
    public class Form : Window
    {
        readonly StackPanel _body = new StackPanel { Margin = new Thickness(14) };
        readonly Dictionary<string, Control> _fields = new Dictionary<string, Control>();
        public ListBox List { get; private set; }

        public Form(string title, IntPtr owner)
        {
            Title = title;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            FlowDirection = FlowDirection.RightToLeft;
            Content = _body;
            new WindowInteropHelper(this).Owner = owner;
        }

        public Form Text(string text, bool muted = false)
        {
            _body.Children.Add(new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                Foreground = muted ? System.Windows.Media.Brushes.Gray : System.Windows.Media.Brushes.Black,
            });
            return this;
        }

        public Form Field(string key, string label, string value = "", bool password = false, bool ltr = false)
        {
            _body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
            Control c = password ? new PasswordBox { Password = value ?? "" } : (Control)new TextBox { Text = value ?? "" };
            c.Padding = new Thickness(4);
            c.Margin = new Thickness(0, 0, 0, 10);
            if (ltr || password) c.FlowDirection = FlowDirection.LeftToRight;
            _fields[key] = c;
            _body.Children.Add(c);
            return this;
        }

        public Form ListOf(IEnumerable<string> items)
        {
            List = new ListBox { Height = 180, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var i in items) List.Items.Add(i);
            if (List.Items.Count > 0) List.SelectedIndex = 0;
            List.MouseDoubleClick += (s, e) => { DialogResult = true; };
            _body.Children.Add(List);
            return this;
        }

        public Form Buttons(string ok = "تمام", string cancel = "إلغاء")
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            var okB = new Button { Content = ok, IsDefault = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0) };
            okB.Click += (s, e) => DialogResult = true;
            var cB = new Button { Content = cancel, IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
            row.Children.Add(okB);
            row.Children.Add(cB);
            _body.Children.Add(row);
            return this;
        }

        public string this[string key] =>
            _fields.TryGetValue(key, out var c) ? (c is PasswordBox p ? p.Password : ((TextBox)c).Text)?.Trim() : null;

        public bool Ask() => ShowDialog() == true;
    }

    static class Ui
    {
        public static IntPtr Owner(Ctx d) => d.Application.MainWindowHandle;

        public static void Info(string text, string title = "Team Hub") => TaskDialog.Show(title, text);

        public static bool RequireLogin(Ctx d)
        {
            if (App.Settings.LoggedIn && App.Hub != null) return true;
            return CmdLogin.ShowLogin(d);
        }

        public static string LocalFolder(string project)
        {
            var safe = string.Join("_", project.Split(Path.GetInvalidFileNameChars()));
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TeamHub", safe);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string SafeName(string s) => string.Join("_", (s ?? "user").Split(Path.GetInvalidFileNameChars()));

        public static string ServerHost()
        {
            try { return new Uri(App.Settings.Server).Host; } catch { return "SERVER"; }
        }

        public static string CentralUnreachableHelp(string central)
        {
            var share = central;
            try { share = Path.GetDirectoryName(central); } catch { }
            return "ما أكدر أوصل للملف الرئيسي:\n" + central + "\n\n" +
                   "1) تأكد إن Radmin VPN شغال ومتصل بشبكة الفريق.\n" +
                   $"2) افتح File Explorer واكتب بالعنوان: {share}\n" +
                   "3) إذا سألك عن اسم مستخدم وكلمة سر، اكتب حساب الفولدر المشترك (من صاحب السيرفر) وفعّل «Remember my credentials».\n" +
                   "4) ارجع دوس «انضمام لمشروع» مرة ثانية.";
        }
    }

    // ------------------------------------------------------------------ commands

    [Transaction(TransactionMode.Manual)]
    public class CmdPane : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
            Run(new Ctx(data.Application), ref message);

        public static Result Run(Ctx d, ref string message)
        {
            try { d.Application.GetDockablePane(App.PaneId).Show(); App.Pane.Refresh(); }
            catch (Exception ex) { Log.Error("pane", ex); }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CmdLogin : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
            Run(new Ctx(data.Application), ref message);

        public static Result Run(Ctx d, ref string message)
        {
            if (App.Settings.LoggedIn)
            {
                var td = new TaskDialog("Team Hub")
                {
                    MainInstruction = $"داخل باسم {App.Settings.DisplayName}",
                    MainContent = $"السيرفر: {App.Settings.Server}",
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "تبديل الحساب / سيرفر ثاني");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "تسجيل خروج");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "فتح لوحة التحكم بالمتصفح");
                var r = td.Show();
                if (r == TaskDialogResult.CommandLink2)
                {
                    App.SetHub(null);
                    App.Settings.Token = "";
                    App.Settings.Save();
                    App.Pane.Refresh();
                }
                else if (r == TaskDialogResult.CommandLink3) App.OpenUrl(App.Settings.Server);
                else if (r == TaskDialogResult.CommandLink1) ShowLogin(d);
                return Result.Succeeded;
            }
            ShowLogin(d);
            return Result.Succeeded;
        }

        public static bool ShowLogin(Ctx d)
        {
            var f = new Form("Team Hub — تسجيل الدخول", Ui.Owner(d))
                .Text("عنوان السيرفر تاخذه من صاحب السيرفر، مثل http://26.x.x.x:8787. إذا تشتغل من البيت، خلي Radmin VPN شغال.", muted: true)
                .Field("server", "عنوان السيرفر", App.Settings.Server, ltr: true)
                .Field("user", "اسم المستخدم", App.Settings.Username, ltr: true)
                .Field("pass", "كلمة السر", "", password: true)
                .Buttons("دخول");
            if (!f.Ask()) return false;

            var hub = new HubClient(f["server"], null);
            var r = hub.Post("/api/login", new { username = f["user"], password = f["pass"], client = "revit" }).GetAwaiter().GetResult();
            if (!r.Ok)
            {
                Ui.Info(r.Status == 0
                    ? $"ما كدرت أوصل للسيرفر {hub.Server}.\nتأكد إن السيرفر شغال، وRadmin VPN متصل، والعنوان صحيح."
                    : "اسم المستخدم أو كلمة السر غلط.");
                return false;
            }
            var u = r.Data["user"];
            App.Settings.Server = hub.Server;
            App.Settings.Token = r.Data["token"].GetValue<string>();
            App.Settings.Username = f["user"];
            App.Settings.UserId = u["id"].GetValue<long>();
            App.Settings.DisplayName = u["display_name"]?.GetValue<string>();
            App.Settings.Role = u["role"]?.GetValue<string>();
            App.Settings.Save();
            hub.Token = App.Settings.Token;
            App.SetHub(hub);
            App.Pane.Refresh();
            Ui.Info($"أهلاً {App.Settings.DisplayName}! هسه دوس «انضمام لمشروع».");
            return true;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CmdJoin : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
            Run(new Ctx(data.Application), ref message);

        public static Result Run(Ctx d, ref string message)
        {
            try
            {
                if (!Ui.RequireLogin(d)) return Result.Cancelled;
                var r = App.Hub.Get("/api/projects").GetAwaiter().GetResult();
                if (!r.Ok) { Ui.Info("ما كدرت أجيب المشاريع من السيرفر: " + r.Error); return Result.Failed; }
                var projects = r.Data["projects"].AsArray()
                    .Where(p => p?["kind"]?.GetValue<string>() == "revit").ToList();
                if (projects.Count == 0)
                {
                    Ui.Info("ماكو مشاريع Revit على السيرفر بعد. المدير يسوي مشروع من «مشروع فريق جديد».");
                    return Result.Cancelled;
                }
                var f = new Form("Team Hub — انضمام لمشروع", Ui.Owner(d))
                    .Text($"انت على Revit {App.RevitVersion}. اختار المشروع:")
                    .ListOf(projects.Select(p => $"{p["name"]}   ·   Revit {p["revit_version"]}   ·   {p["online"]} متصل"))
                    .Buttons("انضمام");
                if (!f.Ask() || f.List.SelectedIndex < 0) return Result.Cancelled;
                var proj = projects[f.List.SelectedIndex];
                return Join(d.Application, proj);
            }
            catch (Exception ex)
            {
                Log.Error("join", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }

        public static Result Join(UIApplication uiapp, JsonNode proj)
        {
            var name = proj["name"].GetValue<string>();
            var version = proj["revit_version"]?.GetValue<string>();
            var central = proj["central_path"]?.GetValue<string>();
            var pid = proj["id"].GetValue<long>();

            if (version != App.RevitVersion)
            {
                var td = new TaskDialog("Team Hub")
                {
                    MainInstruction = $"المشروع «{name}» على Revit {version}",
                    MainContent = $"انت فاتح Revit {App.RevitVersion}. كل الفريق لازم يشتغل بنفس الإصدار بالمشروع الواحد.\n\n" +
                                  $"افتح Revit {version} ودوس «انضمام لمشروع» من هناك. تكدر تنصب أكثر من إصدار Revit على نفس الجهاز.",
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "مساعد الإصدارات");
                if (td.Show() == TaskDialogResult.CommandLink1) CmdVersionHelp.ShowHelp();
                return Result.Cancelled;
            }
            if (HostedCentral.IsHosted(central))
            {
                // Central lives on the server: download my copy of it (only if newer).
                var file = HostedCentral.CentralFile(name, central);
                try { HostedCentral.EnsureLatest(App.Hub, pid, file).GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    Log.Error("central download", ex);
                    Ui.Info("ما كدرت أحمّل الملف الرئيسي من السيرفر:\n" + ex.Message);
                    return Result.Failed;
                }
                central = file;
            }
            if (string.IsNullOrEmpty(central) || !File.Exists(central))
            {
                Ui.Info(Ui.CentralUnreachableHelp(central));
                return Result.Failed;
            }

            var user = uiapp.Application.Username;
            var local = Path.Combine(Ui.LocalFolder(name), $"{Path.GetFileNameWithoutExtension(central)}_{Ui.SafeName(user)}.rvt");
            if (File.Exists(local))
            {
                var td = new TaskDialog("Team Hub")
                {
                    MainInstruction = "عندك نسخة محلية من قبل",
                    MainContent = local,
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "سوي نسخة جديدة من الملف الرئيسي (الأفضل)");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "افتح النسخة القديمة");
                var res = td.Show();
                if (res == TaskDialogResult.CommandLink1)
                {
                    var bak = local.Replace(".rvt", $"_old_{DateTime.Now:yyyyMMdd_HHmmss}.rvt");
                    File.Move(local, bak);
                }
                else if (res != TaskDialogResult.CommandLink2) return Result.Cancelled;
            }
            if (!File.Exists(local))
            {
                WorksharingUtils.CreateNewLocal(
                    ModelPathUtils.ConvertUserVisiblePathToModelPath(central),
                    ModelPathUtils.ConvertUserVisiblePathToModelPath(local));
            }
            App.Settings.LocalFiles[local.ToLowerInvariant()] = pid;
            App.Settings.Save();

            var opts = new OpenOptions();
            opts.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
            uiapp.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(local), opts, false);
            try { uiapp.GetDockablePane(App.PaneId).Show(); } catch { }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CmdCreate : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
            Run(new Ctx(data.Application), ref message);

        public static Result Run(Ctx d, ref string message)
        {
            try
            {
                if (!Ui.RequireLogin(d)) return Result.Cancelled;
                if (App.Settings.Role != "admin") { Ui.Info("بس المدير يكدر يسوي مشروع فريق."); return Result.Cancelled; }
                var uidoc = d.Application.ActiveUIDocument;
                if (uidoc == null || uidoc.Document.IsFamilyDocument)
                {
                    Ui.Info("افتح مشروع Revit (مو عائلة) اللي تريد الفريق يشتغل عليه، وبعدين دوس هذا الزر.");
                    return Result.Cancelled;
                }
                var doc = uidoc.Document;
                var title = Path.GetFileNameWithoutExtension(doc.Title);
                bool already = doc.IsWorkshared && !doc.IsDetached;
                var f = new Form("Team Hub — مشروع فريق جديد", Ui.Owner(d))
                    .Text(already
                        ? "هذا الملف مشترك (Workshared) من قبل. راح أسجل ملفه الرئيسي بالسيرفر حتى الفريق ينضم."
                        : "راح أرفع هذا الملف للسيرفر كملف رئيسي للفريق (مثل SketchUp): الفريق ينزّله من «انضمام لمشروع»، " +
                          "والمزامنة تمر عن طريق السيرفر. ما يحتاج فولدر مشترك. ملفك الأصلي يبقى مثل ما هو.", muted: true)
                    .Field("name", "اسم المشروع", title)
                    .Text($"الإصدار: Revit {App.RevitVersion}. كل الفريق لازم يشتغل بنفس الإصدار.", muted: true)
                    .Buttons("إنشاء");
                if (!f.Ask()) return Result.Cancelled;
                var name = f["name"];
                if (string.IsNullOrEmpty(name)) return Result.Cancelled;

                ApiResult r;
                if (already)
                {
                    var existing = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath());
                    r = App.Hub.Post("/api/projects", new { name, kind = "revit", central_path = existing, revit_version = App.RevitVersion })
                               .GetAwaiter().GetResult();
                    if (!r.Ok) { Ui.Info("السيرفر ما سجل المشروع: " + r.Error); return Result.Failed; }
                }
                else
                {
                    r = App.Hub.Post("/api/projects", new { name, kind = "revit", hosted = true, revit_version = App.RevitVersion })
                               .GetAwaiter().GetResult();
                    if (!r.Ok) { Ui.Info("السيرفر ما سجل المشروع: " + r.Error); return Result.Failed; }
                    var pid = r.Data["project"]["id"].GetValue<long>();
                    var central = HostedCentral.CentralFile(name, r.Data["project"]["central_path"].GetValue<string>());
                    // A project folder with this name may hold an older central: start clean.
                    foreach (var old in Directory.GetFileSystemEntries(Path.GetDirectoryName(central)))
                    {
                        if (Directory.Exists(old)) Directory.Delete(old, true); else File.Delete(old);
                    }
                    if (doc.IsModified && !string.IsNullOrEmpty(doc.PathName)) doc.Save();
                    if (!doc.IsWorkshared) doc.EnableWorksharing("Shared Levels and Grids", "Workset1");
                    var sao = new SaveAsOptions { OverwriteExistingFile = true };
                    sao.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                    doc.SaveAs(central, sao);
                    HostedCentral.Upload(App.Hub, pid, central, 0).GetAwaiter().GetResult();
                }

                // Work in a local copy, never directly in central. Revit can't open a local
                // while its central is open, and can't close the active document, so step
                // through a throw-away placeholder document.
                Result res;
                if (!already)
                {
                    var placeholderPath = Path.Combine(Path.GetTempPath(), $"teamhub_placeholder_{Guid.NewGuid():N}.rvt");
                    var placeholder = d.Application.Application.NewProjectDocument(UnitSystem.Metric);
                    placeholder.SaveAs(placeholderPath);
                    placeholder.Close(false);
                    var ph = d.Application.OpenAndActivateDocument(placeholderPath);
                    doc.Close(false);
                    res = CmdJoin.Join(d.Application, r.Data["project"]);
                    try { ph.Document.Close(false); File.Delete(placeholderPath); } catch (Exception ex) { Log.Error("placeholder", ex); }
                }
                else
                {
                    res = CmdJoin.Join(d.Application, r.Data["project"]);
                }
                Ui.Info($"تم! مشروع «{name}» جاهز.\nالفريق يدوس «انضمام لمشروع» ويختاره.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("create", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CmdSyncNow : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
            Run(new Ctx(data.Application), ref message);

        public static Result Run(Ctx d, ref string message)
        {
            if (App.Live.Session == null) { Ui.Info("مو متصل بمشروع فريق. افتح المشروع من «انضمام لمشروع»."); return Result.Cancelled; }
            App.Live.SyncNow();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CmdLiveToggle : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
            Run(new Ctx(data.Application), ref message);

        public static Result Run(Ctx d, ref string message)
        {
            App.Settings.Live = !App.Settings.Live;
            App.Settings.Save();
            App.Pane.Refresh();
            Ui.Info(App.Settings.Live
                ? $"اللايف شغال: كل {App.Settings.SyncIntervalSeconds:0} ثانية تعديلاتك تنرفع وتعديلات الفريق تنزل عندك، سوة، وبس من توكف لحظة حتى ما تتقطع بشغلك."
                : "اللايف متوقف. زامن يدوي من «مزامنة هسه».");
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CmdVersionHelp : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
            Run(new Ctx(data.Application), ref message);

        public static Result Run(Ctx d, ref string message)
        {
            ShowHelp();
            return Result.Succeeded;
        }

        public static void ShowHelp()
        {
            var td = new TaskDialog("Team Hub — مساعد الإصدارات")
            {
                MainInstruction = "كل مشروع فريق يشتغل على إصدار Revit واحد",
                MainContent =
                    "Revit ما يكدر يحفظ بإصدار أقدم، والملف اللي ينفتح بإصدار أحدث يترقى وما يرجع.\n\n" +
                    "• الأفضل: كل الفريق ينصب نفس الإصدار. تكدر تنصب أكثر من إصدار على نفس الجهاز.\n" +
                    "• إذا لازم تنزل ملف لـ Revit 2023: موقع rvtconvert.com يحوله مرة وحدة، مجاناً لغاية 1 GB. " +
                    "بعدها تسوي منه مشروع فريق جديد على 2023.\n" +
                    "  انتبه: الموقع مستقل (مو من Autodesk) وملفك يترفع لسيرفرهم. بعد التحويل افتحه بـ «Detach from Central».",
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "فتح rvtconvert.com");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "تمام");
            if (td.Show() == TaskDialogResult.CommandLink1) App.OpenUrl("https://rvtconvert.com/");
        }
    }

    /// <summary>What a command needs from Revit — built from ExternalCommandData
    /// (add-in buttons) or from the UIApplication pyRevit hands its scripts.</summary>
    public class Ctx
    {
        public UIApplication Application { get; }
        public Ctx(UIApplication app) { Application = app; }
    }

    /// <summary>The pyRevit extension's buttons call these (TeamHub.extension).</summary>
    public static class PyRevit
    {
        public static string Run(string command, UIApplication app)
        {
            var message = "";
            var d = new Ctx(app);
            var r = command switch
            {
                "pane" => CmdPane.Run(d, ref message),
                "join" => CmdJoin.Run(d, ref message),
                "sync" => CmdSyncNow.Run(d, ref message),
                "live" => CmdLiveToggle.Run(d, ref message),
                "create" => CmdCreate.Run(d, ref message),
                "login" => CmdLogin.Run(d, ref message),
                "versions" => CmdVersionHelp.Run(d, ref message),
                _ => Result.Failed,
            };
            return r == Result.Failed && !string.IsNullOrEmpty(message) ? message : r.ToString();
        }
    }
}
