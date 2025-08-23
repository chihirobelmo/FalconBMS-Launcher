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
            // 1) GET /login and extract CSRF token
            var loginHtml = await _client.GetStringAsync("/login").ConfigureAwait(false);
            var m = Regex.Match(loginHtml, "name=\"authenticity_token\" value=\"([^\"]+)\"");
            if (!m.Success) throw new Exception("authenticity_token not found");
            var csrf = WebUtility.HtmlDecode(m.Groups[1].Value);

            // 2) POST credentials
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("authenticity_token", csrf),
                new KeyValuePair<string,string>("email", email ?? string.Empty),
                new KeyValuePair<string,string>("password", password ?? string.Empty),
                new KeyValuePair<string,string>("remember_me", rememberMe ? "1" : "0"),
            });

            var res = await _client.PostAsync("/login", form).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return false;

            // A simple heuristic: we consider logged-in if any cookies were set for base domain
            var col = _cookies.GetCookies(BaseUri);
            return col != null && col.Count > 0;
        }

        public IEnumerable<Cookie> GetAllCookies()
        {
            foreach (Cookie c in _cookies.GetCookies(BaseUri))
                yield return c;
        }
    }
}
