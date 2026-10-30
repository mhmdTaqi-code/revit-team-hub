using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TeamHub.Revit
{
    /// <summary>Per-user settings, stored in %AppData%\TeamHub\revit.json.</summary>
    public class Settings
    {
        public string Server { get; set; } = "";
        public string Token { get; set; } = "";
        public string Username { get; set; } = "";
        public long UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Role { get; set; } = "";
        public bool Live { get; set; } = true;
        /// <summary>Quiet time: a round waits until I've stopped editing this long.</summary>
        public double IdleSeconds { get; set; } = 3;
        /// <summary>One round (send mine + bring the team's) at most this often.</summary>
        public double SyncIntervalSeconds { get; set; } = 30;
        /// <summary>Local file path (lower-case) → hub project id.</summary>
        public Dictionary<string, long> LocalFiles { get; set; } = new Dictionary<string, long>();

        static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TeamHub", "revit.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch (Exception ex) { Log.Error("settings load", ex); }
            return new Settings();
        }

        /// <summary>Self-test sessions must not overwrite the real user's settings.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public bool NoSave { get; set; }

        public void Save()
        {
            if (NoSave) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Error("settings save", ex); }
        }

        public bool LoggedIn => !string.IsNullOrEmpty(Server) && !string.IsNullOrEmpty(Token);
    }

    public static class Log
    {
        static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TeamHub", "revit.log");
        static readonly object Gate = new object();

        public static void Info(string msg) => Write("INFO", msg);
        public static void Error(string where, Exception ex) => Write("ERROR", $"{where}: {ex.GetType().Name}: {ex.Message}");

        static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
                }
            }
            catch { /* logging must never throw */ }
        }
    }

    public class ApiResult
    {
        public int Status;
        public JsonNode Data;
        public bool Ok => Status >= 200 && Status < 300;
        public string Error => Data?["error"]?.GetValue<string>() ?? (Status == 0 ? "network" : $"HTTP {Status}");
    }

    /// <summary>HTTP + WebSocket client for the Team Hub server. Never called on Revit's API thread for long.</summary>
    public class HubClient : IDisposable
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public string Server { get; }
        public string Token { get; set; }

        ClientWebSocket _ws;
        CancellationTokenSource _wsCts;
        long _wsProject;
        string _lastStatusJson;
        readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        /// <summary>Raised on a background thread for every message pushed by the server.</summary>
        public event Action<JsonNode> Message;
        public event Action<bool> ConnectionChanged;
        public bool Connected { get; private set; }

        public HubClient(string server, string token)
        {
            Server = Normalize(server);
            Token = token;
        }

        public static string Normalize(string server)
        {
            var s = (server ?? "").Trim().TrimEnd('/');
            if (s.Length > 0 && !s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                             && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                s = "http://" + s;
            return s;
        }

        public async Task<ApiResult> Request(HttpMethod method, string path, object body = null)
        {
            try
            {
                using var req = new HttpRequestMessage(method, Server + path);
                if (!string.IsNullOrEmpty(Token)) req.Headers.Add("Authorization", "Bearer " + Token);
                if (body != null)
                    req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var res = await Http.SendAsync(req).ConfigureAwait(false);
                var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                JsonNode data = null;
                try { if (!string.IsNullOrWhiteSpace(text)) data = JsonNode.Parse(text); } catch { }
                return new ApiResult { Status = (int)res.StatusCode, Data = data };
            }
            catch (Exception ex)
            {
                Log.Error($"{method} {path}", ex);
                return new ApiResult { Status = 0 };
            }
        }

        public Task<ApiResult> Get(string path) => Request(HttpMethod.Get, path);
        public Task<ApiResult> Post(string path, object body) => Request(HttpMethod.Post, path, body);

        // ------------------------------------------------------------ WebSocket

        public void StartLive(long projectId)
        {
            StopLive();
            _wsProject = projectId;
            _wsCts = new CancellationTokenSource();
            var ct = _wsCts.Token;
            Task.Run(() => LiveLoop(ct));
        }

        public void StopLive()
        {
            try { _wsCts?.Cancel(); } catch { }
            try { _ws?.Abort(); } catch { }
            _ws = null;
            SetConnected(false);
        }

        async Task LiveLoop(CancellationToken ct)
        {
            var delay = 1000;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var wsUrl = Server.Replace("http://", "ws://").Replace("https://", "wss://")
                                + $"/ws?token={Uri.EscapeDataString(Token)}&project={_wsProject}&client=revit";
                    using var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    await ws.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
                    _ws = ws;
                    SetConnected(true);
                    delay = 1000;
                    if (_lastStatusJson != null) await SendRaw(_lastStatusJson).ConfigureAwait(false);

                    var buffer = new byte[64 * 1024];
                    var sb = new StringBuilder();
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                        if (!r.EndOfMessage) continue;
                        var text = sb.ToString();
                        sb.Clear();
                        try { Message?.Invoke(JsonNode.Parse(text)); }
                        catch (Exception ex) { Log.Error("ws message", ex); }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Error("ws", ex); }
                _ws = null;
                SetConnected(false);
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch { break; }
                delay = Math.Min(delay * 2, 15000);
            }
        }

        void SetConnected(bool on)
        {
            if (Connected == on) return;
            Connected = on;
            try { ConnectionChanged?.Invoke(on); } catch { }
        }

        /// <summary>What I'm doing + which Revit I run, shown to the team.</summary>
        public void SendStatus(string text, string revitVersion, string revitUser)
        {
            var msg = new JsonObject
            {
                ["type"] = "status",
                ["status"] = new JsonObject { ["text"] = text },
                ["app"] = new JsonObject
                {
                    ["su"] = "Revit " + revitVersion,
                    ["plugin"] = App.Version,
                    ["revit_user"] = revitUser,
                },
            };
            _lastStatusJson = msg.ToJsonString();
            _ = SendRaw(_lastStatusJson);
        }

        async Task SendRaw(string json)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) return;
            // ClientWebSocket allows only one send at a time.
            await _sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Error("ws send", ex); }
            finally { _sendGate.Release(); }
        }

        public void Dispose() => StopLive();
    }
}
