using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace TeamHub.Revit
{
    /// <summary>One open workshared local file that belongs to a hub project.</summary>
    public class LiveSession
    {
        public Document Doc;
        public long ProjectId;
        public string ProjectName;
        public string CentralPath;
        public string ProjectVersion;
        /// <summary>Central lives on the Team Hub server; CentralPath is my copy of it.</summary>
        public bool Hosted;
        public int CentralRev;

        public volatile bool Dirty;
        public volatile bool PendingReload;
        public DateTime LastChangeUtc = DateTime.UtcNow;
        public DateTime LastSyncUtc;
        /// <summary>Last round (sync or reload) — the next one is due an interval later.</summary>
        public DateTime LastRoundUtc;
        /// <summary>"Sync now" button: skip the wait for the next round.</summary>
        public volatile bool ForceNow;
        public DateTime LastCheckUtc = DateTime.UtcNow;
        public DateTime NextAttemptUtc;
        public int Added, Modified, Deleted;
        public int Failures;
        public string State = "idle"; // idle | editing | waiting | syncing | reloading | error
        public string LastError;
        public string WaitingFor;
        public TimeSpan LastDuration;
    }

    public class TeamMember
    {
        public long Id;
        public string Name;
        public string Color;
        public string App;
        public string RevitUser;
        public string Status;
    }

    public class ActivityItem
    {
        public DateTime At;
        public string Who;
        public string Color;
        public string Text;
        public bool Chat;
    }

    /// <summary>
    /// The live engine. A background timer decides *when* to sync or reload; the
    /// Revit work itself always runs inside an ExternalEvent, i.e. on Revit's API
    /// thread and only while the user is not in the middle of a command.
    /// </summary>
    public class LiveSync : IExternalEventHandler
    {
        enum Work { None, Sync, Reload, Check }

        readonly ExternalEvent _event;
        readonly Timer _timer;
        readonly object _gate = new object();
        LiveSession _s;
        HubClient _hub;
        Work _granted = Work.None;
        bool _noTurn;            // hub unreachable: sync without a turn
        volatile bool _requesting;
        volatile bool _raised;
        volatile bool _inOwnOp;
        string _lastStatus;

        public List<TeamMember> Online { get; private set; } = new List<TeamMember>();
        public List<ActivityItem> Activity { get; } = new List<ActivityItem>();
        public LiveSession Session => _s;
        public bool HubConnected => _hub?.Connected ?? false;
        public event Action Changed;

        public string RevitVersion { get; set; } = "";
        public string RevitUser { get; set; } = "";

        public LiveSync()
        {
            _event = ExternalEvent.Create(this);
            _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public string GetName() => "Team Hub live sync";

        // ------------------------------------------------------------ session

        public void Start(LiveSession s, HubClient hub)
        {
            Stop();
            lock (_gate)
            {
                _s = s;
                _hub = hub;
            }
            hub.Message += OnHubMessage;
            hub.ConnectionChanged += OnHubConnection;
            hub.StartLive(s.ProjectId);
            s.PendingReload = true; // catch up with anything synced while we were away
            _timer.Change(1000, 1000);
            AddActivity(null, null, $"اتصلت بمشروع «{s.ProjectName}»");
            PushStatus();
            Changed?.Invoke();
            Log.Info($"live session started: project {s.ProjectId} central {s.CentralPath}");
        }

        public void Stop()
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            HubClient hub;
            lock (_gate)
            {
                hub = _hub;
                _hub = null;
                _s = null;
                _granted = Work.None;
            }
            if (hub != null)
            {
                hub.Message -= OnHubMessage;
                hub.ConnectionChanged -= OnHubConnection;
                hub.StopLive();
            }
            Online = new List<TeamMember>();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------ change tracking

        public void OnDocumentChanged(DocumentChangedEventArgs e)
        {
            if (_inOwnOp) return;
            var s = _s;
            if (s == null) return;
            Document doc;
            try { doc = e.GetDocument(); } catch { return; }
            if (doc == null || !doc.Equals(s.Doc)) return;
            s.Added += e.GetAddedElementIds().Count;
            s.Modified += e.GetModifiedElementIds().Count;
            s.Deleted += e.GetDeletedElementIds().Count;
            s.Dirty = true;
            s.LastChangeUtc = DateTime.UtcNow;
            if (s.State != "syncing") s.State = "editing";
            PushStatus();
        }

        /// <summary>Ask for a sync as soon as possible (Sync Now button).</summary>
        public void SyncNow()
        {
            var s = _s;
            if (s == null) return;
            s.Dirty = true;
            s.ForceNow = true;
            s.LastChangeUtc = DateTime.MinValue;
            s.NextAttemptUtc = DateTime.MinValue;
            Tick();
        }

        // ------------------------------------------------------------ scheduler (background thread)

        void Tick()
        {
            try
            {
                var s = _s;
                var hub = _hub;
                if (s == null || hub == null || !App.Settings.Live) return;
                if (_requesting || _raised || _granted != Work.None) return;
                var now = DateTime.UtcNow;
                if (now < s.NextAttemptUtc) return;

                // Rounds, not every pause: at most one sync/reload per interval
                // (default 30 s), and only once I've stopped editing for a moment,
                // so Revit never stalls in the middle of my work. A round sends
                // my changes and brings the team's (Synchronize does both).
                var work = Work.None;
                var due = s.ForceNow || (now - s.LastRoundUtc).TotalSeconds >= App.Settings.SyncIntervalSeconds;
                var quiet = s.ForceNow || (now - s.LastChangeUtc).TotalSeconds >= App.Settings.IdleSeconds;
                if (due && quiet && s.Dirty) work = Work.Sync;
                else if (due && quiet && s.PendingReload) work = Work.Reload;
                else if (!hub.Connected && !s.Dirty && (now - s.LastCheckUtc).TotalSeconds >= 20) work = Work.Check;
                if (work == Work.None) return;
                s.ForceNow = false;

                if (work == Work.Check)
                {
                    // Hub is down: ask Revit directly whether central has news
                    // (a hosted central only changes through the hub).
                    s.LastCheckUtc = now;
                    if (!s.Hosted) Grant(work, noTurn: true);
                    return;
                }
                RequestTurn(s, hub, work);
            }
            catch (Exception ex) { Log.Error("tick", ex); }
        }

        void RequestTurn(LiveSession s, HubClient hub, Work work)
        {
            _requesting = true;
            if (work == Work.Sync) SetState(s, "waiting");
            Task.Run(async () =>
            {
                try
                {
                    var r = await hub.Post($"/api/projects/{s.ProjectId}/revit/sync/begin",
                                           new { revit_version = RevitVersion }).ConfigureAwait(false);
                    if (r.Ok && r.Data?["granted"]?.GetValue<bool>() == true)
                    {
                        s.WaitingFor = null;
                        Grant(work, noTurn: false);
                    }
                    else if (r.Ok)
                    {
                        var ms = r.Data?["retry_ms"]?.GetValue<int>() ?? 1500;
                        s.WaitingFor = r.Data?["holder"]?["display_name"]?.GetValue<string>();
                        s.NextAttemptUtc = DateTime.UtcNow.AddMilliseconds(ms);
                        SetState(s, "waiting");
                    }
                    else if (r.Status == 409)
                    {
                        s.LastError = $"المشروع على Revit {r.Data?["project_version"]} وانت فاتح Revit {RevitVersion}. المزامنة متوقفة.";
                        App.Settings.Live = false;
                        SetState(s, "error");
                    }
                    else if (r.Status == 0)
                    {
                        if (s.Hosted)
                        {
                            // Central lives on the server: wait for it (my work stays in my local file).
                            s.NextAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                            s.LastError = "السيرفر مو متصل. تعديلاتك محفوظة بملفك وراح تتزامن أول ما يرجع.";
                            SetState(s, "error");
                        }
                        // Server unreachable: still sync straight with central so no work is lost.
                        else Grant(work, noTurn: true);
                    }
                    else
                    {
                        s.NextAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                        s.LastError = "السيرفر رفض المزامنة: " + r.Error;
                        SetState(s, "error");
                    }
                }
                finally { _requesting = false; }
            });
        }

        void Grant(Work work, bool noTurn)
        {
            lock (_gate)
            {
                _granted = work;
                _noTurn = noTurn;
                _raised = true;
            }
            var res = _event.Raise();
            if (res != ExternalEventRequest.Accepted && res != ExternalEventRequest.Pending)
            {
                Log.Info("external event not accepted: " + res);
                lock (_gate) { _granted = Work.None; _raised = false; }
            }
        }

        // ------------------------------------------------------------ Revit work (API thread)

        public void Execute(UIApplication app)
        {
            Work work;
            bool noTurn;
            lock (_gate)
            {
                work = _granted;
                noTurn = _noTurn;
                _granted = Work.None;
                _raised = false;
            }
            var s = _s;
            if (work == Work.None || s == null) return;

            var doc = s.Doc;
            if (doc == null || !doc.IsValidObject)
            {
                EndTurn(s, false, work, noTurn);
                return;
            }
            if (doc.IsModifiable || doc.IsReadOnly)
            {
                // A transaction is open (user is mid-edit). Try again shortly.
                s.NextAttemptUtc = DateTime.UtcNow.AddSeconds(2);
                EndTurn(s, false, work, noTurn);
                return;
            }

            int added = s.Added, modified = s.Modified, deleted = s.Deleted;
            var sw = Stopwatch.StartNew();
            _inOwnOp = true;
            try
            {
                if (work == Work.Check)
                {
                    if (!doc.HasAllChangesFromCentral())
                    {
                        SetState(s, "reloading");
                        doc.ReloadLatest(new ReloadLatestOptions());
                        AddActivity(null, null, "انسحبت تعديلات جديدة من الملف الرئيسي");
                    }
                    SetState(s, "idle");
                    return;
                }

                SetState(s, work == Work.Sync ? "syncing" : "reloading");
                // Hosted: my copy of central must be the server's newest first.
                if (s.Hosted) s.CentralRev = HostedCentral.EnsureLatest(_hub, s.ProjectId, s.CentralPath).GetAwaiter().GetResult();
                if (work == Work.Sync)
                {
                    var twc = new TransactWithCentralOptions();
                    twc.SetLockCallback(new NoWaitForLock());
                    var swc = new SynchronizeWithCentralOptions
                    {
                        Comment = "Team Hub live sync",
                        Compact = false,
                        SaveLocalBefore = false,
                        SaveLocalAfter = true,
                    };
                    // Hand back what I borrowed so teammates can edit it right away;
                    // worksets the user took on purpose stay theirs.
                    swc.SetRelinquishOptions(new RelinquishOptions(false)
                    {
                        CheckedOutElements = true,
                        StandardWorksets = true,
                        ViewWorksets = true,
                        FamilyWorksets = true,
                        UserWorksets = false,
                    });
                    doc.SynchronizeWithCentral(twc, swc);
                    // Hosted: the new central goes to the server before the turn ends.
                    if (s.Hosted) s.CentralRev = UploadCentral(s);
                    s.Dirty = false;
                    s.Added = s.Modified = s.Deleted = 0;
                }
                else
                {
                    doc.ReloadLatest(new ReloadLatestOptions());
                }
                s.PendingReload = false;
                s.LastSyncUtc = DateTime.UtcNow;
                s.LastRoundUtc = s.LastSyncUtc;
                s.LastDuration = sw.Elapsed;
                s.LastError = null;
                s.Failures = 0;
                SetState(s, "idle");
                EndTurn(s, true, work, noTurn, added, modified, deleted, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                var name = ex.GetType().Name;
                Log.Error($"{work}", ex);
                s.Failures++;
                if (name.Contains("Contention") || name.Contains("Locked"))
                {
                    s.NextAttemptUtc = DateTime.UtcNow.AddSeconds(2);
                    s.LastError = "الملف الرئيسي مشغول، راح أحاول مرة ثانية";
                }
                else if (name.Contains("Central") || name.Contains("Communication") || ex is System.IO.IOException)
                {
                    s.NextAttemptUtc = DateTime.UtcNow.AddSeconds(Math.Min(60, 5 * s.Failures));
                    s.LastError = "ما أكدر أوصل للملف الرئيسي. تأكد من Radmin VPN والفولدر المشترك.";
                }
                else
                {
                    s.NextAttemptUtc = DateTime.UtcNow.AddSeconds(Math.Min(30, 3 * s.Failures));
                    s.LastError = ex.Message;
                }
                SetState(s, "error");
                EndTurn(s, false, work, noTurn);
            }
            finally
            {
                _inOwnOp = false;
                Changed?.Invoke();
            }
        }

        // The turn is mine until this succeeds, so a short network hiccup is
        // retried right here (nobody else can sync in between).
        int UploadCentral(LiveSession s)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { return HostedCentral.Upload(_hub, s.ProjectId, s.CentralPath, s.CentralRev).GetAwaiter().GetResult(); }
                catch (Exception ex) when (attempt < 3)
                {
                    Log.Error($"central upload attempt {attempt}", ex);
                    Thread.Sleep(2000);
                }
            }
        }

        void EndTurn(LiveSession s, bool ok, Work work, bool noTurn, int added = 0, int modified = 0, int deleted = 0, long ms = 0)
        {
            if (work == Work.Check) return;
            var hub = _hub;
            if (hub == null) return;
            if (noTurn && !ok) return;
            var kind = work == Work.Sync ? "sync" : "reload";
            var centralRev = s.Hosted ? s.CentralRev : 0;
            Task.Run(() => hub.Post($"/api/projects/{s.ProjectId}/revit/sync/end",
                new { ok, kind, duration_ms = ms, added, modified, deleted, central_rev = centralRev }));
            if (ok && work == Work.Sync)
                AddActivity(App.Settings.DisplayName, null, $"زامنت تعديلاتك ({added + modified + deleted} تغيير، {ms / 1000.0:0.0} ثانية)");
        }

        // ------------------------------------------------------------ hub messages (background thread)

        void OnHubConnection(bool on)
        {
            var s = _s;
            if (s != null && on) s.PendingReload = true; // may have missed syncs while offline
            PushStatus(force: true);
            Changed?.Invoke();
        }

        void OnHubMessage(JsonNode m)
        {
            var type = m?["type"]?.GetValue<string>();
            var s = _s;
            if (s == null) return;
            switch (type)
            {
                case "hello":
                case "presence":
                    UpdateOnline(m["online"] as JsonArray);
                    break;
                case "revit_sync":
                {
                    var uid = m["user"]?["id"]?.GetValue<long>() ?? 0;
                    if (uid == App.Settings.UserId) break;
                    var p = m["payload"];
                    if (p?["kind"]?.GetValue<string>() != "sync") break;
                    s.PendingReload = true;
                    var n = (p["added"]?.GetValue<int>() ?? 0) + (p["modified"]?.GetValue<int>() ?? 0) + (p["deleted"]?.GetValue<int>() ?? 0);
                    AddActivity(m["user"]?["display_name"]?.GetValue<string>(), m["user"]?["color"]?.GetValue<string>(),
                                $"زامن تعديلات ({n} تغيير)، راح تظهر عندك هسه");
                    Tick();
                    break;
                }
                case "chat":
                    AddActivity(m["user"]?["display_name"]?.GetValue<string>(), m["user"]?["color"]?.GetValue<string>(),
                                m["payload"]?["text"]?.GetValue<string>(), chat: true);
                    break;
            }
            Changed?.Invoke();
        }

        void UpdateOnline(JsonArray arr)
        {
            var list = new List<TeamMember>();
            if (arr != null)
            {
                foreach (var u in arr)
                {
                    var apps = u?["apps"] as JsonArray;
                    var app = apps?.FirstOrDefault(a => (a?["su"]?.GetValue<string>() ?? "").StartsWith("Revit"));
                    list.Add(new TeamMember
                    {
                        Id = u?["id"]?.GetValue<long>() ?? 0,
                        Name = u?["display_name"]?.GetValue<string>(),
                        Color = u?["color"]?.GetValue<string>(),
                        App = app?["su"]?.GetValue<string>() ?? (apps != null && apps.Count > 0 ? apps[0]?["su"]?.GetValue<string>() : "متصفح"),
                        RevitUser = app?["revit_user"]?.GetValue<string>(),
                        Status = u?["status"]?["text"]?.GetValue<string>(),
                    });
                }
            }
            Online = list;
        }

        /// <summary>Two members with the same Revit user name break element ownership.</summary>
        public string DuplicateRevitUserWarning()
        {
            var dup = Online.Where(m => !string.IsNullOrEmpty(m.RevitUser))
                            .GroupBy(m => m.RevitUser, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault(g => g.Select(x => x.Id).Distinct().Count() > 1);
            return dup == null ? null
                : $"«{string.Join("» و«", dup.Select(x => x.Name).Distinct())}» داخلين بنفس اسم مستخدم Revit ({dup.Key}). " +
                  "لازم كل واحد يغير اسمه من Revit ← Options ← General ← Username، وإلا الملكية تتلخبط.";
        }

        public void AddActivity(string who, string color, string text, bool chat = false)
        {
            lock (Activity)
            {
                Activity.Add(new ActivityItem { At = DateTime.Now, Who = who, Color = color, Text = text, Chat = chat });
                while (Activity.Count > 120) Activity.RemoveAt(0);
            }
        }

        public void SendChat(string text)
        {
            var s = _s;
            var hub = _hub;
            if (s == null || hub == null || string.IsNullOrWhiteSpace(text)) return;
            Task.Run(() => hub.Post($"/api/projects/{s.ProjectId}/chat", new { text }));
        }

        // ------------------------------------------------------------ status

        void SetState(LiveSession s, string state)
        {
            s.State = state;
            PushStatus();
            Changed?.Invoke();
        }

        void PushStatus(bool force = false)
        {
            var s = _s;
            var hub = _hub;
            if (s == null || hub == null) return;
            string text;
            switch (s.State)
            {
                case "editing": text = "يعدّل…"; break;
                case "waiting": text = "ينتظر دوره بالمزامنة"; break;
                case "syncing": text = "يزامن…"; break;
                case "reloading": text = "يسحب تعديلات الفريق…"; break;
                case "error": text = "مشكلة بالمزامنة"; break;
                default: text = s.Dirty ? "يعدّل…" : "متزامن ✓"; break;
            }
            if (!force && text == _lastStatus) return;
            _lastStatus = text;
            hub.SendStatus(text, RevitVersion, RevitUser);
        }
    }

    class NoWaitForLock : ICentralLockedCallback
    {
        // Turns are coordinated by the hub; never block Revit waiting on a lock.
        public bool ShouldWaitForLockAvailability() => false;
    }
}
