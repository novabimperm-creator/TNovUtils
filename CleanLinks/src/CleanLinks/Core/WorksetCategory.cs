using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CleanLinks.Core
{
    /// <summary>
    /// Группа рабочих наборов, опознаваемая по имени: «оси и уровни», «арматура» и так далее.
    /// API не даёт узнать, что лежит в наборе, не открыв его, поэтому единственный признак —
    /// имя, которое дал автор связанного файла.
    /// </summary>
    public class WorksetCategory
    {
        public WorksetCategory(string name, string hint, params string[] keywords)
        {
            Name = name;
            Hint = hint;
            Keywords = new ReadOnlyCollection<string>(keywords.ToList());
        }

        public string Name { get; }

        /// <summary>Пояснение для окна выбора.</summary>
        public string Hint { get; }

        public IList<string> Keywords { get; }

        public bool Matches(string worksetName)
        {
            if (string.IsNullOrEmpty(worksetName)) return false;

            string lower = worksetName.ToLowerInvariant();
            return Keywords.Any(k => lower.Contains(k));
        }

        public override string ToString() => Name;
    }

    public static class WorksetCategories
    {
        /// <summary>
        /// Оси и уровни. Штатный набор Revit встречается и как «Общие уровни и оси», и как
        /// «Общие слои и сетки» — в одном проекте могут соседствовать оба варианта.
        ///
        /// «сетк» отдельным словом сюда не годится: арматурные наборы сплошь и рядом зовут
        /// «Сетки арматурные», и по голому «сетк» арматура попала бы в оси. Поэтому «сетки»
        /// ловятся только в связке со «слоями» и «уровнями».
        /// </summary>
        public static readonly WorksetCategory Grids = new WorksetCategory(
            "Оси и уровни",
            "«Общие уровни и оси», «Общие слои и сетки» и подобные",
            "оси", "ось", "уровн", "слои и сетки", "уровни и сетки", "grid", "level", "datum");

        public static readonly WorksetCategory Rebar = new WorksetCategory(
            "Арматура",
            "«Армирование», «Арматура», «Сетки арматурные» и подобные",
            "армат", "армир", "rebar", "reinforc");

        public static readonly IList<WorksetCategory> All =
            new ReadOnlyCollection<WorksetCategory>(new[] { Grids, Rebar });
    }
}
