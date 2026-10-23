using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using FormattedText = System.Windows.Media.FormattedText;

namespace TeamHub.Revit
{
    public class App : IExternalApplication
    {
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        public static Settings Settings { get; private set; }
        public static LiveSync Live { get; private set; }
        public static HubClient Hub { get; private set; }
        public static TeamPane Pane { get; private set; }
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("6F0E5C8A-3B1E-4D7C-9A55-2C1E9B7D4A11"));
        public static string RevitVersion { get; private set; } = "";
        public static string RevitUser { get; private set; } = "";
        public static UIControlledApplication UiApp { get; private set; }

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                UiApp = app;
                Settings = Settings.Load();
                RevitVersion = app.ControlledApplication.VersionNumber;
                Live = new LiveSync { RevitVersion = RevitVersion };
                Pane = new TeamPane();
                app.RegisterDockablePane(PaneId, "Team Hub", Pane);
                Live.Changed += () => Pane.Refresh();

                BuildRibbon(app);

                app.ControlledApplication.DocumentOpening += OnDocumentOpening;
                app.ControlledApplication.DocumentOpened += OnDocumentOpened;
                app.ControlledApplication.DocumentClosing += OnDocumentClosing;
                app.ControlledApplication.DocumentChanged += (s, e) => Live.OnDocumentChanged(e);
                if (Settings.LoggedIn) Hub = new HubClient(Settings.Server, Settings.Token);
                SelfTest.Init();
                Log.Info($"Team Hub {Version} loaded in Revit {RevitVersion}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("startup", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            try { Live?.Stop(); } catch { }
            return Result.Succeeded;
        }

        public static void SetHub(HubClient hub)
        {
            Live.Stop();
            Hub = hub;
        }

        // ------------------------------------------------------------ ribbon

        void BuildRibbon(UIControlledApplication app)
        {
            const string tab = "Team Hub";
            app.CreateRibbonTab(tab);
            var panel = app.CreateRibbonPanel(tab, "الفريق");
            var asm = Assembly.GetExecutingAssembly().Location;

            PushButtonData Btn(string name, string text, Type cmd, string tip, string glyph, Color color) =>
                new PushButtonData(name, text, asm, cmd.FullName)
                {
                    ToolTip = tip,
                    LargeImage = Icon(glyph, color, 32),
                    Image = Icon(glyph, color, 16),
                };

            panel.AddItem(Btn("th_pane", "لوحة\nالفريق", typeof(CmdPane), "منو متصل، شنو صار، الدردشة", "◆", Color.FromRgb(194, 65, 12)));
            panel.AddItem(Btn("th_join", "انضمام\nلمشروع", typeof(CmdJoin), "يسويلك نسخة محلية من الملف الرئيسي ويفتحها", "⇩", Color.FromRgb(21, 128, 61)));
            panel.AddItem(Btn("th_sync", "مزامنة\nهسه", typeof(CmdSyncNow), "زامن هسه بدون ما تنتظر", "⟳", Color.FromRgb(37, 99, 235)));
            panel.AddItem(Btn("th_live", "لايف\nتشغيل/إيقاف", typeof(CmdLiveToggle), "تشغيل أو إيقاف المزامنة التلقائية", "●", Color.FromRgb(220, 38, 38)));
            panel.AddSeparator();
            panel.AddItem(Btn("th_create", "مشروع\nفريق جديد", typeof(CmdCreate), "للمدير: يحول الملف المفتوح لمشروع فريق (ملف رئيسي مشترك)", "+", Color.FromRgb(107, 114, 128)));
            panel.AddItem(Btn("th_login", "الحساب", typeof(CmdLogin), "تسجيل الدخول لسيرفر Team Hub", "☺", Color.FromRgb(107, 114, 128)));
            panel.AddItem(Btn("th_versions", "مساعد\nالإصدارات", typeof(CmdVersionHelp), "إذا الفريق على إصدارات Revit مختلفة", "⇄", Color.FromRgb(107, 114, 128)));
        }

        static BitmapSource Icon(string glyph, Color color, int size)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var r = size / 2.0;
                dc.DrawEllipse(new SolidColorBrush(color), null, new Point(r, r), r - 0.5, r - 0.5);
                var ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Symbol"), size * 0.55, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(r - ft.Width / 2, r - ft.Height / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        // ------------------------------------------------------------ version guard

        /// <summary>
        /// Opening a workshared model in a newer Revit upgrades it, and an upgraded
        /// local can never sync with its central again. Stop that before it happens.
        /// </summary>
        void OnDocumentOpening(object sender, DocumentOpeningEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(e.PathName) || !File.Exists(e.PathName)) return;
                if (!e.PathName.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) return;
                var info = BasicFileInfo.Extract(e.PathName);
                if (info.IsSavedInCurrentVersion || !info.IsWorkshared) return;

                var fileVersion = info.Format;
                if (info.IsSavedInLaterVersion)
                {
                    TaskDialog.Show("Team Hub", $"هذا الملف محفوظ بـ Revit {fileVersion}، أحدث من نسختك (Revit {RevitVersion}). ما ينفتح هنا.\n\n" +
                        "الحل: نصّب Revit " + fileVersion + "، أو استخدم «مساعد الإصدارات».");
                    return;
                }
                var td = new TaskDialog("Team Hub — انتبه")
                {
                    MainInstruction = $"هذا مشروع فريق محفوظ بـ Revit {fileVersion}",
                    MainContent = $"انت فاتح Revit {RevitVersion}. إذا فتحته هنا، Revit راح يرقيه لـ {RevitVersion}، " +
                                  "وبعدها ما تكدر تزامن ويا الفريق أبداً، والترقية ما ترجع.\n\n" +
                                  $"الصحيح: افتحه بـ Revit {fileVersion}.",
                    CommonButtons = TaskDialogCommonButtons.None,
                    DefaultButton = TaskDialogResult.CommandLink1,
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "لا تفتحه (الأفضل)");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "افتحه وأرقّيه (أعرف شنو أسوي)");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "مساعد الإصدارات");
                var res = td.Show();
                if (res == TaskDialogResult.CommandLink2) return;
                if (e.Cancellable) e.Cancel();
                if (res == TaskDialogResult.CommandLink3) CmdVersionHelp.ShowHelp();
            }
            catch (Exception ex) { Log.Error("version guard", ex); }
        }

        // ------------------------------------------------------------ session detection

        void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null || doc.IsFamilyDocument || !doc.IsWorkshared || doc.IsDetached || doc.IsLinked) return;
                RevitUser = doc.Application.Username;
                Live.RevitUser = RevitUser;
                var central = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath());
                var local = doc.PathName;
                if (string.Equals(Path.GetFullPath(local), SafeFull(central), StringComparison.OrdinalIgnoreCase))
                {
                    Pane.Warning = "انت فاتح الملف الرئيسي نفسه. للشغل الجماعي افتح نسخة محلية من «انضمام لمشروع».";
                    Pane.Refresh();
                    return;
                }
                if (Hub == null || !Settings.LoggedIn) return;

                Settings.LocalFiles.TryGetValue(local.ToLowerInvariant(), out var knownPid);
                var hub = Hub;
                Task.Run(async () =>
                {
                    var r = await hub.Get("/api/projects").ConfigureAwait(false);
                    if (!r.Ok) { Log.Info("projects lookup failed: " + r.Error); return; }
                    var projects = r.Data?["projects"]?.AsArray();
                    var p = projects?.FirstOrDefault(x =>
                        x?["kind"]?.GetValue<string>() == "revit" &&
                        ((knownPid != 0 && x["id"]?.GetValue<long>() == knownPid) ||
                         SamePath(x["central_path"]?.GetValue<string>(), central)));
                    if (p == null) return;
                    Live.Start(new LiveSession
                    {
                        Doc = doc,
                        ProjectId = p["id"].GetValue<long>(),
                        ProjectName = p["name"]?.GetValue<string>(),
                        CentralPath = central,
                        ProjectVersion = p["revit_version"]?.GetValue<string>(),
                    }, hub);
                });
            }
            catch (Exception ex) { Log.Error("document opened", ex); }
        }

        void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            try
            {
                var s = Live.Session;
                if (s != null && s.Doc != null && s.Doc.IsValidObject && s.Doc.Equals(e.Document))
                {
                    if (s.Dirty)
                        TaskDialog.Show("Team Hub", "عندك تعديلات ما تزامنت بعد. Revit راح يسألك إذا تريد تزامن قبل ما يسكر، اختار Synchronize.");
                    Live.Stop();
                }
            }
            catch (Exception ex) { Log.Error("document closing", ex); }
        }

        static string SafeFull(string p)
        {
            try { return Path.GetFullPath(p); } catch { return p ?? ""; }
        }

        public static bool SamePath(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(SafeFull(a).TrimEnd('\\'), SafeFull(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open url", ex); }
        }
    }
}
