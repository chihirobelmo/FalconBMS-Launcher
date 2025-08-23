using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FalconBMS.Launcher.Windows
{
    internal sealed class ApiSession
    {
        private static readonly Lazy<ApiSession> _lazy = new Lazy<ApiSession>(() => new ApiSession());
        public static ApiSession Instance => _lazy.Value;

        private readonly CookieContainer _cookies;
        private readonly HttpClientHandler _handler;
        private readonly HttpClient _client;

        public Uri BaseUri { get; }
        public HttpClient Client => _client;

        private ApiSession()
        {
            // Default base URI; consider making configurable.
            BaseUri = new Uri("http://localhost:3000");

            // TLS12 for HTTPS if needed
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            _cookies = new CookieContainer();
            _handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                AllowAutoRedirect = true,
                UseCookies = true
            };
            _client = new HttpClient(_handler) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(15) };
        }

        public async Task<bool> LoginAsync(string email, string password, bool rememberMe)
        {
            // 1) GET /login and extract CSRF token (Accept text/html to avoid 406)
            string loginHtml;
            using (var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, "/login")))
            {
                req.Headers.Accept.Clear();
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                var resp = await _client.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                loginHtml = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }

            var m = Regex.Match(loginHtml, "name=\"authenticity_token\" value=\"([^\"]+)\"");
            if (!m.Success) throw new Exception("authenticity_token not found");
            var csrf = WebUtility.HtmlDecode(m.Groups[1].Value);

            // 2) POST credentials (also Accept HTML)
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("authenticity_token", csrf),
                new KeyValuePair<string,string>("email", email ?? string.Empty),
                new KeyValuePair<string,string>("password", password ?? string.Empty),
                new KeyValuePair<string,string>("remember_me", rememberMe ? "1" : "0"),
            });

            using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "/login")))
            {
                req.Headers.Accept.Clear();
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                req.Headers.Referrer = new Uri(BaseUri, "/login");
                req.Content = form;
                var res = await _client.SendAsync(req).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return false;
            }

            // A simple heuristic: we consider logged-in if any cookies were set for base domain
            var col = _cookies.GetCookies(BaseUri);
            return col != null && col.Count > 0;
        }

        public IEnumerable<Cookie> GetAllCookies()
        {
            foreach (Cookie c in _cookies.GetCookies(BaseUri))
                yield return c;
        }

        public async Task<bool> LogoutAsync()
        {
            // Fetch CSRF token from root (fallback to /login if not present)
            string token = null;
            foreach (var path in new[] { "/", "/login" })
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, path)))
                    {
                        req.Headers.Accept.Clear();
                        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                        var resp = await _client.SendAsync(req).ConfigureAwait(false);
                        resp.EnsureSuccessStatusCode();
                        var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        token = ExtractCsrfToken(html);
                        if (!string.IsNullOrEmpty(token)) break;
                    }
                }
                catch { /* try next */ }
            }

            if (string.IsNullOrEmpty(token))
                throw new Exception("csrf-token meta not found");

            var tried = new List<string>();
            foreach (var path in new[] { "/logout", "/users/sign_out" })
            {
                var logoutUri = new Uri(BaseUri, path);

                // Try DELETE first
                using (var req = new HttpRequestMessage(HttpMethod.Delete, logoutUri))
                {
                    req.Headers.Accept.Clear();
                    req.Headers.Accept.ParseAdd("*/*");
                    req.Headers.Referrer = new Uri(BaseUri, "/");
                    req.Headers.Add("X-CSRF-Token", token);
                    var resp = await _client.SendAsync(req).ConfigureAwait(false);
                    var code = (int)resp.StatusCode;
                    tried.Add($"DELETE {path} -> {code}");
                    if (code >= 200 && code < 400) return true;
                    if (code != 404 && code != 405) // if not NotFound/MethodNotAllowed, don't try fallback blindly
                    {
                        string body = null;
                        try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                        var reason = resp.ReasonPhrase;
                        throw new Exception($"Logout failed: HTTP {code} {reason}{(string.IsNullOrEmpty(body) ? string.Empty : ", body: " + Truncate(body, 200))}");
                    }
                }

                // Fallback: POST with method override and authenticity_token param
                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string,string>("authenticity_token", token),
                    new KeyValuePair<string,string>("_method", "delete"),
                });

                using (var req = new HttpRequestMessage(HttpMethod.Post, logoutUri))
                {
                    req.Headers.Accept.Clear();
                    req.Headers.Accept.ParseAdd("*/*");
                    req.Headers.Referrer = new Uri(BaseUri, "/");
                    req.Headers.Add("X-CSRF-Token", token);
                    req.Content = form;
                    var resp = await _client.SendAsync(req).ConfigureAwait(false);
                    var code = (int)resp.StatusCode;
                    tried.Add($"POST {path} (_method=delete) -> {code}");
                    if (code >= 200 && code < 400) return true;
                    if (code != 404)
                    {
                        string body = null;
                        try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                        var reason = resp.ReasonPhrase;
                        throw new Exception($"Logout failed: HTTP {code} {reason}{(string.IsNullOrEmpty(body) ? string.Empty : ", body: " + Truncate(body, 200))}");
                    }
                }
            }

            throw new Exception("Logout failed: endpoints not found. Tried: " + string.Join(", ", tried));
        }

        private static string ExtractCsrfToken(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            // Match meta tag with name='csrf-token' regardless of attribute order and quote style
            var patterns = new[]
            {
                "<meta[^>]*name=[\"']csrf-token[\"'][^>]*content=[\"']([^\"']+)[\"'][^>]*>",
                "<meta[^>]*content=[\"']([^\"']+)[\"'][^>]*name=[\"']csrf-token[\"'][^>]*>"
            };
            foreach (var p in patterns)
            {
                var m = Regex.Match(html, p, RegexOptions.IgnoreCase);
                if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value);
            }
            return null;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }
    }
}
