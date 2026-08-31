using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Реестр пунктов сайдбара. «Свод» — отдельный id, проверки добавляются сюда.
    /// </summary>
    public sealed class CheckRegistry
    {
        public const string SummaryId = "summary";
        public const string SummaryTitle = "Свод";

        public IReadOnlyList<ICheck> Checks { get; }

        public CheckRegistry(AutoCheckStore store, Document doc)
        {
            var checks = new List<ICheck>
            {
                new GridsLevelsLinksCheck(store)
            };
            if (ModelNameRules.IsArOrPof(doc))
                checks.Add(new AntiMirrorCheck(store));
            if (ModelNameRules.IsRebarNoMarkModel(doc))
                checks.Add(new RebarNoMarkCheck(store));
            if (ModelNameRules.IsNoPartsModel(doc))
                checks.Add(new NoPartsCheck(store));
            if (ModelNameRules.IsArModel(doc))
            {
                checks.Add(new LintelsNoMarkCheck(store));
                checks.Add(new EvacuationRoutesCheck(store));
                checks.Add(new UnplacedRoomsCheck(store));
                checks.Add(new RoomDepartmentCheck(store));
            }
            Checks = checks;
        }

        public ICheck Find(string id) => Checks.FirstOrDefault(c => c.Id == id);
    }
}
