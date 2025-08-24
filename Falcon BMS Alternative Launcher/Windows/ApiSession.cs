using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FalconBMS.Launcher.Windows
{
    internal sealed class ApiSession
    {
        private static readonly Lazy<ApiSession> _lazy = new Lazy<ApiSession>(() => new ApiSession());
        public static ApiSession Instance => _lazy.Value;

    private readonly CookieContainer _cookies;
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _client;
    private string _bearerToken;

        public Uri BaseUri { get; }
    public HttpClient Client => _client;
    public bool IsLoggedIn => !string.IsNullOrEmpty(_bearerToken);

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
            // Bearer token login via JSON API: POST /api/token { email, password }
            var payload = $"{{\"email\":\"{EscapeJson(email)}\",\"password\":\"{EscapeJson(password)}\"}}";
            using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "/api/token")))
            {
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using (var res = await _client.SendAsync(req).ConfigureAwait(false))
                {
                    if (!res.IsSuccessStatusCode)
                        return false;

                    var token = ExtractTokenFromAuthHeader(res) ?? await ExtractTokenAsync(res).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(token))
                        return false;

                    SetBearer(token);
                    return true;
                }
            }
        }

        public IEnumerable<Cookie> GetAllCookies()
        {
            foreach (Cookie c in _cookies.GetCookies(BaseUri))
                yield return c;
        }

        public async Task<bool> LogoutAsync()
        {
            if (!IsLoggedIn)
            {
                ClearBearer();
                return true;
            }

            using (var req = new HttpRequestMessage(HttpMethod.Delete, new Uri(BaseUri, "/api/token")))
            {
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                using (var resp = await _client.SendAsync(req).ConfigureAwait(false))
                {
                    var code = (int)resp.StatusCode;
                    // Consider 2xx success or 401 (already invalid/expired) as successful logout
                    bool ok = code >= 200 && code < 300 || code == 401;
                    ClearBearer();
                    return ok;
                }
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "...";
        }

        private void SetBearer(string token)
        {
            _bearerToken = token;
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        private void ClearBearer()
        {
            _bearerToken = null;
            _client.DefaultRequestHeaders.Authorization = null;
            // Also clear cookies if any lingering
            try
            {
                foreach (Cookie c in _cookies.GetCookies(BaseUri))
                {
                    c.Expired = true;
                }
            }
            catch { }
        }

        private static async Task<string> ExtractTokenAsync(HttpResponseMessage res)
        {
            string content = null;
            try { content = await res.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
            if (string.IsNullOrWhiteSpace(content)) return null;

            // Try JSON first
            try
            {
                var token = TryParseTokenFromJson(content);
                if (!string.IsNullOrEmpty(token)) return token;
            }
            catch { /* not json or parsing failed */ }

            // Fallback: if body is just the token string
            var trimmed = content.Trim().Trim('"');
            if (!string.IsNullOrEmpty(trimmed) && trimmed.Length > 10)
                return trimmed;

            return null;
        }

        private static string ExtractTokenFromAuthHeader(HttpResponseMessage res)
        {
            try
            {
                if (res.Headers.TryGetValues("Authorization", out var values))
                {
                    foreach (var v in values)
                    {
                        var s = v?.Trim();
                        if (s != null && s.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                            return s.Substring(7).Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        private static string TryParseTokenFromJson(string json)
        {
            var root = JToken.Parse(json);
            if (root == null) return null;

            if (root.Type == JTokenType.String)
                return root.Value<string>();

            // Common property names
            var candidates = new[] { "token", "access_token", "jwt", "bearer", "authorization" };
            foreach (var name in candidates)
            {
                var t = root.SelectToken(name) ?? root.SelectToken($"$.{name}") ?? root.SelectToken($"$..{name}");
                if (t != null && t.Type == JTokenType.String)
                    return t.Value<string>();
            }

            return null;
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
