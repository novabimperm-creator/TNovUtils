using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using TNovCommon;
using TNovUtils.Checklist.Revit;
using TNovUtils.Checklist.UI;

namespace TNovUtils.Checklist.Checks
{
    public sealed class LevelNumberCheck : ObservableObject, ICheck
    {
        public const string CheckId = "level-number";
        public const string DisplayTitle = Report.ChecklistCatalog.LevelNumberTitle;
        public const string ResultTitle = "Параметр N_Эт.Номер заполнен и соответствует уровню";

        private readonly AutoCheckStore _store;

        public string Id => CheckId;
        public string Title => DisplayTitle;
        public int Number => AutoCheckStore.LevelNumberNumber;

        private CheckStatus _status = CheckStatus.Outdated;
        public CheckStatus Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => CheckStatusRules.Text(Status);

        private DateTime? _lastRunAt;
        public DateTime? LastRunAt { get => _lastRunAt; private set { SetProperty(ref _lastRunAt, value); OnPropertyChanged(nameof(DisplayDate)); } }

        private string _lastRunBy;
        public string LastRunBy { get => _lastRunBy; private set => SetProperty(ref _lastRunBy, value); }

        public string DisplayDate => LastRunAt.HasValue ? LastRunAt.Value.ToString("dd.MM HH:mm") : "—";

        public LevelNumberCheck(AutoCheckStore store)
        {
            _store = store;
            _store.Changed += (s, e) => Reload();
            Reload();
        }

        public UserControl CreateView() => new AutoCheckDetailControl(
            _store,
            AutoCheckStore.LevelNumberNumber,
            DisplayTitle,
            ResultTitle,
            LevelNumberChecker.Run);

        public CheckRunResult Run(Document doc) => LevelNumberChecker.Run(doc);

        public void Reload()
        {
            var item = _store.Get(AutoCheckStore.LevelNumberNumber);
            Status = CheckStatusRules.FromItem(item);
            LastRunBy = item?.Creator;
            LastRunAt = item != null && !string.IsNullOrWhiteSpace(item.Title)
                ? item.CreationDate
                : (DateTime?)null;
        }
    }

    /// <summary>
    /// Элементы категорий плагина Эт.Номер (кроме балок и отверстий — их заполняют КР/BIM):
    /// N_Эт.Номер отсутствует, пуст или не совпадает с уровнем, который определил бы плагин.
    /// Уровень и номер (монолитные перекрытия -1, без 0) определяются тем же LevelResolver
    /// с настройками проекта из JSON плагина. Значение 0 всегда ошибка. Модель не меняет.
    /// </summary>
    public static class LevelNumberChecker
    {
        private const int MaxLogLines = 200; //на группу; полный список Id — всегда

        public static CheckRunResult Run(Document doc)
        {
            var resolver = new LevelResolver(doc, LevelNumberSettings.Load(doc));

            var noParam = new List<Element>();
            var empty = new List<(Element Elem, string Info)>();
            var wrong = new List<(Element Elem, string Info)>();
            var unresolved = new List<(Element Elem, string Info)>();
            var byGeometry = new List<(Element Elem, string Info)>();

            foreach (var elem in LevelNumberElements.Collect(doc))
            {
                string kind = LevelNumberElements.GetKind(elem);
                if (kind == "Default" || kind == "FamilyInstance_Beam" || kind == "FamilyInstance_Hole") continue;

                Parameter p = elem.get_Parameter(LevelNumberParam.Guid);
                if (p == null) { noParam.Add(elem); continue; }
                if (LevelNumberParam.IsDrivenByParent(elem, p)) continue; //значение из родительского семейства

                LevelResolveResult resolved = resolver.Resolve(elem);
                if (!resolved.Number.HasValue) { unresolved.Add((elem, resolved.Info)); continue; }

                double expected = resolved.Number.Value;
                if (!p.HasValue) { empty.Add((elem, "ожидается " + Format(expected) + " (" + resolved.Info + ")")); continue; }

                double actual = LevelNumberParam.Decode(p.AsDouble());
                if (Math.Abs(actual - expected) > 0.001)
                    wrong.Add((elem, "указано " + Format(actual) + ", ожидается " + Format(expected) + " (" + resolved.Info + ")"));
                else if (resolved.ByGeometry)
                    byGeometry.Add((elem, resolved.Info));
            }

            var log = new StringBuilder();
            var ids = new List<string>();

            if (noParam.Count > 0)
            {
                log.AppendLine();
                log.AppendLine($"Нет параметра N_Эт.Номер: {noParam.Count}");
                foreach (var g in noParam.GroupBy(e => e.Category?.Name ?? "Без категории").OrderBy(g => g.Key, StringComparer.Ordinal))
                    log.AppendLine($"  {g.Key}: {g.Count()}");
                AppendIds(log, ids, noParam);
            }
            AppendGroup(log, ids, "N_Эт.Номер не заполнен", empty);
            AppendGroup(log, ids, "N_Эт.Номер не соответствует уровню", wrong);
            AppendGroup(log, ids, "Номер этажа не определяется (нет уровня и геометрии или имя уровня без кода)", unresolved);

            // Значение верное, но уровень получен по отметке — модель стоит поправить; на результат не влияет
            if (byGeometry.Count > 0)
            {
                log.AppendLine();
                log.AppendLine($"Для справки — значение верное, но уровень определен по отметке элемента (проверьте привязку): {byGeometry.Count}");
                AppendLines(log, byGeometry);
                log.AppendLine("Id: " + string.Join(", ", byGeometry.Select(x => ElementIds.ToStringValue(x.Elem.Id))));
            }

            return new CheckRunResult
            {
                Title = LevelNumberCheck.ResultTitle,
                Passed = ids.Count == 0,
                ElemIds = ids.Count > 0 ? string.Join(", ", ids) : "",
                Log = log.ToString()
            };
        }

        private static void AppendGroup(StringBuilder log, List<string> ids, string header, List<(Element Elem, string Info)> items)
        {
            if (items.Count == 0) return;
            log.AppendLine();
            log.AppendLine($"{header}: {items.Count}");
            AppendLines(log, items);
            AppendIds(log, ids, items.Select(x => x.Elem));
        }

        private static void AppendLines(StringBuilder log, List<(Element Elem, string Info)> items)
        {
            foreach (var x in items.Take(MaxLogLines))
                log.AppendLine($"  {ElementIds.ToStringValue(x.Elem.Id)} {x.Elem.Category?.Name}: {x.Elem.Name} — {x.Info}");
            if (items.Count > MaxLogLines)
                log.AppendLine($"  … и ещё {items.Count - MaxLogLines}");
        }

        private static void AppendIds(StringBuilder log, List<string> ids, IEnumerable<Element> elems)
        {
            var groupIds = elems.Select(e => ElementIds.ToStringValue(e.Id)).ToList();
            log.AppendLine("Id: " + string.Join(", ", groupIds));
            ids.AddRange(groupIds);
        }

        private static string Format(double number) => number.ToString("0.##");
    }
}
