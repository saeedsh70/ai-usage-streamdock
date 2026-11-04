// Google Antigravity quota, read from the Antigravity app's own language server on
// this PC while Antigravity is running. No login file is read and nothing leaves the
// machine: the language server already knows the quota and serves it on 127.0.0.1.
//
//   1. Find language_server_windows_*.exe started by Antigravity (WMI) and take the
//      --csrf_token from its command line.
//   2. Find the TCP ports that process listens on (IP Helper API) and ask each one.
//   3. POST /exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary
//      (5-hour and weekly buckets per quota group) and .../GetUserStatus (per-model
//      quota, plan and prompt credits) with header X-Codeium-Csrf-Token.
//
// These are internal endpoints of the Antigravity app and may change with its updates.

using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AiUsageDeck
{
    internal sealed class AntigravityProvider : IProvider
    {
        private const string Service = "/exa.language_server_pb.LanguageServerService/";
        private const string Body = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}";

        private int _pid;
        private int _port;
        private string _csrf;

        public string Id { get { return "antigravity"; } }
        public string Name { get { return "Antigravity"; } }
        public string Tag { get { return "AGY"; } }
        public string ExtraLabel { get { return "CREDITS"; } }

        public string Where(Config config)
        {
            return "the running Antigravity app (local language server)";
        }

        public UsageSnapshot Fetch(Config config)
        {
            Server server = Locate();
            if (server == null)
                throw new UsageException("Not running", "Antigravity is not running. Open the Antigravity app; its quota is read from it while it runs.");

            Dictionary<string, object> status = null, summary = null;
            if (_pid == server.Pid && _port > 0 && _csrf == server.Csrf)
            {
                status = Call(config, _port, server.Csrf, "GetUserStatus");
                if (status == null) _port = 0;
            }
            if (status == null)
            {
                foreach (int port in server.Ports)
                {
                    status = Call(config, port, server.Csrf, "GetUserStatus");
                    if (status != null) { _pid = server.Pid; _port = port; _csrf = server.Csrf; break; }
                }
            }
            if (status == null)
                throw new UsageException("No answer", "Antigravity is running but its language server did not answer on ports " +
                                         string.Join(", ", server.Ports.ConvertAll(p => p.ToString()).ToArray()) + ".");
            summary = Call(config, _port, server.Csrf, "RetrieveUserQuotaSummary");
            return Parse(status, summary);
        }

        private Dictionary<string, object> Call(Config config, int port, string csrf, string method)
        {
            var headers = new Dictionary<string, string>();
            headers["Content-Type"] = "application/json";
            headers["Connect-Protocol-Version"] = "1";
            headers["X-Codeium-Csrf-Token"] = csrf;
            try
            {
                HttpResult r = Http.Send(config, "POST", "https://127.0.0.1:" + port + Service + method, headers, Body, true, 6000);
                if (r.Status != 200) return null;
                return Json.ParseObject(r.Body);
            }
            catch (UsageException) { return null; }
            catch (ArgumentException) { return null; } // not JSON
            catch (InvalidOperationException) { return null; }
        }

        public static UsageSnapshot Parse(Dictionary<string, object> status, Dictionary<string, object> summary)
        {
            var snap = new UsageSnapshot();
            var user = Json.Dict(status, "userStatus");
            var plan = Json.Dict(Json.Dict(user, "planStatus"), "planInfo");
            snap.Plan = Json.Str(plan, "planName");

            // 5-hour / weekly buckets per quota group ("Gemini models", "Claude and GPT models" ...)
            var groups = Json.Array(Json.Dict(summary, "response"), "groups") ?? Json.Array(summary, "groups");
            if (groups != null)
            {
                foreach (object g in groups)
                {
                    var group = g as Dictionary<string, object>;
                    string scope = Json.Str(group, "displayName") ?? Json.Str(group, "display_name") ?? "Quota";
                    if (scope.EndsWith(" models", StringComparison.OrdinalIgnoreCase)) scope = scope.Substring(0, scope.Length - 7);
                    object[] buckets = Json.Array(group, "buckets");
                    if (buckets == null) continue;
                    foreach (object b in buckets)
                    {
                        var bucket = b as Dictionary<string, object>;
                        if (Json.Bool(bucket, "disabled", false)) continue;
                        double? remaining = Json.Num(bucket, "remainingFraction") ?? Json.Num(bucket, "remaining_fraction");
                        if (!remaining.HasValue) continue;
                        string window = (Json.Str(bucket, "window") ?? "").ToLowerInvariant();
                        var l = new Limit();
                        l.Scope = scope;
                        l.Percent = Providers.ClampPct(100 - remaining.Value * 100);
                        l.ResetsAt = Json.Time(bucket, "resetTime") ?? Json.Time(bucket, "reset_time");
                        if (window.StartsWith("5h") || window.Contains("five") || window.Contains("hour")) { l.Kind = "session"; l.Name = "5-hour"; }
                        else if (window.Contains("week")) { l.Kind = "weekly"; l.Name = "Weekly"; }
                        else { l.Kind = "other"; l.Name = Providers.Pretty(window.Length > 0 ? window : "quota"); }
                        snap.Limits.Add(l);
                    }
                }
            }

            // per-model quota
            object[] models = Json.Array(Json.Dict(user, "cascadeModelConfigData"), "clientModelConfigs");
            if (models != null)
            {
                foreach (object m in models)
                {
                    var model = m as Dictionary<string, object>;
                    var quota = Json.Dict(model, "quotaInfo") ?? Json.Dict(model, "quota_info");
                    if (quota == null) continue;
                    double? remaining = Json.Num(quota, "remainingFraction") ?? Json.Num(quota, "remaining_fraction");
                    var l = new Limit();
                    l.Kind = "model";
                    l.Name = Json.Str(model, "label") ?? "Model";
                    l.Percent = Providers.ClampPct(100 - (remaining ?? 1) * 100); // a missing fraction means untouched
                    l.ResetsAt = Json.Time(quota, "resetTime") ?? Json.Time(quota, "reset_time");
                    snap.Limits.Add(l);
                }
            }

            // prompt credits
            double? monthly = Json.Num(plan, "monthlyPromptCredits");
            double? available = Json.Num(Json.Dict(user, "planStatus"), "availablePromptCredits");
            if (available.HasValue)
            {
                var x = new ExtraUsage();
                x.Enabled = true;
                x.Value = Math.Floor(available.Value).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                if (monthly.HasValue && monthly.Value > 0)
                {
                    x.Sub = "of " + Math.Floor(monthly.Value).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                    x.Percent = Providers.ClampPct((monthly.Value - available.Value) * 100 / monthly.Value);
                }
                else x.Sub = "credits";
                snap.Extra = x;
            }

            if (snap.Limits.Count == 0 && snap.Extra == null)
                throw new UsageException("Bad reply", "Antigravity answered but reported no quota.");
            return snap;
        }

        #region discovery

        private sealed class Server
        {
            public int Pid;
            public string Csrf;
            public List<int> Ports = new List<int>();
        }

        private static Server Locate()
        {
            // Test hook: "port|csrf" of a language server, skipping discovery.
            string over = Environment.GetEnvironmentVariable("AI_USAGE_DECK_AGY");
            if (!string.IsNullOrEmpty(over))
            {
                string[] parts = over.Split('|');
                var s = new Server();
                s.Pid = -1;
                s.Csrf = parts.Length > 1 ? parts[1] : "";
                s.Ports.Add(int.Parse(parts[0]));
                return s;
            }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;

            foreach (KeyValuePair<int, string> proc in LanguageServers())
            {
                string cmd = proc.Value;
                bool antigravity = Regex.IsMatch(cmd, @"--app_data_dir[=\s]+""?antigravity\b", RegexOptions.IgnoreCase) ||
                                   cmd.IndexOf("\\antigravity\\", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!antigravity) continue;
                Match token = Regex.Match(cmd, @"--csrf_token[=\s]+""?([A-Za-z0-9\-]+)");
                if (!token.Success) continue;
                var s = new Server();
                s.Pid = proc.Key;
                s.Csrf = token.Groups[1].Value;
                Match ext = Regex.Match(cmd, @"--extension_server_port[=\s]+(\d+)");
                int extPort = ext.Success ? int.Parse(ext.Groups[1].Value) : 0;
                List<int> ports = ListeningPorts(proc.Key);
                // The API usually sits next to the extension server port; try that first.
                ports.Sort(delegate(int a, int b) { return Math.Abs(a - extPort - 1).CompareTo(Math.Abs(b - extPort - 1)); });
                ports.Remove(extPort);
                s.Ports = ports;
                if (s.Ports.Count == 0 && extPort > 0) s.Ports.Add(extPort + 1);
                return s;
            }
            return null;
        }

        private static List<KeyValuePair<int, string>> LanguageServers()
        {
            var list = new List<KeyValuePair<int, string>>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'language_server%'"))
                using (System.Management.ManagementObjectCollection results = searcher.Get())
                {
                    foreach (System.Management.ManagementBaseObject o in results)
                    {
                        string cmd = o["CommandLine"] as string;
                        if (string.IsNullOrEmpty(cmd)) continue;
                        list.Add(new KeyValuePair<int, string>(Convert.ToInt32(o["ProcessId"]), cmd));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Antigravity process lookup failed: " + ex.Message);
            }
            return list;
        }

        // IP Helper: the IPv4 TCP ports a process listens on, without running netstat or PowerShell.
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

        private const int AfInet = 2;
        private const int TcpTableOwnerPidListener = 3;

        private static List<int> ListeningPorts(int pid)
        {
            var ports = new List<int>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (size <= 0) return ports;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0) return ports;
                int count = Marshal.ReadInt32(buffer);
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid (6 x DWORD)
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(buffer, 4 + i * 24);
                    int owner = Marshal.ReadInt32(row, 20);
                    if (owner != pid) continue;
                    int raw = Marshal.ReadInt32(row, 8);
                    int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); // network byte order
                    if (!ports.Contains(port)) ports.Add(port);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return ports;
        }

        #endregion
    }
}
