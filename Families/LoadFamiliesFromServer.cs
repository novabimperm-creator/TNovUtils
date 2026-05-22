using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;

namespace TNovUtils
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LoadFamiliesFromServer : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Устанавливаем контекст проекта
            string rawName = string.IsNullOrEmpty(doc.PathName)
                ? "Новый проект"
                : (doc.PathName.StartsWith("Revit Server:")
                    ? new Func<string>(() => {
                        var parts = doc.PathName.Split('|');
                        return parts.Length > 1 ? Path.GetFileNameWithoutExtension(parts[1]) : "Revit Server проект";
                    })()
                    : Path.GetFileNameWithoutExtension(doc.PathName));

            var projectNames = ProjectListLoader.LoadProjectNames();
            string foundProject = ProjectListLoader.FindProjectInPath(doc.PathName, projectNames);

            RevitContext.CurrentProjectPath = doc.PathName;
            RevitContext.CurrentProjectDisplayName = RevitContext.CleanProjectDisplayName(rawName);
            RevitContext.CurrentProjectNameFromCde = foundProject;

            TaskDialog choiceDialog = new TaskDialog("Выбор действия");
            choiceDialog.TitleAutoPrefix = false;
            choiceDialog.MainInstruction = "Что вы хотите сделать?";
            choiceDialog.MainContent = "Выберите нужное действие:";
            choiceDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Открыть библиотеку семейств");
            choiceDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Создать заявку на семейство");
            choiceDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Просмотр заявок");
            choiceDialog.CommonButtons = TaskDialogCommonButtons.None;
            choiceDialog.DefaultButton = TaskDialogResult.CommandLink1;

            TaskDialogResult result = choiceDialog.Show();

            if (result == TaskDialogResult.CommandLink1)
            {
                string libraryPath = @"\\fs-nova\NOVA\04_БИБЛИОТЕКА\BIM";
                if (!Directory.Exists(libraryPath))
                {
                    new InfoWindow400($"Папка не найдена: {libraryPath}\nПроверьте доступность сетевого диска Z:").ShowDialog();
                    return Result.Failed;
                }

                // Всегда загружаем из кэша, даже если он пустой
                List<FamilyInfo> families = GetCachedFamilies(libraryPath);

                // Окно открывается в любом случае – пользователь сможет обновить кэш
                var window = new FamilyBrowserWindow(families, libraryPath);
                bool? dialogResult = window.ShowDialog();

                if (dialogResult == true && window.SelectedFamily != null)
                {
                    string familyPath = window.SelectedFamily.FullPath;
                    try
                    {
                        using (Transaction tx = new Transaction(doc, "Загрузка семейства"))
                        {
                            tx.Start();
                            Family family = null;
                            if (doc.LoadFamily(familyPath, out family))
                            {
                                new InfoWindow400($"Семейство '{window.SelectedFamily.Name}' загружено.").ShowDialog();
                            }
                            else
                            {
                                new InfoWindow400("Не удалось загрузить семейство.").ShowDialog();
                            }
                            tx.Commit();
                        }
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                        return Result.Failed;
                    }
                }
            }
            else if (result == TaskDialogResult.CommandLink2)
            {
                var requestWindow = new FamilyRequestWindow(
                    RevitContext.CurrentProjectPath,
                    RevitContext.CurrentProjectDisplayName);
                requestWindow.ShowDialog();
            }
            else if (result == TaskDialogResult.CommandLink3)
            {
                var requestsWindow = new RequestsListWindow();
                requestsWindow.ShowDialog();
            }

            return Result.Succeeded;
        }

        // Загружает кэшированные семейства (не сканируя диск)
        private List<FamilyInfo> GetCachedFamilies(string rootPath)
        {
            var cache = FamilyCacheManager.LoadCache();
            var result = new List<FamilyInfo>();
            foreach (var kvp in cache)
            {
                var item = kvp.Value;
                result.Add(new FamilyInfo
                {
                    FullPath = item.FullPath,
                    Name = item.Name,
                    Version = item.VersionString,
                    LastModified = item.LastModified,
                    Category = item.Category,
                    VersionNumber = item.VersionNumber
                });
            }
            return result;
        }
    }
}