using System;
using System.Collections.Generic;
using System.Linq;

namespace TNovUtils.Issues.Api
{
    /// <summary>
    /// Справочник «id сотрудника → ФИО» на процесс.
    ///
    /// 🔴 Зачем статический. Список вопросов рисуется байндингом ПРЯМО НА DTO
    /// (<see cref="Issue"/>), а в DTO приходит только `author` — идентификатор
    /// вида `user-1770893578590`. Колонка «Автор» была привязана к нему и
    /// показывала этот id вместо имени (жалоба Виктора 2026-08-31). ФИО в
    /// карточке и в фильтрах подставлялось руками, в списке подставлять было
    /// некому: строка о каталоге сотрудников не знает.
    ///
    /// Каталог заполняется один раз при загрузке окна (см. IssuesWindow), до
    /// первой отрисовки списка. Не загрузился — <see cref="NameOf"/> честно
    /// вернёт id, как было: выдумывать имя нельзя.
    /// </summary>
    public static class PeopleDirectory
    {
        private static Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Заполнить каталог (замена целиком).</summary>
        public static void Set(IEnumerable<DirectoryUser> users)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var u in users ?? Enumerable.Empty<DirectoryUser>())
            {
                if (u == null || string.IsNullOrEmpty(u.Id)) continue;
                // Индексатор, а НЕ ToDictionary: один повторившийся id уронил бы
                // построение всего справочника, и имён не было бы ни у кого.
                map[u.Id] = u.DisplayName;
            }
            _names = map;
        }

        /// <summary>ФИО по id; каталога нет или человека в нём нет — сам id.</summary>
        public static string NameOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "—";
            return _names.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : id;
        }
    }
}
