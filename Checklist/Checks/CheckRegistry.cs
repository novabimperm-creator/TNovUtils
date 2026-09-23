using System;
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
        public const string BimChecksId = "bim-checks";
        public const string BimChecksTitle = "BIM-проверки";

        public IReadOnlyList<ICheck> Checks { get; }

        public CheckRegistry(AutoCheckStore store, Document doc)
        {
            var checks = new List<ICheck>
            {
                new GridsLevelsLinksCheck(store),
                new RfCoordinationCheck(store)
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
            if (ModelNameRules.IsVkOvModel(doc))
                checks.Add(new AdskPostcheckCheck(store));
            Checks = checks;
        }

        public ICheck Find(string id) => Checks.FirstOrDefault(c => c.Id == id);

        /// <summary>
        /// Автопроверки, применимые к модели, по её имени — те же правила, что в конструкторе.
        /// Нужно отчёту: он читает JSON моделей, не открывая документы.
        /// </summary>
        public static IReadOnlyList<(int Number, string Title)> ApplicableAutoChecks(string modelName) =>
            ApplicableAutoCheckRunners(modelName).Select(c => (c.Number, c.Title)).ToList();

        /// <summary>
        /// То же, что ApplicableAutoChecks, но с логикой проверки — для пакетного запуска без UI (TNovAuto).
        /// </summary>
        public static IReadOnlyList<(int Number, string Title, Func<Document, CheckRunResult> Run)> ApplicableAutoCheckRunners(string modelName)
        {
            var list = new List<(int Number, string Title, Func<Document, CheckRunResult> Run)>
            {
                (AutoCheckStore.GridsLevelsLinksNumber, GridsLevelsLinksCheck.DisplayTitle, GridsLevelsLinksChecker.Run),
                // Без UI набор РФ не открыть — только сообщаем
                (AutoCheckStore.RfCoordinationNumber, RfCoordinationCheck.DisplayTitle, d => RfCoordinationChecker.Run(d, allowUi: false))
            };
            if (ModelNameRules.IsArOrPof(modelName))
                list.Add((AutoCheckStore.AntiMirrorNumber, AntiMirrorCheck.DisplayTitle, AntiMirrorChecker.Run));
            if (ModelNameRules.IsRebarNoMarkModel(modelName))
                list.Add((AutoCheckStore.RebarNoMarkNumber, RebarNoMarkCheck.DisplayTitle, RebarNoMarkChecker.Run));
            if (ModelNameRules.IsNoPartsModel(modelName))
                list.Add((AutoCheckStore.NoPartsNumber, NoPartsCheck.DisplayTitle, NoPartsChecker.Run));
            if (ModelNameRules.IsArModel(modelName))
            {
                list.Add((AutoCheckStore.LintelsNoMarkNumber, LintelsNoMarkCheck.DisplayTitle, LintelsNoMarkChecker.Run));
                list.Add((AutoCheckStore.EvacuationRoutesNumber, EvacuationRoutesCheck.DisplayTitle, EvacuationRoutesChecker.Run));
                list.Add((AutoCheckStore.UnplacedRoomsNumber, UnplacedRoomsCheck.DisplayTitle, UnplacedRoomsChecker.Run));
                list.Add((AutoCheckStore.RoomDepartmentNumber, RoomDepartmentCheck.DisplayTitle, RoomDepartmentChecker.Run));
            }
            if (ModelNameRules.IsVkOvModel(modelName))
                list.Add((AutoCheckStore.AdskPostcheckNumber, AdskPostcheckCheck.DisplayTitle, AdskPostcheckChecker.Run));
            return list;
        }
    }
}
