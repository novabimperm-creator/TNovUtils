using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using TNovUtils.Issues.Config;

namespace TNovUtils.Issues.Api
{
    /// <summary>
    /// Поддержка сессии: обновление access по refresh (rotate-on-use) и logout.
    /// Первичный вход — через браузер (BrowserAuthService, ТЗ): встроенного окна
    /// логина больше нет.
    /// </summary>
    public sealed class AuthService
    {
        private readonly HttpClient _http;
        private readonly TokenStore _tokens;
        private readonly CookiePersistence _cookies;
        private readonly System.Net.CookieContainer _container;
        private readonly Uri _baseUri;

        public AuthService(HttpClient http, TokenStore tokens, CookiePersistence cookies,
                           System.Net.CookieContainer container, Uri baseUri)
        {
            _http = http; _tokens = tokens; _cookies = cookies; _container = container; _baseUri = baseUri;
        }

        /// <summary>
        /// Обновить access по сохранённому refresh. false → нужен повторный вход.
        ///
        /// 🔴 Файл входа общий для всех экземпляров плагина на машине (окно «Вопросов»,
        /// «Модель», старая надстройка TNovProIssues), а refresh ротируется. Раньше
        /// сессия читала файл один раз при создании: вошёл человек позже — она об этом
        /// не знала («Нет входа» сразу после входа, 2026-09-28); повернул вход соседний
        /// экземпляр — её копия протухала, и отказ сервера СТИРАЛ файл, то есть
        /// разлогинивал человека везде. Теперь: сначала свежий вход с диска, после
        /// отказа — ещё раз с диска, стираем только тот вход, от которого отказался сервер.
        /// </summary>
        public async Task<bool> RefreshAsync()
        {
            var saved = _tokens.PeekSaved();
            if (!string.IsNullOrEmpty(saved) && saved != _tokens.RefreshToken) _tokens.Adopt(saved);
            if (!_tokens.HasRefresh) return false;

            var used = _tokens.RefreshToken;
            if (await TryRefreshAsync(used)) return true;

            var now = _tokens.PeekSaved();
            if (!string.IsNullOrEmpty(now) && now != used)
            {
                _tokens.Adopt(now);
                if (await TryRefreshAsync(now)) return true;
                used = now;
            }
            if (_tokens.PeekSaved() == used) _tokens.Clear();
            else _tokens.AccessToken = null;
            return false;
        }

        private async Task<bool> TryRefreshAsync(string refreshToken)
        {
            var body = Json.Serialize(new { refreshToken });
            using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/refresh-token")))
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var resp = await _http.SendAsync(req))
                {
                    if (!resp.IsSuccessStatusCode) return false;
                    var text = await resp.Content.ReadAsStringAsync();
                    var rr = Json.Deserialize<RefreshResponse>(text);
                    if (rr == null || string.IsNullOrEmpty(rr.AccessToken)) return false;
                    _tokens.SetTokens(rr.AccessToken, rr.RefreshToken); // refresh ротируется
                    _cookies.Save(_container, _baseUri);
                    return true;
                }
            }
        }

        public async Task LogoutAsync()
        {
            try
            {
                var body = Json.Serialize(new { refreshToken = _tokens.RefreshToken });
                using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/logout")))
                {
                    if (!string.IsNullOrEmpty(_tokens.AccessToken))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    await _http.SendAsync(req);
                }
            }
            catch { /* ignore */ }
            finally { _tokens.Clear(); }
        }
    }
}
