// Small helpers over JavaScriptSerializer's loose object graph.
// Written for the C# 5 compiler that ships inside Windows (csc v4.0.30319).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace AiUsageDeck
{
    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = 16 * 1024 * 1024;
            return s;
        }

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return Serializer.DeserializeObject(text);
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            return Parse(text) as Dictionary<string, object>;
        }

        public static string Serialize(object value)
        {
            return Serializer.Serialize(value);
        }

        public static Dictionary<string, object> Dict(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        public static object[] Array(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || value == null) return null;
            var arr = value as object[];
            if (arr != null) return arr;
            var list = value as ArrayList;
            if (list != null) return list.ToArray();
            return null;
        }

        public static string Str(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || value == null) return null;
            var s = value as string;
            if (s != null) return s;
            if (value is bool) return ((bool)value) ? "true" : "false";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static double? Num(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || value == null) return null;
            if (value is bool) return null;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is decimal) return (double)(decimal)value;
            if (value is double) return (double)value;
            var s = value as string;
            double parsed;
            if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return null;
        }

        public static int Int(Dictionary<string, object> obj, string key, int fallback)
        {
            double? n = Num(obj, key);
            return n.HasValue ? (int)Math.Round(n.Value) : fallback;
        }

        public static bool Bool(Dictionary<string, object> obj, string key, bool fallback)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;
            var s = value as string;
            if (s != null)
            {
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s == "on") return true;
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0" || s == "off") return false;
            }
            double? n = Num(obj, key);
            return n.HasValue ? n.Value != 0 : fallback;
        }

        public static string Escape(string value)
        {
            if (value == null) return "";
            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>ISO-8601 timestamp to UTC, or null.</summary>
        public static DateTime? Time(Dictionary<string, object> obj, string key)
        {
            string s = Str(obj, key);
            if (string.IsNullOrEmpty(s)) return null;
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out parsed))
                return parsed.UtcDateTime;
            return null;
        }
    }
}
