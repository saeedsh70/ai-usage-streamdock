using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AiUsageDeck
{
    internal sealed class HttpResult
    {
        public int Status;
        public string Body;
    }

    internal static class Http
    {
        static Http()
        {
            // .NET Framework 4.x defaults may not include TLS 1.2 on older Windows builds.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }
            ServicePointManager.Expect100Continue = false;
        }

        /// <param name="localSelfSigned">
        /// For a server on 127.0.0.1 that uses a self-signed certificate (Antigravity's language server):
        /// accept its certificate and never use a proxy. Refused for any other host.
        /// </param>
        public static HttpResult Send(Config config, string method, string url, Dictionary<string, string> headers,
                                      string body, bool localSelfSigned = false, int timeoutMs = 20000)
        {
            var uri = new Uri(url);
            if (localSelfSigned && !uri.IsLoopback) throw new ArgumentException("self-signed is only allowed for loopback");

            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.Method = method;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (localSelfSigned)
            {
                req.Proxy = null;
                req.ServerCertificateValidationCallback = AcceptLoopback;
            }
            else req.Proxy = ProxyFor(config);

            if (headers != null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    switch (h.Key.ToLowerInvariant())
                    {
                        case "user-agent": req.UserAgent = h.Value; break;
                        case "accept": req.Accept = h.Value; break;
                        case "content-type": req.ContentType = h.Value; break;
                        case "referer": req.Referer = h.Value; break;
                        default: req.Headers[h.Key] = h.Value; break;
                    }
                }
            }

            try
            {
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    req.ContentLength = bytes.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return Read(resp);
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    using (resp) return Read(resp);
                }
                throw new UsageException("Offline", "Network error for " + uri.Host + ": " + ex.Message +
                    (string.IsNullOrEmpty(config.Proxy) || localSelfSigned ? "" : " (proxy " + config.Proxy + ")"));
            }
        }

        private static bool AcceptLoopback(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            var req = sender as HttpWebRequest;
            return req != null && req.RequestUri.IsLoopback;
        }

        private static HttpResult Read(HttpWebResponse resp)
        {
            var r = new HttpResult();
            r.Status = (int)resp.StatusCode;
            using (Stream s = resp.GetResponseStream())
            using (var reader = new StreamReader(s, Encoding.UTF8))
                r.Body = reader.ReadToEnd();
            return r;
        }

        private static IWebProxy ProxyFor(Config config)
        {
            if (string.IsNullOrEmpty(config.Proxy))
            {
                IWebProxy system = WebRequest.GetSystemWebProxy();
                system.Credentials = CredentialCache.DefaultCredentials;
                return system;
            }
            if (config.Proxy.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            string p = config.Proxy;
            if (p.IndexOf("://", StringComparison.Ordinal) < 0) p = "http://" + p;
            return new WebProxy(p);
        }
    }
}
