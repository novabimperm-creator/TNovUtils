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
                        #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Семейный";
            //подключение приложения и документа
            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }
            UIDocument uidoc = RevitAPI.UiDocument; Document doc = RevitAPI.Document;
            UIApplication uiapp = RevitAPI.UiApplication; Autodesk.Revit.ApplicationServices.Application rvtApp = uiApp.Application;
            string docName = doc.Title.ToString(); docName = docName.Replace(",", " ");
            string userName = rvtApp.Username; userName = userName.Replace(",", "");
            string docNameUserName = "_" + userName; docName = docName.Replace(docNameUserName, "");
            docName = docName.Replace(",", "");
            #endregion

            TNovConfig config = TNovConfigLoad.LoadConfig(DBCommandName, TNovVersion);

            #region Настройки логов
            // создание log - файла
            Logger.Initialize(DBCommandName, dateTime, TNovVersion);

            var viewModel0 = new AppVersionViewModel();

            string jsonpath0 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TNovClient/TNovSettings.json");
            viewModel0 = JsonConvert.DeserializeObject<AppVersionViewModel>(File.ReadAllText(jsonpath0));
            if (viewModel0.extendedLogs)

            {
                var qViewModel = new QuestionWindowViewModel();
                qViewModel.headtxt = "Включены расширенные логи. " +
                    "Плагин будет работать медленнее, но соберет больше данных. " +
                    "Выключить расширенные логи для ускорения работы?";
                var qwpfview = new QuestionWindow280(qViewModel);
                qViewModel.CloseRequest += (s, e) => qwpfview.Close();
                bool? qok = qwpfview.ShowDialog();
                if (qok != null && qok == true) { Logger.TurnOffExtendedLogs(); } else Logger.Log("Расширенные логи вкл", 2);
            }
            #endregion

            #region Контекст проекта
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

            #endregion

#region Диалог

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

            #endregion

#region Открытие окон

            if (result == TaskDialogResult.CommandLink1)
            {
                Logger.Log("Сценарий 1 - библиотека семейств", 1);

string libraryPath = @"\\fs-nova\NOVA\04_БИБЛИОТЕКА\BIM";
                if (!Directory.Exists(libraryPath))
                {
                    new InfoWindow400($"Папка не найдена: {libraryPath}\nПроверьте доступность сетевого диска.").ShowDialog();
Logger.Log($"Папка не найдена: {libraryPath}\nПроверьте доступность сетевого диска",4);
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
Logger.Log($"Выбрано семейство {familyPath}", 1);
                    try
                    {
                        using (Transaction tx = new Transaction(doc, "Загрузка семейства"))
                        {
                            tx.Start();
                            Family family = null;
                            if (doc.LoadFamily(familyPath, out family))
                            {
                                new InfoWindow400($"Семейство '{window.SelectedFamily.Name}' загружено.").ShowDialog();
Logger.Log($"Семейство '{window.SelectedFamily.Name}' загружено.", 1);
                            }
                            else
                            {
                                new InfoWindow400("Не удалось загрузить семейство.").ShowDialog();
Logger.Log("Не удалось загрузить семейство.",4);
                            }
                            tx.Commit();
                        }
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
															Logger.Log($"Ошибка: {ex.Message},4);
                        return Result.Failed;
                    }
                }
            }
            else if (result == TaskDialogResult.CommandLink2)
            {
                Logger.Log("Сценарий 2 - заявка на семейство", 1);
var requestWindow = new FamilyRequestWindow(
                    RevitContext.CurrentProjectPath,
                    RevitContext.CurrentProjectDisplayName);
                requestWindow.ShowDialog();
            }
            else if (result == TaskDialogResult.CommandLink3)
            {
                Logger.Log("Сценарий 3 - журнал заявок", 1);
var requestsWindow = new RequestsListWindow();
                requestsWindow.ShowDialog();
            }

            #endregion
Logger.Log("Завершение работы",5);
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