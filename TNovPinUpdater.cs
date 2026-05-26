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
        static AddInId _appId;
        static UpdaterId _updaterId;

        public TNovPinUpdater(AddInId id)
        {
            _appId = id;

            _updaterId = new UpdaterId(_appId, new Guid(
                                                   "e8e6a0c4-afd7-4d94-b2c1-0585fbefea1f"));
        }

        public void Execute(UpdaterData data)
        {
            Document doc = data.GetDocument();
            Autodesk.Revit.ApplicationServices.Application app = doc.Application;

            //параметры
            ElementId familyNameParamId = new ElementId(-1002002); //id параметра Имя семейства

            
            List<ElementId> idsA = data.GetAddedElementIds().ToList();

            foreach (ElementId id in idsA)
            {
                try
                {
                    Element elem = doc.GetElement(id); elem.Pinned = true;
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
            return "TNovPinUpdater";
        }
    }
}
