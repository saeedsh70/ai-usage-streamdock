// Global (not per-key) settings, kept in %APPDATA%\AIUsageDeck\config.json.
// Nothing secret lives here: the plugin only reads logins that the providers'
// own tools already keep on this PC, and never stores or changes them.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AiUsageDeck
{
    internal sealed class Config
    {
        /// <summary>Optional Claude Code config folder; empty = CLAUDE_CONFIG_DIR or %USERPROFILE%\.claude.</summary>
        public string ClaudeDir = "";

        /// <summary>Optional Codex home folder; empty = CODEX_HOME or %USERPROFILE%\.codex.</summary>
        public string CodexDir = "";

        /// <summary>Minutes between automatic refreshes.</summary>
        public int RefreshMinutes = 5;

        /// <summary>Optional HTTP proxy such as http://127.0.0.1:8080; empty = Windows system proxy; "none" = direct.</summary>
        public string Proxy = "";

        public static string FilePath
        {
            get { return Path.Combine(Log.Folder, "config.json"); }
        }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(FilePath)) return c;
                var d = Json.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8));
                if (d == null) return c;
                c.ClaudeDir = (Json.Str(d, "claudeDir") ?? "").Trim();
                c.CodexDir = (Json.Str(d, "codexDir") ?? "").Trim();
                c.RefreshMinutes = Clamp(Json.Int(d, "refreshMinutes", 5), 1, 120);
                c.Proxy = (Json.Str(d, "proxy") ?? "").Trim();
            }
            catch (Exception ex)
            {
                Log.Write("config load failed: " + ex.Message);
            }
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(Log.Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(ToPublic()), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        public Dictionary<string, object> ToPublic()
        {
            var d = new Dictionary<string, object>();
            d["claudeDir"] = ClaudeDir;
            d["codexDir"] = CodexDir;
            d["refreshMinutes"] = RefreshMinutes;
            d["proxy"] = Proxy;
            return d;
        }

        public static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
