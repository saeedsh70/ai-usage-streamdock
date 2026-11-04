// Used by the installer and uninstaller, never by the plugin itself:
//   AIUsage.exe --close-host <state file>   close the stream deck software (and old plugin backends),
//                                            remembering which program to start again
//   AIUsage.exe --start-host <state file>   start it again
// The software only scans its plugins folder at startup, and it keeps the plugin
// exe locked while it runs, so installing means closing it and starting it again.
// Doing this here keeps the installer free of scripts and shell commands.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace AiUsageDeck
{
    internal static class HostControl
    {
        // The stream deck software only - never other Redragon tools such as mouse or keyboard drivers.
        private static readonly Regex HostPath =
            new Regex(@"stream ?dock|streamcraft|stream station|\\hotspot\\|ss-?55\d", RegexOptions.IgnoreCase);

        private static readonly string[] Backends = { "AIUsage", "ClaudeUsage" };

        public static int Run(string[] args)
        {
            string state = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "aiusage-host.txt");
            try
            {
                if (args[0] == "--close-host") return Close(state);
                if (args[0] == "--start-host") return Start(state);
            }
            catch (Exception ex)
            {
                Log.Write("host control failed: " + ex.Message);
            }
            return 0; // never fail an install because of this
        }

        private static int Close(string state)
        {
            int self = Process.GetCurrentProcess().Id;
            var hosts = new Dictionary<int, string>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self) continue;
                    string path = p.MainModule.FileName;
                    if (Array.IndexOf(Backends, p.ProcessName) >= 0 || HostPath.IsMatch(path)) hosts[p.Id] = path;
                }
                catch (Exception) { } // other users' or elevated processes
            }

            // Remember top-level host programs only (not helpers they started, not plugin backends).
            var parents = ParentIds(hosts.Keys);
            var toStart = new List<string>();
            foreach (KeyValuePair<int, string> h in hosts)
            {
                int parent;
                bool child = parents.TryGetValue(h.Key, out parent) && hosts.ContainsKey(parent);
                string name = Path.GetFileNameWithoutExtension(h.Value);
                bool plugin = h.Value.IndexOf("\\plugins\\", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!child && !plugin && Array.IndexOf(Backends, name) < 0 && !toStart.Contains(h.Value)) toStart.Add(h.Value);
            }
            File.WriteAllLines(state, toStart.ToArray());

            foreach (int pid in hosts.Keys)
            {
                try
                {
                    Process p = Process.GetProcessById(pid);
                    if (!p.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill();
                    p.WaitForExit(5000);
                }
                catch (Exception) { }
            }
            Thread.Sleep(500);
            Log.Write("installer closed the stream deck software (" + toStart.Count + " program(s) to restart)");
            return 0;
        }

        private static int Start(string state)
        {
            if (!File.Exists(state)) return 0;
            foreach (string path in File.ReadAllLines(state))
            {
                if (path.Length == 0 || !File.Exists(path)) continue;
                var psi = new ProcessStartInfo(path);
                psi.WorkingDirectory = Path.GetDirectoryName(path);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            File.Delete(state);
            return 0;
        }

        private static Dictionary<int, int> ParentIds(IEnumerable<int> pids)
        {
            var result = new Dictionary<int, int>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId FROM Win32_Process"))
                using (System.Management.ManagementObjectCollection all = searcher.Get())
                {
                    var wanted = new HashSet<int>(pids);
                    foreach (System.Management.ManagementBaseObject o in all)
                    {
                        int pid = Convert.ToInt32(o["ProcessId"]);
                        if (wanted.Contains(pid)) result[pid] = Convert.ToInt32(o["ParentProcessId"]);
                    }
                }
            }
            catch (Exception) { }
            return result;
        }
    }
}
