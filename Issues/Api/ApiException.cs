using System;
using System.Net;

namespace TNovUtils.Issues.Api
{
    /// <summary>Базовая ошибка REST-вызова (несоответствие 2xx).</summary>
    public class ApiException : Exception
    {
        public HttpStatusCode StatusCode { get; }
        public string Code { get; }       // машинный код из тела { code: ... }, если есть
        public string RawBody { get; }

        public ApiException(HttpStatusCode status, string message, string code = null, string rawBody = null)
            : base(message)
        {
            StatusCode = status;
            Code = code;
            RawBody = rawBody;
        }
    }

    /// <summary>401 — требуется (повторная) аутентификация.</summary>
    public sealed class AuthRequiredException : ApiException
    {
        public AuthRequiredException(string message = "Authentication required")
            : base(HttpStatusCode.Unauthorized, message) { }
    }

    /// <summary>409 — устаревший updatedAt. В Current — актуальное состояние issue.</summary>
    public sealed class ConflictException : ApiException
    {
        public Issue Current { get; }
        public ConflictException(Issue current, string rawBody)
            : base(HttpStatusCode.Conflict, "Замечание было изменено с момента открытия", "CONFLICT", rawBody)
        {
            Current = current;
        }
    }

    /// <summary>400 PROJECT_UNRESOLVED — проект не определился, нужен ручной выбор.</summary>
    public sealed class ProjectUnresolvedException : ApiException
    {
        public ProjectUnresolvedException(string rawBody)
            : base(HttpStatusCode.BadRequest, "Проект не определён по модели — выберите вручную", "PROJECT_UNRESOLVED", rawBody) { }
    }
}
