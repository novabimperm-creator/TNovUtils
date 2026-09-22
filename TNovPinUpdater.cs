using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.Attributes;
using TNovCommon;
using Newtonsoft.Json;

namespace TNovUtils
{
    [Transaction(TransactionMode.Manual)]
    public class TNovPinUpdater : IUpdater
    {
        private const string UpdaterName = "TNovPinUpdater";

        static AddInId _appId;
        static UpdaterId _updaterId;

        public TNovPinUpdater(AddInId id)
        {
            _appId = id;

            _updaterId = new UpdaterId(_appId, new Guid(
                                                   "e8e6a0c4-afd7-4d94-b2c1-0585fbefea1f"));
        }

        /// <summary>
        /// Точка входа Revit. Наружу не должно вылетать ни одного исключения:
        /// любое исключение из IUpdater.Execute Revit показывает пользователю
        /// с предложением отключить обновитель.
        /// </summary>
        public void Execute(UpdaterData data)
        {
            try
            {
                ExecuteCore(data);
            }
            catch (Exception ex)
            {
                UpdaterDiagnostics.Report(UpdaterName, "Execute", ex);
            }
        }

        private void ExecuteCore(UpdaterData data)
        {
            if (data == null) return;

            Document doc = data.GetDocument();
            if (doc == null) return;

            ICollection<ElementId> idsA = data.GetAddedElementIds();
            if (idsA == null) return;

            foreach (ElementId id in idsA)
            {
                try
                {
                    Element elem = doc.GetElement(id);
                    if (elem != null && !elem.Pinned) elem.Pinned = true;
                }
                catch { }
            }
        }

        public string GetAdditionalInformation()
        {
            return "TNov, bim@pm-nova.ru";
        }

        public ChangePriority GetChangePriority()
        {
            return ChangePriority.FloorsRoofsStructuralWalls;
        }

        public UpdaterId GetUpdaterId()
        {
            return _updaterId;
        }

        public string GetUpdaterName()
        {
            return UpdaterName;
        }
    }
}
