using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using TNovUtils.Issues.Config;

namespace TNovUtils.Issues.Api
{
    /// <summary>
    /// Авторизация плагина ЧЕРЕЗ БРАУЗЕР (ТЗ, по образцу Tangl). Loopback-flow:
    ///  1) поднимаем локальный TcpListener на 127.0.0.1:&lt;free port&gt;;
    ///  2) открываем системный браузер на /api/issues/desktop-auth/start?cb=&amp;state=;
    ///  3) сервер (по refresh-cookie веба) редиректит на cb?code=&amp;state=;
    ///  4) ловим code, обмениваем на токены через /desktop-auth/exchange.
    /// TcpListener (а не HttpListener) — чтобы не требовать URL-ACL/админ-прав.
    /// </summary>
    public sealed class BrowserAuthService
    {
        private readonly HttpClient _http;
        private readonly TokenStore _tokens;
        private readonly Uri _baseUri;

        public BrowserAuthService(HttpClient http, TokenStore tokens, Uri baseUri)
        {
            _http = http; _tokens = tokens; _baseUri = baseUri;
        }

        public async Task<bool> AuthorizeAsync(TimeSpan? timeout = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string state = Guid.NewGuid().ToString("N");
                string cb = $"http://127.0.0.1:{port}/";
                var startUrl = new Uri(_baseUri,
                    $"api/issues/desktop-auth/start?cb={Uri.EscapeDataString(cb)}&state={state}").ToString();

                try { Process.Start(new ProcessStartInfo(startUrl) { UseShellExecute = true }); }
                catch { return false; }

                var acceptTask = listener.AcceptTcpClientAsync();
                var to = timeout ?? TimeSpan.FromMinutes(3);
                if (await Task.WhenAny(acceptTask, Task.Delay(to)) != acceptTask) return false;

                string code, gotState;
                using (var client = await acceptTask)
                {
                    ReadCallback(client, out code, out gotState);
                    WriteHtml(client, code != null && gotState == state
                        ? "Готово! Вернитесь в Revit."
                        : "Не удалось завершить вход. Закройте вкладку и повторите из Revit.");
                }

                if (code == null || gotState != state) return false;
                return await ExchangeAsync(code);
            }
            catch { return false; }
            finally { try { listener.Stop(); } catch { } }
        }

        private async Task<bool> ExchangeAsync(string code)
        {
            var body = Json.Serialize(new { code });
            using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/issues/desktop-auth/exchange")))
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var resp = await _http.SendAsync(req))
                {
                    if (!resp.IsSuccessStatusCode) return false;
                    var text = await resp.Content.ReadAsStringAsync();
                    var tr = Json.Deserialize<TokenResponse>(text);
                    if (tr == null || string.IsNullOrEmpty(tr.AccessToken)) return false;
                    _tokens.SetTokens(tr.AccessToken, tr.RefreshToken);
                    _tokens.User = tr.User;
                    return true;
                }
            }
        }

        // Минимальный разбор HTTP-запроса от браузера: первая строка "GET /?code=..&state=.. HTTP/1.1".
        private static void ReadCallback(TcpClient client, out string code, out string state)
        {
            code = null; state = null;
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                var buf = new byte[8192];
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) return;
                string text = Encoding.ASCII.GetString(buf, 0, n);
                string firstLine = text.Split('\n')[0];
                var parts = firstLine.Split(' ');
                if (parts.Length < 2) return;
                string path = parts[1];
                int q = path.IndexOf('?');
                if (q < 0) return;
                var qs = HttpUtility.ParseQueryString(path.Substring(q + 1));
                code = qs["code"];
                state = qs["state"];
            }
            catch { /* leave nulls */ }
        }

        private static void WriteHtml(TcpClient client, string message)
        {
            try
            {
                string html = $"<!doctype html><meta charset=\"utf-8\"><body style=\"font-family:sans-serif;text-align:center;margin-top:60px\"><h2>TNovPRO · Вопросы</h2><p>{message}</p></body>";
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                string headers = "HTTP/1.1 200 OK\r\n" +
                                 "Content-Type: text/html; charset=utf-8\r\n" +
                                 $"Content-Length: {bytes.Length}\r\n" +
                                 "Connection: close\r\n\r\n";
                var stream = client.GetStream();
                var head = Encoding.ASCII.GetBytes(headers);
                stream.Write(head, 0, head.Length);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch { /* ignore */ }
        }
    }
}
