// AI Usage for StreamDock (Redragon SS-550 and other MiraBox / HotSpot StreamDock builds).
//
// The host launches this executable with
//     -port <n> -pluginUUID <uuid> -registerEvent registerPlugin -info <json>
// and expects a WebSocket client on ws://127.0.0.1:<port> that registers with
//     {"event": "<registerEvent>", "uuid": "<pluginUUID>"}
// After that it speaks the Elgato-compatible event protocol.
//
// Every handler runs on the main thread (the pump); network calls run on pool
// threads, one per provider at a time, and hand their result back through the queue.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace AiUsageDeck
{
    internal static class Plugin
    {
        public const string MeterAction = "com.saeedsh.aiusage.meter";

        private sealed class ProviderState
        {
            public IProvider Provider;
            public UsageSnapshot Snapshot;
            public UsageException Error;
            public bool Fetching;
            public bool Refetch;
            public DateTime LastAttempt = DateTime.MinValue;
            public TimeSpan Backoff = TimeSpan.Zero;
        }

        private static readonly BlockingCollection<object> Inbox = new BlockingCollection<object>();
        private static readonly object SendGate = new object();
        private static readonly Dictionary<string, Dictionary<string, object>> Keys =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> KeyDownAt =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ProviderState> States = new Dictionary<string, ProviderState>();

        private static ClientWebSocket _socket;
        private static Config _config;
        private static string _piContext;
        private static string _piAction = MeterAction;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--close-host" || args[0] == "--start-host"))
                return HostControl.Run(args);

            try
            {
                string port = Arg(args, "-port");
                string uuid = Arg(args, "-pluginUUID");
                string register = Arg(args, "-registerEvent");
                if (port == null || uuid == null || register == null)
                {
                    Log.Write("missing launch arguments: " + string.Join(" ", args));
                    return 1;
                }
                Log.Write("starting (port " + port + ")");
                _config = Config.Load();
                foreach (IProvider p in Providers.All)
                {
                    var st = new ProviderState();
                    st.Provider = p;
                    States[p.Id] = st;
                }

                _socket = new ClientWebSocket();
                _socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port), CancellationToken.None).Wait();
                Send("{\"event\":\"" + Json.Escape(register) + "\",\"uuid\":\"" + Json.Escape(uuid) + "\"}");

                var receiver = new Thread(ReceiveLoop);
                receiver.IsBackground = true;
                receiver.Start();

                var ticker = new Thread(delegate()
                {
                    while (!Inbox.IsAddingCompleted)
                    {
                        Thread.Sleep(20000);
                        try { Inbox.Add((Action)Tick); } catch (InvalidOperationException) { return; }
                    }
                });
                ticker.IsBackground = true;
                ticker.Start();

                Pump();
                Log.Write("host closed the connection, exiting");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("FATAL " + ex);
                return 2;
            }
        }

        #region transport

        private static void ReceiveLoop()
        {
            var buffer = new byte[64 * 1024];
            var message = new List<byte>();
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult r = _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).Result;
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        try { _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(2000); }
                        catch (Exception) { }
                        break;
                    }
                    for (int i = 0; i < r.Count; i++) message.Add(buffer[i]);
                    if (!r.EndOfMessage) continue;
                    Inbox.Add(Encoding.UTF8.GetString(message.ToArray()));
                    message.Clear();
                }
            }
            catch (Exception ex)
            {
                Log.Write("receive loop ended: " + ex.GetBaseException().Message);
            }
            Inbox.CompleteAdding();
        }

        private static void Send(string json)
        {
            lock (SendGate)
            {
                if (_socket == null || _socket.State != WebSocketState.Open) return;
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                try
                {
                    _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
                }
                catch (Exception ex)
                {
                    Log.Write("send failed: " + ex.GetBaseException().Message);
                }
            }
        }

        private static void Pump()
        {
            foreach (object item in Inbox.GetConsumingEnumerable())
            {
                try
                {
                    var work = item as Action;
                    if (work != null) work();
                    else Dispatch((string)item);
                }
                catch (Exception ex)
                {
                    Log.Write("handler error: " + ex);
                }
            }
        }

        private static void SetImage(string context, string dataUrl)
        {
            Send("{\"event\":\"setImage\",\"context\":\"" + Json.Escape(context) +
                 "\",\"payload\":{\"image\":\"" + dataUrl + "\",\"target\":0}}");
        }

        private static void SetTitle(string context, string title)
        {
            Send("{\"event\":\"setTitle\",\"context\":\"" + Json.Escape(context) +
                 "\",\"payload\":{\"title\":\"" + Json.Escape(title) + "\",\"target\":0}}");
        }

        private static void OpenUrl(string url)
        {
            Send("{\"event\":\"openUrl\",\"payload\":{\"url\":\"" + Json.Escape(url) + "\"}}");
        }

        private static void SendToPi(object payload)
        {
            if (_piContext == null) return;
            Send("{\"event\":\"sendToPropertyInspector\",\"action\":\"" + Json.Escape(_piAction) +
                 "\",\"context\":\"" + Json.Escape(_piContext) + "\",\"payload\":" + Json.Serialize(payload) + "}");
        }

        #endregion

        #region events

        private static void Dispatch(string raw)
        {
            var msg = Json.ParseObject(raw);
            if (msg == null) return;
            string ev = Json.Str(msg, "event");
            string context = Json.Str(msg, "context");
            string action = Json.Str(msg, "action");
            var payload = Json.Dict(msg, "payload");

            switch (ev)
            {
                case "willAppear":
                    Keys[context] = Json.Dict(payload, "settings") ?? new Dictionary<string, object>();
                    SetTitle(context, "");
                    Render(context);
                    EnsureFresh(ProviderOf(context));
                    break;
                case "willDisappear":
                    Keys.Remove(context);
                    KeyDownAt.Remove(context);
                    break;
                case "didReceiveSettings":
                    if (context != null) ApplySettings(context, Json.Dict(payload, "settings"));
                    break;
                case "keyDown":
                    if (context != null) KeyDownAt[context] = DateTime.UtcNow;
                    break;
                case "keyUp":
                    OnKeyUp(context);
                    break;
                case "propertyInspectorDidAppear":
                    _piContext = context;
                    if (!string.IsNullOrEmpty(action)) _piAction = action;
                    SendState();
                    break;
                case "propertyInspectorDidDisappear":
                    if (_piContext == context) _piContext = null;
                    break;
                case "sendToPlugin":
                    string target = Json.Str(payload, "ctx");
                    if (!string.IsNullOrEmpty(target)) context = target;
                    if (context != null) _piContext = context;
                    if (!string.IsNullOrEmpty(action)) _piAction = action;
                    FromPi(context, payload);
                    break;
            }
        }

        private static void ApplySettings(string context, Dictionary<string, object> settings)
        {
            if (settings == null || !Keys.ContainsKey(context)) return;
            string before = ProviderOf(context);
            Keys[context] = settings;
            Render(context);
            if (ProviderOf(context) != before) EnsureFresh(ProviderOf(context));
            SendState();
        }

        private static void OnKeyUp(string context)
        {
            DateTime down;
            bool longPress = context != null && KeyDownAt.TryGetValue(context, out down) &&
                             (DateTime.UtcNow - down).TotalMilliseconds >= 600;
            if (context != null) KeyDownAt.Remove(context);
            string provider = ProviderOf(context);
            if (longPress) { OpenUrl(UsagePage(provider)); return; }
            StartFetch(provider, true, false);
        }

        private static string UsagePage(string provider)
        {
            if (provider == "chatgpt") return "https://chatgpt.com/codex";
            if (provider == "antigravity") return "https://antigravity.google/";
            return "https://claude.ai/settings/usage";
        }

        private static void FromPi(string context, Dictionary<string, object> payload)
        {
            string command = Json.Str(payload, "command");
            if (command == "settings")
            {
                if (context != null) ApplySettings(context, Json.Dict(payload, "settings"));
            }
            else if (command == "getState") SendState();
            else if (command == "refresh") StartFetch(ProviderOf(context), true, true);
            else if (command == "openUsage") OpenUrl(UsagePage(ProviderOf(context)));
            else if (command == "openLog")
            {
                try { System.Diagnostics.Process.Start(Log.Folder); } catch (Exception) { }
            }
            else if (command == "saveConfig")
            {
                var c = Json.Dict(payload, "config");
                if (c == null) return;
                _config.ClaudeDir = (Json.Str(c, "claudeDir") ?? "").Trim();
                _config.CodexDir = (Json.Str(c, "codexDir") ?? "").Trim();
                _config.RefreshMinutes = Config.Clamp(Json.Int(c, "refreshMinutes", 5), 1, 120);
                _config.Proxy = (Json.Str(c, "proxy") ?? "").Trim();
                try
                {
                    _config.Save();
                    Log.Write("settings saved");
                }
                catch (Exception ex)
                {
                    Log.Write("settings save failed: " + ex.Message);
                }
                foreach (ProviderState st in States.Values)
                {
                    st.Backoff = TimeSpan.Zero;
                    if (InUse(st.Provider.Id)) StartFetch(st.Provider.Id, true, true);
                }
            }
        }

        #endregion

        #region fetching

        private static string ProviderOf(string context)
        {
            Dictionary<string, object> settings;
            if (context == null || !Keys.TryGetValue(context, out settings)) return "claude";
            string p = Json.Str(settings, "provider");
            return Providers.Get(p).Id;
        }

        private static bool InUse(string provider)
        {
            foreach (string context in Keys.Keys)
                if (ProviderOf(context) == provider) return true;
            return false;
        }

        private static void EnsureFresh(string provider)
        {
            ProviderState st = States[provider];
            if (st.Snapshot == null && st.Error == null) StartFetch(provider, false, false);
        }

        private static void Tick()
        {
            foreach (ProviderState st in States.Values)
            {
                if (!InUse(st.Provider.Id) || st.Fetching) continue;
                TimeSpan interval = TimeSpan.FromMinutes(_config.RefreshMinutes) + st.Backoff;
                // A provider that is simply not running/signed in is checked again sooner.
                if (st.Snapshot == null && st.Error != null && st.Backoff == TimeSpan.Zero)
                    interval = TimeSpan.FromMinutes(Math.Min(1, _config.RefreshMinutes));
                if (DateTime.UtcNow - st.LastAttempt >= interval) StartFetch(st.Provider.Id, false, false);
            }
            RenderAll(); // countdowns move
        }

        /// <summary>manual: a key press (debounced). force: settings changed, always fetch again.</summary>
        private static void StartFetch(string provider, bool manual, bool force)
        {
            ProviderState st = States[provider];
            if (st.Fetching) { if (force) st.Refetch = true; return; }
            if (manual && !force && DateTime.UtcNow - st.LastAttempt < TimeSpan.FromSeconds(8)) return;
            st.Fetching = true;
            st.LastAttempt = DateTime.UtcNow;
            Config config = _config;

            ThreadPool.QueueUserWorkItem(delegate
            {
                UsageSnapshot result = null;
                UsageException error = null;
                try { result = st.Provider.Fetch(config); }
                catch (UsageException ex) { error = ex; }
                catch (Exception ex) { error = new UsageException("Error", ex.GetBaseException().Message); }

                try { Inbox.Add((Action)delegate { Finish(st, result, error); }); }
                catch (InvalidOperationException) { }
            });
        }

        private static void Finish(ProviderState st, UsageSnapshot result, UsageException error)
        {
            st.Fetching = false;
            if (st.Refetch)
            {
                st.Refetch = false;
                StartFetch(st.Provider.Id, true, true);
                return;
            }
            if (result != null)
            {
                st.Snapshot = result;
                st.Error = null;
                st.Backoff = TimeSpan.Zero;
                Log.Write(st.Provider.Name + " updated: " + Describe(result));
            }
            else
            {
                bool changed = st.Error == null || st.Error.Message != error.Message;
                st.Error = error;
                if (error.Short == "Rate limited")
                    st.Backoff = TimeSpan.FromMinutes(Math.Min(30, Math.Max(5, st.Backoff.TotalMinutes * 2)));
                if (changed) Log.Write(st.Provider.Name + " update failed: " + error.Short + " - " + error.Message);
            }
            RenderAll();
            SendState();
        }

        private static string Describe(UsageSnapshot s)
        {
            var parts = new List<string>();
            foreach (Limit l in s.Limits)
                parts.Add((l.Scope != null ? l.Scope + " " : "") + l.Name + " " + KeyRenderer.Pct(l.Percent));
            return string.Join(", ", parts.ToArray());
        }

        #endregion

        #region rendering

        private static void RenderAll()
        {
            foreach (string context in new List<string>(Keys.Keys)) Render(context);
        }

        private static string S(Dictionary<string, object> settings, string key, string fallback)
        {
            string v = Json.Str(settings, key);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static void Render(string context)
        {
            Dictionary<string, object> settings;
            if (context == null || !Keys.TryGetValue(context, out settings)) return;
            try
            {
                SetImage(context, Draw(settings));
            }
            catch (Exception ex)
            {
                Log.Write("render failed: " + ex.Message);
            }
        }

        private static string Draw(Dictionary<string, object> settings)
        {
            ProviderState st = States[Providers.Get(Json.Str(settings, "provider")).Id];
            IProvider provider = st.Provider;
            string metric = S(settings, "metric", "session");
            // "model" (older settings) and "filter" both narrow the limit by name.
            string filter = (Json.Str(settings, "filter") ?? Json.Str(settings, "model") ?? "").Trim();
            string style = S(settings, "style", "fill");
            bool showReset = Json.Bool(settings, "showReset", true);
            int warnAt = Config.Clamp(Json.Int(settings, "warnAt", 60), 1, 100);
            int dangerAt = Config.Clamp(Json.Int(settings, "dangerAt", 85), 1, 100);
            string label = (Json.Str(settings, "label") ?? "").Trim();
            // Only Antigravity has quota groups; elsewhere the filter only picks a model's limit.
            if (metric != "model" && provider.Id != "antigravity") filter = "";

            if (st.Snapshot == null)
            {
                if (label.Length == 0) label = DefaultLabel(provider, metric, filter, null);
                if (st.Error == null) return KeyRenderer.Message(label, "...", KeyRenderer.Accent);
                return KeyRenderer.Message(label, KeyWord(st.Error.Short), KeyRenderer.Accent);
            }

            bool stale = st.Error != null;
            if (metric == "overview")
            {
                if (label.Length == 0) label = provider.Tag;
                return KeyRenderer.Overview(label, st.Snapshot.Find("session", filter), st.Snapshot.Find("weekly", filter),
                                            stale, warnAt, dangerAt);
            }
            if (metric == "extra")
            {
                if (label.Length == 0) label = provider.ExtraLabel;
                return KeyRenderer.Extra(label, st.Snapshot.Extra, stale, warnAt, dangerAt);
            }

            Limit limit = st.Snapshot.Find(metric, filter);
            if (label.Length == 0) label = DefaultLabel(provider, metric, filter, limit);
            if (limit == null)
                return KeyRenderer.Message(label, "N/A", System.Drawing.Color.FromArgb(150, 150, 150));
            return KeyRenderer.Meter(label, limit.Percent, limit.ResetsAt, style, showReset, stale, warnAt, dangerAt);
        }

        /// <summary>A model name without its "(High)" / "(Thinking)" style suffix, for a key label.</summary>
        private static string ShortName(string name)
        {
            int paren = name.IndexOf('(');
            if (paren > 2) name = name.Substring(0, paren);
            return name.Trim().ToUpperInvariant();
        }

        /// <summary>Short, large-print labels: the keys are only ~14 mm wide.</summary>
        private static string DefaultLabel(IProvider provider, string metric, string filter, Limit limit)
        {
            switch (metric)
            {
                case "weekly":
                    return (filter.Length > 0 ? filter.ToUpperInvariant() : provider.Tag) + " WK";
                case "model":
                    if (limit != null && !string.IsNullOrEmpty(limit.Scope)) return ShortName(limit.Scope);
                    if (limit != null && !string.IsNullOrEmpty(limit.Name)) return ShortName(limit.Name);
                    return filter.Length == 0 ? "MODEL" : filter.ToUpperInvariant();
                case "extra": return provider.ExtraLabel;
                case "overview": return provider.Tag;
                default:
                    return (filter.Length > 0 ? filter.ToUpperInvariant() : provider.Tag) + " 5H";
            }
        }

        /// <summary>The error as one or two words big enough to read on the key; details go to the inspector.</summary>
        private static string KeyWord(string shortError)
        {
            switch (shortError)
            {
                case "Sign in": return "SIGN\nIN";
                case "Login expired": return "LOGIN\nEXPIRED";
                case "Not running": return "NOT\nRUNNING";
                case "No answer": return "NO\nANSWER";
                case "Offline": return "OFFLINE";
                case "Blocked": return "BLOCKED";
                case "Rate limited": return "WAIT";
                case "No plan": return "NO\nPLAN";
                default: return "ERROR";
            }
        }

        #endregion

        #region property inspector state

        private static void SendState()
        {
            if (_piContext == null) return;
            var providers = new Dictionary<string, object>();
            foreach (ProviderState st in States.Values)
            {
                var status = new Dictionary<string, object>();
                status["name"] = st.Provider.Name;
                status["where"] = st.Provider.Where(_config);
                status["fetching"] = st.Fetching;
                if (st.Snapshot != null)
                {
                    status["plan"] = st.Snapshot.Plan ?? "";
                    status["fetchedAt"] = st.Snapshot.FetchedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    var limits = new List<object>();
                    foreach (Limit l in st.Snapshot.Limits)
                    {
                        var d = new Dictionary<string, object>();
                        d["kind"] = l.Kind;
                        d["name"] = l.Scope != null ? l.Scope + " \u00B7 " + l.Name : l.Name;
                        d["scope"] = l.Scope ?? "";
                        d["window"] = l.Name ?? "";
                        d["percent"] = Math.Round(l.Percent, 1);
                        d["reset"] = l.ResetsAt.HasValue ? KeyRenderer.Countdown(l.ResetsAt.Value) : "";
                        limits.Add(d);
                    }
                    status["limits"] = limits;
                    if (st.Snapshot.Extra != null)
                    {
                        var x = new Dictionary<string, object>();
                        x["label"] = st.Provider.ExtraLabel;
                        x["enabled"] = st.Snapshot.Extra.Enabled;
                        x["value"] = st.Snapshot.Extra.Value ?? "";
                        x["sub"] = st.Snapshot.Extra.Sub ?? "";
                        status["extra"] = x;
                    }
                }
                if (st.Error != null)
                {
                    status["error"] = st.Error.Short;
                    status["detail"] = st.Error.Message;
                }
                providers[st.Provider.Id] = status;
            }

            var payload = new Dictionary<string, object>();
            payload["event"] = "state";
            payload["config"] = _config.ToPublic();
            payload["providers"] = providers;
            payload["logFolder"] = Log.Folder;
            SendToPi(payload);
        }

        #endregion

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
