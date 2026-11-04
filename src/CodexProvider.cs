// ChatGPT plan limits for Codex (Plus / Pro / Business ...), read with the login the
// Codex CLI / app keeps in %USERPROFILE%\.codex\auth.json. Read-only: the token is
// never renewed and the file is never written.
//
// GET https://chatgpt.com/backend-api/codex/usage (older: /backend-api/wham/usage) is
// what Codex's own /status shows. It is not a documented public API.
//   { "plan_type": "plus",
//     "rate_limit": { "primary_window":   { "used_percent": 12, "limit_window_seconds": 18000,  "reset_at": 1779597324 },
//                     "secondary_window": { "used_percent": 40, "limit_window_seconds": 604800, "reset_at": 1780184124 } },
//     "additional_rate_limits": [ { "limit_name": "...", "rate_limit": { ...same windows... } } ],
//     "credits": { "has_credits": true, "unlimited": false, "balance": "480" } }

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AiUsageDeck
{
    internal sealed class CodexProvider : IProvider
    {
        // The usage endpoint sits behind a browser check, so requests look like a browser.
        private const string BrowserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0";

        public string Id { get { return "chatgpt"; } }
        public string Name { get { return "ChatGPT (Codex)"; } }
        public string Tag { get { return "GPT"; } }
        public string ExtraLabel { get { return "CREDITS"; } }

        private static string Base
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("AI_USAGE_DECK_CHATGPT");
                return string.IsNullOrEmpty(v) ? "https://chatgpt.com" : v.TrimEnd('/');
            }
        }

        public string Where(Config config)
        {
            return AuthPath(config);
        }

        public static string AuthPath(Config config)
        {
            string dir;
            if (!string.IsNullOrEmpty(config.CodexDir)) dir = Environment.ExpandEnvironmentVariables(config.CodexDir);
            else
            {
                dir = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }
            return Path.Combine(dir, "auth.json");
        }

        private static Dictionary<string, object> ReadTokens(string path)
        {
            Dictionary<string, object> auth;
            try
            {
                auth = Json.ParseObject(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (FileNotFoundException) { throw NotSignedIn(path); }
            catch (DirectoryNotFoundException) { throw NotSignedIn(path); }
            catch (Exception ex)
            {
                throw new UsageException("Read error", "Could not read " + path + ": " + ex.Message);
            }
            var tokens = Json.Dict(auth, "tokens");
            if (tokens == null || string.IsNullOrEmpty(Json.Str(tokens, "access_token")))
            {
                if (!string.IsNullOrEmpty(Json.Str(auth, "OPENAI_API_KEY")))
                    throw new UsageException("No plan", "Codex is signed in with an API key, which has no ChatGPT plan limits. Sign in with ChatGPT (`codex login`).");
                throw NotSignedIn(path);
            }
            return tokens;
        }

        private static UsageException NotSignedIn(string path)
        {
            return new UsageException("Sign in", "Codex is not signed in with ChatGPT on this PC (" + path +
                " not found). Install Codex and run `codex login`.");
        }

        private static UsageException Expired()
        {
            return new UsageException("Login expired",
                "Codex's saved ChatGPT login has expired. Open Codex once and it renews it; " +
                "this plugin only reads the login and never renews it itself.");
        }

        public UsageSnapshot Fetch(Config config)
        {
            string path = AuthPath(config);
            var tokens = ReadTokens(path);
            string token = Json.Str(tokens, "access_token");
            var claims = Providers.JwtClaims(token);
            double? exp = Json.Num(claims, "exp");
            if (exp.HasValue && Providers.NowMs() / 1000.0 > exp.Value - 30) throw Expired();

            HttpResult r = Get(config, tokens);
            if (r.Status == 401)
            {
                var latest = ReadTokens(path);
                if (Json.Str(latest, "access_token") == token) throw Expired();
                tokens = latest;
                r = Get(config, tokens);
                if (r.Status == 401) throw Expired();
            }
            if (r.Status == 403) throw new UsageException("Blocked", "chatgpt.com refused the request (403). It is usually a temporary browser check; it will retry.");
            if (r.Status == 429) throw new UsageException("Rate limited", "chatgpt.com rate-limited this client (429). It will retry later.");
            if (r.Status != 200) throw new UsageException("HTTP " + r.Status, "Codex usage request failed: HTTP " + r.Status + " " + Providers.Trim(r.Body));

            UsageSnapshot snap = Parse(Json.ParseObject(r.Body));
            if (string.IsNullOrEmpty(snap.Plan)) snap.Plan = PlanFromIdToken(Json.Str(tokens, "id_token"));
            return snap;
        }

        private static HttpResult Get(Config config, Dictionary<string, object> tokens)
        {
            var headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer " + Json.Str(tokens, "access_token");
            string account = Json.Str(tokens, "account_id");
            if (!string.IsNullOrEmpty(account)) headers["ChatGPT-Account-Id"] = account;
            headers["User-Agent"] = BrowserAgent;
            headers["Accept"] = "application/json";
            headers["Referer"] = "https://chatgpt.com/";

            HttpResult r = null;
            foreach (string path in new[] { "/backend-api/codex/usage", "/backend-api/wham/usage" })
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    r = Http.Send(config, "GET", Base + path, headers, null);
                    if (r.Status != 403) break;
                    Thread.Sleep(600 * (attempt + 1)); // the browser check usually passes on a retry
                }
                if (r.Status != 404) return r;
            }
            return r;
        }

        private static string PlanFromIdToken(string idToken)
        {
            var auth = Json.Dict(Providers.JwtClaims(idToken), "https://api.openai.com/auth");
            return Json.Str(auth, "chatgpt_plan_type");
        }

        public static UsageSnapshot Parse(Dictionary<string, object> body)
        {
            if (body == null) throw new UsageException("Bad reply", "The Codex usage reply was not a JSON object.");
            var snap = new UsageSnapshot();
            snap.Plan = Json.Str(body, "plan_type");

            AddWindows(snap, Json.Dict(body, "rate_limit"), null);
            object[] extra = Json.Array(body, "additional_rate_limits");
            if (extra != null)
            {
                foreach (object o in extra)
                {
                    var item = o as Dictionary<string, object>;
                    string name = Json.Str(item, "limit_name");
                    if (string.IsNullOrEmpty(name)) name = Json.Str(item, "metered_feature");
                    AddWindows(snap, Json.Dict(item, "rate_limit"), string.IsNullOrEmpty(name) ? "Other" : name);
                }
            }
            var review = Json.Dict(body, "code_review_rate_limit");
            if (review != null) AddWindows(snap, review, "Code review");
            if (snap.Limits.Count == 0 && Json.Dict(body, "rate_limit") == null)
                throw new UsageException("Bad reply", "The Codex usage reply has no rate limits.");

            var credits = Json.Dict(body, "credits");
            if (credits != null)
            {
                var x = new ExtraUsage();
                x.Enabled = Json.Bool(credits, "has_credits", false) || Json.Bool(credits, "unlimited", false);
                if (Json.Bool(credits, "unlimited", false)) x.Value = "\u221E";
                else
                {
                    double? balance = Json.Num(credits, "balance");
                    x.Value = balance.HasValue ? Math.Floor(balance.Value).ToString("0", CultureInfo.InvariantCulture) : "-";
                }
                x.Sub = "credits";
                snap.Extra = x;
            }
            return snap;
        }

        /// <summary>primary/secondary windows; the limit's own name scopes extra limits.</summary>
        private static void AddWindows(UsageSnapshot snap, Dictionary<string, object> rateLimit, string scope)
        {
            if (rateLimit == null) return;
            foreach (string key in new[] { "primary_window", "secondary_window" })
            {
                var w = Json.Dict(rateLimit, key);
                double? used = Json.Num(w, "used_percent");
                if (!used.HasValue) continue;
                double? seconds = Json.Num(w, "limit_window_seconds");
                var l = new Limit();
                l.Percent = Providers.ClampPct(used.Value);
                double? resetAt = Json.Num(w, "reset_at");
                double? resetAfter = Json.Num(w, "reset_after_seconds");
                if (resetAt.HasValue) l.ResetsAt = Providers.FromUnix(resetAt.Value);
                else if (resetAfter.HasValue) l.ResetsAt = DateTime.UtcNow.AddSeconds(resetAfter.Value);

                string window = WindowName(seconds, key == "primary_window" ? "Primary" : "Secondary");
                if (scope == null)
                {
                    l.Kind = window == "5-hour" ? "session" : window == "Weekly" ? "weekly" : "other";
                    l.Name = window == "5-hour" ? "Session" : window;
                }
                else
                {
                    l.Kind = "model";
                    l.Name = window;
                    l.Scope = scope;
                }
                snap.Limits.Add(l);
            }
        }

        private static string WindowName(double? seconds, string fallback)
        {
            if (!seconds.HasValue || seconds.Value <= 0) return fallback;
            double s = seconds.Value;
            if (Math.Abs(s - 18000) <= 1800) return "5-hour";
            if (Math.Abs(s - 86400) <= 8640) return "Daily";
            if (Math.Abs(s - 604800) <= 60480) return "Weekly";
            if (Math.Abs(s - 2592000) <= 259200) return "Monthly";
            return fallback;
        }
    }
}
