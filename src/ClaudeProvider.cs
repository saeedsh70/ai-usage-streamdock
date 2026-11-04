// Claude (Pro / Max) limits, read with the login Claude Code keeps in
// %USERPROFILE%\.claude\.credentials.json. Read-only: the token is never renewed
// and the file is never written; when it has expired, the key asks you to open Claude Code.
//
// GET https://api.anthropic.com/api/oauth/usage is what Claude Code's /usage shows.
// It is not a documented public API, so both the current "limits" array and the
// older flat five_hour / seven_day objects are accepted.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AiUsageDeck
{
    internal sealed class ClaudeProvider : IProvider
    {
        // Anthropic's edge answers 403 to unknown user agents on the OAuth endpoints.
        private const string UserAgent = "claude-code/2.1.71";

        public string Id { get { return "claude"; } }
        public string Name { get { return "Claude"; } }
        public string Tag { get { return "CLAUDE"; } }
        public string ExtraLabel { get { return "EXTRA"; } }

        private static string ApiBase
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("AI_USAGE_DECK_ANTHROPIC_API");
                return string.IsNullOrEmpty(v) ? "https://api.anthropic.com" : v.TrimEnd('/');
            }
        }

        public string Where(Config config)
        {
            return CredentialsPath(config);
        }

        public static string CredentialsPath(Config config)
        {
            string dir;
            if (!string.IsNullOrEmpty(config.ClaudeDir)) dir = Environment.ExpandEnvironmentVariables(config.ClaudeDir);
            else
            {
                dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            }
            return Path.Combine(dir, ".credentials.json");
        }

        private static string ReadToken(string path, out double expiresAt, out string plan)
        {
            expiresAt = 0;
            plan = null;
            Dictionary<string, object> creds;
            try
            {
                creds = Json.ParseObject(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (FileNotFoundException) { throw NotSignedIn(path); }
            catch (DirectoryNotFoundException) { throw NotSignedIn(path); }
            catch (Exception ex)
            {
                throw new UsageException("Read error", "Could not read " + path + ": " + ex.Message);
            }
            var login = Json.Dict(creds, "claudeAiOauth");
            string token = Json.Str(login, "accessToken");
            if (string.IsNullOrEmpty(token)) throw NotSignedIn(path);
            expiresAt = Json.Num(login, "expiresAt") ?? 0;
            string tier = Json.Str(login, "rateLimitTier");
            if (string.IsNullOrEmpty(tier)) tier = Json.Str(login, "subscriptionType");
            if (!string.IsNullOrEmpty(tier))
            {
                if (tier.StartsWith("default_claude_", StringComparison.Ordinal)) tier = tier.Substring("default_claude_".Length);
                plan = tier.Replace('_', ' ');
            }
            return token;
        }

        private static UsageException NotSignedIn(string path)
        {
            return new UsageException("Sign in", "Claude Code is not signed in on this PC (" + path +
                " not found). Install Claude Code, run `claude` and use /login.");
        }

        private static UsageException Expired()
        {
            return new UsageException("Login expired",
                "Claude Code's saved login has expired. Open Claude Code (run `claude`) once and it renews it; " +
                "this plugin only reads the login and never renews it itself.");
        }

        public UsageSnapshot Fetch(Config config)
        {
            string path = CredentialsPath(config);
            double expiresAt;
            string plan;
            string token = ReadToken(path, out expiresAt, out plan);
            if (expiresAt > 0 && Providers.NowMs() > expiresAt - 30000) throw Expired();

            HttpResult r = Get(config, token);
            if (r.Status == 401)
            {
                // Claude Code may have renewed the token since we read it.
                string latest = ReadToken(path, out expiresAt, out plan);
                if (latest == token) throw Expired();
                r = Get(config, latest);
                if (r.Status == 401) throw Expired();
            }
            if (r.Status == 403) throw new UsageException("No plan", "Usage unavailable (403): no active Claude Pro/Max subscription on this login, or OAuth is disabled for the organization.");
            if (r.Status == 429) throw new UsageException("Rate limited", "The usage endpoint rate-limited this client (429). It will retry later.");
            if (r.Status != 200) throw new UsageException("HTTP " + r.Status, "Claude usage request failed: HTTP " + r.Status + " " + Providers.Trim(r.Body));

            UsageSnapshot snap = Parse(Json.ParseObject(r.Body));
            snap.Plan = plan;
            return snap;
        }

        private static HttpResult Get(Config config, string token)
        {
            var headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer " + token;
            headers["anthropic-beta"] = "oauth-2025-04-20";
            headers["User-Agent"] = UserAgent;
            headers["Accept"] = "application/json";
            return Http.Send(config, "GET", ApiBase + "/api/oauth/usage", headers, null);
        }

        public static UsageSnapshot Parse(Dictionary<string, object> body)
        {
            if (body == null) throw new UsageException("Bad reply", "The Claude usage reply was not a JSON object.");
            var snap = new UsageSnapshot();

            object[] limits = Json.Array(body, "limits");
            if (limits != null)
            {
                foreach (object o in limits)
                {
                    var e = o as Dictionary<string, object>;
                    double? pct = Json.Num(e, "percent");
                    if (!pct.HasValue) pct = Json.Num(e, "utilization");
                    if (!pct.HasValue) continue;
                    string kind = Json.Str(e, "kind") ?? "";
                    var l = new Limit();
                    l.Percent = Providers.ClampPct(pct.Value);
                    l.ResetsAt = Json.Time(e, "resets_at");
                    if (kind == "session") { l.Kind = "session"; l.Name = "Session"; }
                    else if (kind == "weekly_all") { l.Kind = "weekly"; l.Name = "Weekly"; }
                    else if (kind == "weekly_scoped")
                    {
                        l.Kind = "model";
                        var model = Json.Dict(Json.Dict(e, "scope"), "model");
                        l.Name = Json.Str(model, "display_name");
                        if (string.IsNullOrEmpty(l.Name)) l.Name = Json.Str(model, "id");
                        if (string.IsNullOrEmpty(l.Name)) l.Name = "Model";
                    }
                    else { l.Kind = "other"; l.Name = Providers.Pretty(kind); }
                    snap.Limits.Add(l);
                }
            }

            if (snap.Limits.Count == 0)
            {
                AddFlat(snap, body, "five_hour", "session", "Session");
                AddFlat(snap, body, "seven_day", "weekly", "Weekly");
                foreach (KeyValuePair<string, object> kv in body)
                {
                    if (!kv.Key.StartsWith("seven_day_", StringComparison.Ordinal)) continue;
                    string rest = kv.Key.Substring("seven_day_".Length);
                    if (rest == "oauth_apps" || rest == "cowork") continue;
                    AddFlat(snap, body, kv.Key, "model", Providers.Pretty(rest));
                }
            }

            var extra = Json.Dict(body, "extra_usage");
            if (extra != null)
            {
                var x = new ExtraUsage();
                x.Enabled = Json.Bool(extra, "is_enabled", false);
                double? used = Json.Num(extra, "used_credits");     // cents
                double? limit = Json.Num(extra, "monthly_limit");   // cents
                if (used.HasValue) x.Value = KeyRenderer.Money(used.Value / 100.0);
                if (limit.HasValue) x.Sub = "of " + KeyRenderer.Money(limit.Value / 100.0);
                x.Percent = Json.Num(extra, "utilization");
                if (!x.Percent.HasValue && used.HasValue && limit.HasValue && limit.Value > 0)
                    x.Percent = used.Value * 100.0 / limit.Value;
                if (x.Percent.HasValue) x.Percent = Providers.ClampPct(x.Percent.Value);
                snap.Extra = x;
            }
            return snap;
        }

        private static void AddFlat(UsageSnapshot snap, Dictionary<string, object> body, string key, string kind, string name)
        {
            var o = Json.Dict(body, key);
            double? pct = Json.Num(o, "utilization");
            if (!pct.HasValue) return;
            var l = new Limit();
            l.Kind = kind;
            l.Name = name;
            l.Percent = Providers.ClampPct(pct.Value);
            l.ResetsAt = Json.Time(o, "resets_at");
            snap.Limits.Add(l);
        }
    }
}
