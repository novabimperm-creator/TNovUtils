using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TNovUtils.Issues.Config;

namespace TNovUtils.Issues.Api
{
    /// <summary>
    /// REST-клиент к API модуля Issues в TNovPRO («мост» плагина).
    /// Добавляет Bearer access-токен, на 401 пытается refresh и повторяет запрос
    /// один раз; маппит коды ошибок дома (409 → ConflictException и т.д.).
    /// </summary>
    public sealed class ApiClient
    {
        private readonly HttpClient _http;
        private readonly TokenStore _tokens;
        private readonly AuthService _auth;
        private readonly Uri _baseUri;

        public ApiClient(HttpClient http, TokenStore tokens, AuthService auth, Uri baseUri)
        {
            _http = http; _tokens = tokens; _auth = auth; _baseUri = baseUri;
        }

        public Uri BaseUri => _baseUri;

        // ── Низкоуровневая отправка с авто-refresh ───────────────────────────
        private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage req)
        {
            if (!string.IsNullOrEmpty(_tokens.AccessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
            return await _http.SendAsync(req);
        }

        private async Task<string> SendAsync(Func<HttpRequestMessage> make)
        {
            var resp = await SendOnceAsync(make());
            if (resp.StatusCode == HttpStatusCode.Unauthorized && _tokens.HasRefresh)
            {
                resp.Dispose();
                if (await _auth.RefreshAsync())
                    resp = await SendOnceAsync(make());
                else
                    throw new AuthRequiredException();
            }
            using (resp)
            {
                var body = resp.Content != null ? await resp.Content.ReadAsStringAsync() : "";
                if (!resp.IsSuccessStatusCode) throw MapError(resp.StatusCode, body);
                return body;
            }
        }

        private ApiException MapError(HttpStatusCode status, string body)
        {
            string error = null, code = null;
            JObject jo = null;
            try { jo = JObject.Parse(body); error = (string)jo["error"]; code = (string)jo["code"]; }
            catch { /* not json */ }

            if (status == HttpStatusCode.Unauthorized) return new AuthRequiredException(error ?? "Требуется вход");
            if (status == HttpStatusCode.Conflict)
            {
                // 409 бывает двух видов: устаревший updatedAt (CONFLICT) и недопустимый
                // переход статуса (TRANSITION_INVALID). Конфликтом версий считаем только первый.
                if (code == "CONFLICT")
                {
                    Issue current = null;
                    try { if (jo?["current"] != null) current = jo["current"].ToObject<Issue>(); } catch { }
                    return new ConflictException(current, body);
                }
                return new ApiException(status, error ?? "Conflict", code, body);
            }
            if (status == HttpStatusCode.BadRequest && code == "PROJECT_UNRESOLVED")
                return new ProjectUnresolvedException(body);
            return new ApiException(status, error ?? $"HTTP {(int)status}", code, body);
        }

        private HttpRequestMessage Make(HttpMethod m, string path, object json = null)
        {
            var req = new HttpRequestMessage(m, new Uri(_baseUri, path.TrimStart('/')));
            if (json != null)
                req.Content = new StringContent(Json.Serialize(json), Encoding.UTF8, "application/json");
            return req;
        }

        private static string Q(string s) => Uri.EscapeDataString(s ?? "");

        // ── Проверка связи ─────────────────────────────────────────────────────
        public sealed class ConnectionResult
        {
            public bool Reachable;      // сервер ответил (любой HTTP-код)
            public bool Authorized;     // ответ 2xx на health под нашим токеном
            public int StatusCode;      // HTTP-код ответа (0 — не дошли)
            public long ElapsedMs;      // round-trip
            public string Error;        // текст исключения при недоступности
        }

        /// <summary>
        /// Пробует связаться с TNovPRO: GET api/issues/_health (за requireAuth).
        /// Ответ вообще → сервер доступен; 200 → доступен и авторизованы; 401 → доступен,
        /// нужен вход; исключение → сети/сервера нет. На 401 с сохранённым refresh пробуем
        /// обновить токен и повторить один раз (как в обычных запросах).
        /// </summary>
        public async Task<ConnectionResult> CheckConnectionAsync()
        {
            var res = new ConnectionResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var resp = await SendOnceAsync(Make(HttpMethod.Get, "api/issues/_health"));
                if (resp.StatusCode == HttpStatusCode.Unauthorized && _tokens.HasRefresh)
                {
                    resp.Dispose();
                    if (await _auth.RefreshAsync())
                        resp = await SendOnceAsync(Make(HttpMethod.Get, "api/issues/_health"));
                }
                using (resp)
                {
                    res.Reachable = true;
                    res.StatusCode = (int)resp.StatusCode;
                    res.Authorized = resp.IsSuccessStatusCode;
                }
            }
            catch (Exception ex) { res.Error = ex.Message; }
            sw.Stop();
            res.ElapsedMs = sw.ElapsedMilliseconds;
            return res;
        }

        // ── Issues ───────────────────────────────────────────────────────────
        public async Task<List<Issue>> GetIssuesByModelAsync(string modelName, string status = null, bool archived = false)
        {
            var qs = new List<string> { "model=" + Q(modelName) };
            if (!string.IsNullOrEmpty(status)) qs.Add("status=" + Q(status));
            if (archived) qs.Add("archived=1");
            var body = await SendAsync(() => Make(HttpMethod.Get, "api/issues?" + string.Join("&", qs)));
            return Json.Deserialize<IssuesListResponse>(body).Issues;
        }

        public async Task<List<Issue>> GetMyIssuesAsync()
        {
            var body = await SendAsync(() => Make(HttpMethod.Get, "api/issues?mine=1"));
            return Json.Deserialize<IssuesListResponse>(body).Issues;
        }

        /// <summary>Гибкий список с серверными фильтрами (модель по имени / статус / проект / «мои»).</summary>
        public async Task<List<Issue>> GetIssuesAsync(string model = null, string status = null, string projectId = null, bool mine = false)
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(model)) qs.Add("model=" + Q(model));
            if (!string.IsNullOrEmpty(status)) qs.Add("status=" + Q(status));
            if (!string.IsNullOrEmpty(projectId)) qs.Add("projectId=" + Q(projectId));
            if (mine) qs.Add("mine=1");
            var path = "api/issues" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
            var body = await SendAsync(() => Make(HttpMethod.Get, path));
            return Json.Deserialize<IssuesListResponse>(body).Issues;
        }

        public async Task<Issue> GetIssueAsync(string id)
        {
            var body = await SendAsync(() => Make(HttpMethod.Get, "api/issues/" + Q(id)));
            return Json.Deserialize<Issue>(body);
        }

        public async Task<Issue> CreateIssueAsync(List<IssueModelRef> models, string description,
            List<string> assignees, string projectId = null)
        {
            var payload = new
            {
                models = models ?? new List<IssueModelRef>(), // 1–2 слота (modelId + elementId)
                projectId,
                description,
                assignees = assignees ?? new List<string>(),
            };
            var body = await SendAsync(() => Make(HttpMethod.Post, "api/issues", payload));
            return Json.Deserialize<Issue>(body);
        }

        /// <summary>changes — словарь полей allowlist; updatedAt добавляется автоматически.</summary>
        public async Task<Issue> UpdateIssueAsync(string id, string updatedAt, IDictionary<string, object> changes)
        {
            var payload = new Dictionary<string, object>(changes ?? new Dictionary<string, object>())
            {
                ["updatedAt"] = updatedAt,
            };
            var body = await SendAsync(() => Make(HttpMethod.Put, "api/issues/" + Q(id), payload));
            return Json.Deserialize<Issue>(body);
        }

        // ── Пользователи (пикер ответственных) ─────────────────────────────────
        /// <summary>
        /// Список пользователей платформы для выбора ответственных (GET /api/users,
        /// requireAuth). Сервер отдаёт голый JSON-массив без секретов. Заблокированных
        /// и удалённых отсеиваем — их нельзя назначать.
        /// </summary>
        public async Task<List<DirectoryUser>> GetUsersAsync()
        {
            var body = await SendAsync(() => Make(HttpMethod.Get, "api/users"));
            var all = Json.Deserialize<List<DirectoryUser>>(body) ?? new List<DirectoryUser>();
            return all.FindAll(u => u != null && !u.IsBlocked && !u.IsDeleted && !string.IsNullOrEmpty(u.Id));
        }

        // ── Projects / Models ─────────────────────────────────────────────────
        public async Task<List<Project>> GetProjectsAsync(bool archived = false)
        {
            var body = await SendAsync(() => Make(HttpMethod.Get, "api/issues/projects" + (archived ? "?archived=1" : "")));
            return Json.Deserialize<ProjectsListResponse>(body).Projects;
        }

        public async Task<List<Model>> GetModelsAsync(string projectId = null)
        {
            var path = "api/issues/models" + (string.IsNullOrEmpty(projectId) ? "" : "?projectId=" + Q(projectId));
            var body = await SendAsync(() => Make(HttpMethod.Get, path));
            return Json.Deserialize<ModelsListResponse>(body).Models;
        }

        /// <summary>Upsert модели по нормализованному имени + авто-привязка проекта.</summary>
        public async Task<Model> UpsertModelAsync(string name)
        {
            var body = await SendAsync(() => Make(HttpMethod.Post, "api/issues/models", new { name }));
            return Json.Deserialize<Model>(body);
        }

        // ── Photos ─────────────────────────────────────────────────────────────
        public async Task<PhotoMeta> UploadPhotoAsync(string issueId, byte[] bytes, string fileName, string mime = "image/jpeg")
        {
            Func<HttpRequestMessage> make = () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/issues/" + Q(issueId) + "/photos"));
                var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(mime);
                form.Add(file, "file", fileName);
                req.Content = form;
                return req;
            };
            var body = await SendAsync(make);
            return Json.Deserialize<PhotoMeta>(body);
        }

        public async Task DeletePhotoAsync(string issueId, string photoId)
        {
            await SendAsync(() => Make(HttpMethod.Delete, "api/issues/" + Q(issueId) + "/photos/" + Q(photoId)));
        }

        // ── 3D-фрагмент (.glb) ─────────────────────────────────────────────────
        /// <summary>
        /// Залить 3D-фрагмент замечания (binary glTF) — контракт `POST /api/issues/:id/geometry`,
        /// multipart, поле `file`, MIME `model/gltf-binary` (см. timetta-plus/ISSUES_HANDOFF.md).
        /// Upsert: повторная заливка заменяет. Сервер ставит `has_geometry=true` и шлёт `issue-updated`.
        /// </summary>
        public async Task UploadGeometryAsync(string issueId, byte[] glb, string fileName = "issue.glb",
            string mime = "model/gltf-binary")
        {
            Func<HttpRequestMessage> make = () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/issues/" + Q(issueId) + "/geometry"));
                var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(glb);
                file.Headers.ContentType = new MediaTypeHeaderValue(mime);
                form.Add(file, "file", fileName);
                req.Content = form;
                return req;
            };
            await SendAsync(make);
        }

        /// <summary>Скачать байты по относительному url (фото/превью) с авто-refresh.</summary>
        public async Task<byte[]> GetBytesAsync(string relativeUrl)
        {
            var resp = await SendOnceAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, relativeUrl.TrimStart('/'))));
            if (resp.StatusCode == HttpStatusCode.Unauthorized && _tokens.HasRefresh)
            {
                resp.Dispose();
                if (!await _auth.RefreshAsync()) throw new AuthRequiredException();
                resp = await SendOnceAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, relativeUrl.TrimStart('/'))));
            }
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) throw new ApiException(resp.StatusCode, "Не удалось загрузить файл");
                return await resp.Content.ReadAsByteArrayAsync();
            }
        }
    }

    /// <summary>
    /// Фабрика «сессии»: собирает HttpClient с CookieContainer (для device-trust),
    /// подтягивает сохранённые cookie/refresh, и связывает TokenStore + AuthService + ApiClient.
    /// </summary>
    public sealed class ApiSession
    {
        public PluginConfig Config { get; }
        public TokenStore Tokens { get; }
        public AuthService Auth { get; }
        public BrowserAuthService Browser { get; }
        public ApiClient Client { get; }

        private readonly CookiePersistence _cookies;
        private readonly CookieContainer _container;

        public ApiSession(PluginConfig config)
        {
            Config = config;
            _container = new CookieContainer();
            _cookies = new CookiePersistence();
            _cookies.Load(_container, config.BaseUri);

            var handler = new HttpClientHandler
            {
                CookieContainer = _container,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TNovPRO-Issues-Revit/0.1");

            Tokens = new TokenStore();
            Auth = new AuthService(http, Tokens, _cookies, _container, config.BaseUri);
            Browser = new BrowserAuthService(http, Tokens, config.BaseUri);
            Client = new ApiClient(http, Tokens, Auth, config.BaseUri);
        }

        /// <summary>Есть ли шанс войти без ввода пароля (есть сохранённый refresh).</summary>
        public Task<bool> TryRestoreSessionAsync() => Auth.RefreshAsync();
    }
}
