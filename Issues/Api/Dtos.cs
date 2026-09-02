using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace TNovUtils.Issues.Api
{
    // Контракт v2 — docs/API.md §0.1 (camelCase, под ТЗ). Даты держим строками
    // и эхо-им обратно (updatedAt — версия для оптимистичной блокировки).

    /// <summary>Слот модели вопроса: modelId + элемент Revit (Id1 для слота 0, Id2 для слота 1).</summary>
    public sealed class IssueModelRef
    {
        [JsonProperty("modelId")] public string ModelId { get; set; }
        [JsonProperty("elementId")] public long? ElementId { get; set; }

        /// <summary>
        /// Имя модели. Его резолвит СЕРВЕР (issues.js, rowToPublic → models[].name) —
        /// клиентский кэш «id → имя», снятый один раз при открытии окна, не знает
        /// моделей, заведённых позже, и подставляет чужое имя при верных данных в базе.
        /// В запросах не шлём: сервер принимает только modelId/elementId.
        /// </summary>
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }
    }

    public sealed class PhotoMeta
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Mime { get; set; }
        public string Url { get; set; }
        public string ThumbUrl { get; set; }
    }

    public sealed class Issue
    {
        public string Id { get; set; }
        public long Gnum { get; set; }
        public int Number { get; set; }
        public string ProjectId { get; set; }
        public List<string> ModelIds { get; set; } = new List<string>();
        public List<IssueModelRef> Models { get; set; } = new List<IssueModelRef>(); // 1–2 слота
        public string Status { get; set; }      // new|in_progress|completed|closed
        public string Description { get; set; }  // «Содержание вопроса»
        public string Author { get; set; }
        public List<string> Assignees { get; set; } = new List<string>();
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }

        // только в списке
        public int PhotoCount { get; set; }
        public string ThumbUrl { get; set; }

        // только в детали
        public List<PhotoMeta> Photos { get; set; } = new List<PhotoMeta>();

        [JsonIgnore] public string StatusRu => StatusLabels.Ru(Status);

        // ФИО для колонок списка: строка байндится на DTO и до каталога
        // сотрудников не дотягивается сама — берём его из PeopleDirectory.
        [JsonIgnore] public string AuthorName => PeopleDirectory.NameOf(Author);
        [JsonIgnore]
        public string AssigneesText => (Assignees == null || Assignees.Count == 0)
            ? "—"
            : string.Join(", ", Assignees.Select(PeopleDirectory.NameOf));
    }

    public static class StatusLabels
    {
        public static string Ru(string s)
        {
            switch (s)
            {
                case "new": return "Новое";
                case "in_progress": return "В работе";
                case "completed": return "Выполнено";
                case "closed": return "Закрыто";
                default: return s;
            }
        }
    }

    public sealed class Project
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> Prefixes { get; set; } = new List<string>();
        public bool IsArchived { get; set; }
        public string CreatedAt { get; set; }
    }

    public sealed class Model
    {
        public string Id { get; set; }
        public string ProjectId { get; set; }
        public string Name { get; set; }
        public string CreatedAt { get; set; }
        public bool ProjectResolved { get; set; }
    }

    /// <summary>Пользователь платформы для пикера ответственных (GET /api/users, без секретов).</summary>
    public sealed class DirectoryUser
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Position { get; set; }
        public string Department { get; set; }
        public bool IsBlocked { get; set; }
        public bool IsDeleted { get; set; }

        /// <summary>ФИО, иначе username, иначе id — для показа в списке.</summary>
        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                var n = ($"{FirstName} {LastName}").Trim();
                return !string.IsNullOrEmpty(n) ? n : (!string.IsNullOrEmpty(Username) ? Username : Id);
            }
        }

        [JsonIgnore]
        public string Subtitle
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(Position)) parts.Add(Position);
                if (!string.IsNullOrEmpty(Department)) parts.Add(Department);
                if (!string.IsNullOrEmpty(Username)) parts.Add("@" + Username);
                return string.Join(" · ", parts);
            }
        }
    }

    public sealed class IssuesListResponse { public List<Issue> Issues { get; set; } = new List<Issue>(); }
    public sealed class ProjectsListResponse { public List<Project> Projects { get; set; } = new List<Project>(); }
    public sealed class ModelsListResponse { public List<Model> Models { get; set; } = new List<Model>(); }

    // Ответ desktop-auth/exchange (а также формат токенов из веб-логина).
    public sealed class TokenResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public UserInfo User { get; set; }
    }
    public sealed class RefreshResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
    }
    public sealed class UserInfo
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string Role { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
    }
}
