using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovUtils.Issues.Revit;

namespace TNovUtils.Issues.Commands
{
    /// <summary>
    /// ЭКСПЕРИМЕНТАЛЬНАЯ команда: выгружает геометрию выделенного элемента (элементов)
    /// и его ближайшего окружения (соседей) в .glb на Рабочий стол. Затем .glb открывается
    /// в браузерном прототипе (experiments/3d-viewer) — проверка концепции «3D-просмотр
    /// замечания в браузере» на реальном проекте. Создание замечаний не затрагивается.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class ExportGlbCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("TNovPRO · Экспорт 3D", "Нет открытой модели.");
                return Result.Cancelled;
            }

            var ids = uidoc.Selection.GetElementIds()
#if R2027
                .Select(id => id.Value) // Revit 2024+: 64-битный ElementId.Value
#else
                .Select(id => (long)id.IntegerValue)
#endif
                .ToList();

            if (ids.Count == 0)
            {
                TaskDialog.Show("TNovPRO · Экспорт 3D",
                    "Выделите элемент замечания и повторите.\n\n" +
                    "Выделенные элементы → целевые (красный), их ближайшее окружение → соседи (серый).");
                return Result.Cancelled;
            }

            try
            {
                var result = GeometryExporter.Export(uidoc.Document, ids);

                var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var path = Path.Combine(dir, $"tnovpro-3d-{ids[0]}.glb");
                File.WriteAllBytes(path, result.Glb);

                var sizeKb = result.Glb.Length / 1024.0;
                var td = new TaskDialog("TNovPRO · Экспорт 3D")
                {
                    MainInstruction = "Фрагмент выгружен в .glb",
                    MainContent =
                        $"Треугольников: {result.TriangleCount:N0}\n" +
                        $"Соседей: {result.NeighborCount:N0}{(result.Truncated ? " (обрезано до лимита)" : "")}\n" +
                        $"Размер: {sizeKb:N0} КБ\n\n" +
                        $"Файл: {path}\n\n" +
                        "Откройте прототип вьюера в браузере и нажмите «Загрузить .glb».",
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Показать файл в папке");
                if (td.Show() == TaskDialogResult.CommandLink1)
                {
                    try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { /* не критично */ }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("TNovPRO · Экспорт 3D", "Не удалось выгрузить геометрию: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}
