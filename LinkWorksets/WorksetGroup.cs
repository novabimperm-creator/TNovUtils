using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace TNovUtils.LinkWorksets
{
    /// <summary>
    /// Группа рабочих наборов связи, опознаваемая по имени: «оси и уровни», «арматура».
    /// API не даёт узнать, что лежит в наборе, не открыв его, поэтому единственный признак —
    /// имя, которое дал автор связанного файла.
    /// </summary>
    public sealed class WorksetGroup
    {
        private WorksetGroup(string name, params string[] keywords)
        {
            Name = name;
            Keywords = new ReadOnlyCollection<string>(keywords.ToList());
        }

        public string Name { get; }

        public IList<string> Keywords { get; }

        public bool Matches(string worksetName)
        {
            if (string.IsNullOrEmpty(worksetName)) return false;

            string lower = worksetName.ToLowerInvariant();
            return Keywords.Any(k => lower.Contains(k));
        }

        public override string ToString() => Name;

        /// <summary>
        /// Оси и уровни. Штатный набор Revit встречается и как «Общие уровни и оси», и как
        /// «Общие слои и сетки» — в одном проекте могут соседствовать оба варианта.
        ///
        /// «сетк» отдельным словом сюда не годится: арматурные наборы сплошь и рядом зовут
        /// «Сетки арматурные», и по голому «сетк» арматура попала бы в оси. Поэтому «сетки»
        /// ловятся только в связке со «слоями» и «уровнями».
        /// </summary>
        public static readonly WorksetGroup GridsAndLevels = new WorksetGroup(
            "Оси и уровни",
            "оси", "ось", "уровн", "слои и сетки", "уровни и сетки", "grid", "level", "datum");

        public static readonly WorksetGroup Rebar = new WorksetGroup(
            "Арматура",
            "армат", "армир", "rebar", "reinforc");
    }
}
