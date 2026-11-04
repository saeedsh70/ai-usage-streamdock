// The provider-neutral usage model every key is drawn from.

using System;
using System.Collections.Generic;

namespace AiUsageDeck
{
    internal sealed class Limit
    {
        /// <summary>session (about 5 hours) | weekly | model (a model's or group's own limit) | other</summary>
        public string Kind;
        /// <summary>Display name: Session, Weekly, Opus, Gemini 3 Pro ...</summary>
        public string Name;
        /// <summary>What the limit covers when a provider has several, e.g. an Antigravity quota group.</summary>
        public string Scope;
        /// <summary>Used, 0..100.</summary>
        public double Percent;
        /// <summary>UTC.</summary>
        public DateTime? ResetsAt;
    }

    /// <summary>Money or credits beyond the plan's limits (Claude extra usage, Codex credits, Antigravity prompt credits).</summary>
    internal sealed class ExtraUsage
    {
        public bool Enabled;
        /// <summary>Big value on the key, e.g. "$12.34" or "480".</summary>
        public string Value;
        /// <summary>Line under it, e.g. "of $50".</summary>
        public string Sub;
        /// <summary>Used share, 0..100, when known.</summary>
        public double? Percent;
    }

    internal sealed class UsageSnapshot
    {
        public readonly List<Limit> Limits = new List<Limit>();
        public ExtraUsage Extra;
        public string Plan;
        public DateTime FetchedAt = DateTime.UtcNow;

        /// <summary>The first limit of a kind, optionally the first whose name or scope contains filter.</summary>
        public Limit Find(string kind, string filter)
        {
            // An exact name (as picked from the settings list) wins over a partial one.
            if (!string.IsNullOrEmpty(filter))
                foreach (Limit l in Limits)
                    if (l.Kind == kind && (Same(l.Name, filter) || Same(l.Scope, filter))) return l;
            foreach (Limit l in Limits)
            {
                if (l.Kind != kind) continue;
                if (string.IsNullOrEmpty(filter)) return l;
                if (Contains(l.Name, filter) || Contains(l.Scope, filter)) return l;
            }
            return null;
        }

        private static bool Same(string a, string b)
        {
            return a != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string s, string part)
        {
            return s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal sealed class UsageException : Exception
    {
        /// <summary>Short status for the key: Sign in, Login expired, Offline, Not running ...</summary>
        public readonly string Short;
        public UsageException(string shortText, string detail) : base(detail) { Short = shortText; }
    }

    internal interface IProvider
    {
        /// <summary>claude | chatgpt | antigravity</summary>
        string Id { get; }
        /// <summary>Product name for the settings panel.</summary>
        string Name { get; }
        /// <summary>Short tag for default key labels: CLAUDE, GPT, AGY.</summary>
        string Tag { get; }
        /// <summary>Label of the Extra metric on this provider: EXTRA or CREDITS.</summary>
        string ExtraLabel { get; }
        /// <summary>Where the login or connection is looked for, for the settings panel.</summary>
        string Where(Config config);
        UsageSnapshot Fetch(Config config);
    }

    internal static class Providers
    {
        public static readonly IProvider[] All = { new ClaudeProvider(), new CodexProvider(), new AntigravityProvider() };

        public static IProvider Get(string id)
        {
            foreach (IProvider p in All)
                if (p.Id == id) return p;
            return All[0];
        }

        public static double NowMs()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        public static DateTime FromUnix(double seconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }

        public static double ClampPct(double v)
        {
            if (double.IsNaN(v)) return 0;
            return v < 0 ? 0 : (v > 100 ? 100 : v);
        }

        public static string Trim(string s)
        {
            if (s == null) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }

        /// <summary>The JSON payload of a JWT, without verifying it (only used to read an expiry or a plan name).</summary>
        public static Dictionary<string, object> JwtClaims(string jwt)
        {
            try
            {
                string[] parts = (jwt ?? "").Split('.');
                if (parts.Length < 2) return null;
                string b64 = parts[1].Replace('-', '+').Replace('_', '/');
                while (b64.Length % 4 != 0) b64 += "=";
                return Json.ParseObject(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string Pretty(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Other";
            return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace('_', ' '));
        }
    }
}
