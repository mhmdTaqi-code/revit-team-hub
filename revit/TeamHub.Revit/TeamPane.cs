using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

namespace TeamHub.Revit
{
    /// <summary>Dockable "Team Hub" pane: live state, team, activity and chat. Built in code (no XAML).</summary>
    public class TeamPane : IDockablePaneProvider
    {
        readonly Border _root;
        readonly TextBlock _title, _state, _detail, _warn;
        readonly StackPanel _team, _feed;
        readonly Button _liveBtn, _syncBtn;
        readonly Border _updateBar;
        readonly TextBlock _updateText;
        readonly Button _updateBtn;
        readonly TextBox _chat;
        public string Warning { get; set; }

        static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(194, 65, 12));
        static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(21, 128, 61));
        static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(185, 28, 28));
        static readonly Brush WarnBg = new SolidColorBrush(Color.FromRgb(255, 246, 229));

        public TeamPane()
        {
            var stack = new StackPanel { Margin = new Thickness(10) };
            _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            _state = new TextBlock { FontSize = 13, Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.SemiBold };
            _detail = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            _warn = new TextBlock { TextWrapping = TextWrapping.Wrap, Background = WarnBg, Padding = new Thickness(8),
                                    Margin = new Thickness(0, 8, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14)) };

            var buttons = new UniformGrid2(2);
            _liveBtn = MakeButton("", () => { App.Settings.Live = !App.Settings.Live; App.Settings.Save(); Refresh(); });
            _syncBtn = MakeButton("مزامنة هسه", () => App.Live.SyncNow());
            buttons.Children.Add(_liveBtn);
            buttons.Children.Add(_syncBtn);
            buttons.Margin = new Thickness(0, 10, 0, 0);

            _team = new StackPanel();
            _feed = new StackPanel();
            _chat = new TextBox { Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(4) };
            _chat.KeyDown += (s, e) => { if (e.Key == Key.Enter) SendChat(); };
            var chatRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var send = MakeButton("إرسال", SendChat);
            DockPanel.SetDock(send, Dock.Left);
            chatRow.Children.Add(send);
            chatRow.Children.Add(_chat);

            stack.Children.Add(_title);
            stack.Children.Add(_state);
            stack.Children.Add(_detail);
            stack.Children.Add(_warn);

            // A newer version is on the server: offer it, install only on request.
            _updateText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            _updateBtn = MakeButton("تحديث", async () =>
            {
                try { await Updater.Install(); }
                catch (Exception ex)
                {
                    Log.Error("update install", ex);
                    Warning = "ما كمل التحديث: " + ex.Message;
                }
                Refresh();
            });
            var updateRow = new DockPanel();
            DockPanel.SetDock(_updateBtn, Dock.Left);
            updateRow.Children.Add(_updateBtn);
            updateRow.Children.Add(_updateText);
            _updateBar = new Border
            {
                Child = updateRow, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(224, 242, 254)), Visibility = Visibility.Collapsed,
            };
            stack.Children.Add(_updateBar);
            stack.Children.Add(buttons);
            stack.Children.Add(Header("الفريق المتصل"));
            stack.Children.Add(_team);
            stack.Children.Add(Header("النشاط والدردشة"));
            stack.Children.Add(chatRow);
            stack.Children.Add(_feed);

            _root = new Border
            {
                Background = Brushes.White,
                FlowDirection = FlowDirection.RightToLeft,
                Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            };
            TextOptions.SetTextFormattingMode(_root, TextFormattingMode.Display);
            Render();
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _root;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        }

        static TextBlock Header(string text) => new TextBlock
        {
            Text = text, Foreground = Muted, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4),
        };

        static Button MakeButton(string text, Action onClick)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(2) };
            b.Click += (s, e) =>
            {
                try { onClick(); } catch (Exception ex) { Log.Error("pane button", ex); }
            };
            return b;
        }

        void SendChat()
        {
            var t = _chat.Text?.Trim();
            if (string.IsNullOrEmpty(t)) return;
            App.Live.SendChat(t);
            _chat.Text = "";
        }

        public void Refresh()
        {
            try
            {
                if (_root.Dispatcher.CheckAccess()) Render();
                else _root.Dispatcher.BeginInvoke(new Action(Render));
            }
            catch (Exception ex) { Log.Error("pane refresh", ex); }
        }

        void Render()
        {
            var live = App.Live;
            var s = live?.Session;
            var settings = App.Settings;

            if (settings == null || !settings.LoggedIn)
            {
                _title.Text = "Team Hub";
                _state.Text = "مو داخل حساب";
                _state.Foreground = Muted;
                _detail.Text = "دوس «الحساب» من تبويب Team Hub وسجل دخول بعنوان السيرفر واسمك وكلمة السر.";
            }
            else if (s == null)
            {
                _title.Text = "Team Hub";
                _state.Text = "مو متصل بمشروع";
                _state.Foreground = Muted;
                _detail.Text = $"أهلاً {settings.DisplayName}. افتح مشروع الفريق من «انضمام لمشروع»، أو افتح نسختك المحلية وراح يتصل وحده.";
            }
            else
            {
                _title.Text = s.ProjectName;
                var (txt, brush) = StateText(s, settings.Live, live.HubConnected);
                _state.Text = txt;
                _state.Foreground = brush;
                var last = s.LastSyncUtc == default ? "بعد ما صارت مزامنة" : $"آخر مزامنة {s.LastSyncUtc.ToLocalTime():HH:mm:ss} ({s.LastDuration.TotalSeconds:0.0} ث)";
                _detail.Text = $"Revit {App.RevitVersion} · اسمك بـ Revit: {App.RevitUser}\n{last}" +
                               (s.Dirty ? $"\nتعديلات تنتظر: {s.Added + s.Modified + s.Deleted}" : "") +
                               (s.LastError != null ? $"\n⚠ {s.LastError}" : "");
            }

            var warn = Warning ?? live?.DuplicateRevitUserWarning();
            if (s != null && s.ProjectVersion != null && s.ProjectVersion != App.RevitVersion)
                warn = $"المشروع على Revit {s.ProjectVersion} وانت على {App.RevitVersion}.";
            _warn.Text = warn ?? "";
            _warn.Visibility = string.IsNullOrEmpty(warn) ? Visibility.Collapsed : Visibility.Visible;

            if (Updater.Installed != null)
            {
                _updateText.Text = $"تحدّث لـ {Updater.Installed} ✓ سكّر Revit وافتحه حتى يشتغل الجديد.";
                _updateBtn.Visibility = Visibility.Collapsed;
                _updateBar.Visibility = Visibility.Visible;
            }
            else if (Updater.Available != null)
            {
                _updateText.Text = Updater.Installing
                    ? $"يحمّل النسخة {Updater.Available}…"
                    : $"نسخة جديدة من Team Hub: {Updater.Available} (عندك {App.Version}).";
                _updateBtn.IsEnabled = !Updater.Installing;
                _updateBtn.Visibility = Visibility.Visible;
                _updateBar.Visibility = Visibility.Visible;
            }
            else _updateBar.Visibility = Visibility.Collapsed;

            _liveBtn.Content = settings != null && settings.Live ? "⏸ إيقاف اللايف" : "▶ تشغيل اللايف";
            _liveBtn.IsEnabled = _syncBtn.IsEnabled = s != null;

            _team.Children.Clear();
            var online = live?.Online ?? new System.Collections.Generic.List<TeamMember>();
            if (online.Count == 0) _team.Children.Add(new TextBlock { Text = "محد متصل", Foreground = Muted });
            foreach (var m in online)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 6, 0),
                                       Background = ParseBrush(m.Color), VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(dot);
                row.Children.Add(new TextBlock
                {
                    Text = $"{m.Name}{(m.Id == settings?.UserId ? " (أنت)" : "")} · {m.App}{(string.IsNullOrEmpty(m.Status) ? "" : " · " + m.Status)}",
                    TextWrapping = TextWrapping.Wrap,
                });
                _team.Children.Add(row);
            }

            _feed.Children.Clear();
            ActivityItem[] items;
            lock (live?.Activity ?? new System.Collections.Generic.List<ActivityItem>())
                items = live?.Activity.ToArray() ?? Array.Empty<ActivityItem>();
            foreach (var a in items.Reverse().Take(60))
            {
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
                tb.Inlines.Add(new System.Windows.Documents.Run(a.At.ToString("HH:mm ")) { Foreground = Muted });
                if (!string.IsNullOrEmpty(a.Who))
                    tb.Inlines.Add(new System.Windows.Documents.Run(a.Who + (a.Chat ? ": " : " ")) { Foreground = ParseBrush(a.Color), FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new System.Windows.Documents.Run(a.Text ?? ""));
                if (a.Chat) tb.Background = new SolidColorBrush(Color.FromRgb(244, 245, 247));
                _feed.Children.Add(tb);
            }
        }

        static (string, Brush) StateText(LiveSession s, bool liveOn, bool hub)
        {
            if (!liveOn) return ("⏸ اللايف متوقف، زامن يدوي", Muted);
            switch (s.State)
            {
                case "editing": return ("✎ تعدّل… راح تتزامن من توكف", Accent);
                case "waiting": return (s.WaitingFor != null ? $"⏳ ينتظر دوره ({s.WaitingFor} دا يزامن)" : "⏳ ينتظر دوره", Accent);
                case "syncing": return ("⟳ دا يزامن…", Accent);
                case "reloading": return ("⇩ دا يسحب تعديلات الفريق…", Accent);
                case "error": return ("⚠ مشكلة بالمزامنة", Bad);
            }
            if (!hub) return ("● متصل بالملف الرئيسي، السيرفر مقطوع (فحص كل 20 ثانية)", Accent);
            return (s.Dirty ? "✎ تعديلات تنتظر" : "✓ لايف، كلشي متزامن", s.Dirty ? Accent : Ok);
        }

        static Brush ParseBrush(string hex)
        {
            try { return (Brush)new BrushConverter().ConvertFromString(string.IsNullOrEmpty(hex) ? "#888888" : hex); }
            catch { return Muted; }
        }
    }

    /// <summary>Minimal uniform grid (equal columns) without pulling in Primitives.</summary>
    public class UniformGrid2 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid2(int columns) { Columns = columns; }
    }
}
