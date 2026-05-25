using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Shapes;
using System.Windows.Threading;
using TNovCommon;
using static System.Windows.Forms.LinkLabel;
using Path = System.IO.Path;

namespace TNovUtils
{
    [Transaction(TransactionMode.Manual)]
    public class Links : IExternalCommand
    {
        private TNovProgressBar bimExportProgressBar;
        private void ThreadStartingPoint()
        {
            this.bimExportProgressBar = new TNovProgressBar();
            this.bimExportProgressBar.Show();
            Dispatcher.Run();
        }
        private static IEnumerable<Node> GetAllNodes(ObservableCollection<Node> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;

                foreach (var child in GetAllNodes(node.Children))
                {
                    yield return child;
                }
            }
        }
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Связной";
            //подключение приложения и документа
            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }
            UIDocument uidoc = RevitAPI.UiDocument; Document doc = RevitAPI.Document;
            UIApplication uiApp = RevitAPI.UiApplication; Autodesk.Revit.ApplicationServices.Application rvtApp = uiApp.Application;
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

            Logger.Log("Сбор элементов", 1);

            List<RevitLinkInstance> links0 = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RvtLinks)      //фильтр по категории Связи
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .Cast<RevitLinkInstance>()         //элементы категории Связи
                                                                         .ToList();                         //формируем список

            List<string> linksString = new List<string>();
            if (links0 == null || links0.Count == 0) linksString.Add("-----");
            else
            {
                Logger.Log("Существующие связи: ", 2);
                foreach (var link in links0)
                {
                    string[] nameparts = link.Name.Split(new char[] { ':' });
                    linksString.Add(nameparts[0]);
                    Logger.Log("   " + nameparts[0], 2);
                }
            }
                

            Logger.Log("Элементы собраны. Создаем списки для работы, проверяем, является ли модель файлом хранилища", 1);
            bool dws = doc.IsWorkshared; if (!dws) Logger.Log("Документ не является ФХ", 2);

            #region Диалог
            Logger.Log("Диалоговое окно", 1);
            RevitServerViewModel viewModel = new RevitServerViewModel(linksString);
            var wpfview = new RevitServer(viewModel);
            viewModel.CloseRequest += (s, ea) => wpfview.Close();
            bool? ok = wpfview.ShowDialog();
            if (ok != null && ok == true) { }
            else { Logger.Log("Запуск отменен пользователем. Завершение работы.", 3); return Result.Cancelled; }

#endregion

#region Сбор элементов 
            //собираем список связей на вставку
            Logger.Log("Собираем список связей на вставку", 1);

            List<string> modelPaths = new List<string>();

            List<Node> allNodes = GetAllNodes(viewModel.Nodes).ToList();
            foreach (var node in allNodes)
            {
                if (node.IsChecked && node.IsModel && node.IsLocked==false) 
                {
                    string path = @"RSN:\\" + nova.revitserver + @"\" + node.Path;
                    modelPaths.Add(path);
                    Logger.Log("   " + path, 2);
                }
            }
#endregion

            Thread thread = new Thread(new ThreadStart(this.ThreadStartingPoint));
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Thread.Sleep(100);

            int PBCount = 0;
            this.bimExportProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.bimExportProgressBar.TNov_ProgressBar.Minimum = (double)PBCount));
            this.bimExportProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.bimExportProgressBar.value.Text = PBCount.ToString()));
            this.bimExportProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.bimExportProgressBar.TNov_ProgressBar.Maximum = (double)modelPaths.Count));
            this.bimExportProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.bimExportProgressBar.maxvalue.Text = modelPaths.Count.ToString()));

#region Основной код
            List<string> badLinks = new List<string>();
            //транзакция - вставка связей

            try
            {
                using (Transaction trans = new Transaction(doc, "Связной"))
                {
                    trans.Start(); Logger.Log("Открываем транзакцию", 1);

                    foreach (string modelPath in modelPaths)
                    {
                        Logger.Log(modelPath, 1);

                        string[] parts = modelPath.Split(Path.DirectorySeparatorChar);
                        string fileName = parts[parts.Length-1]; fileName = fileName.Replace(".rvt", "");

                        this.bimExportProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.bimExportProgressBar.info.Text = fileName));


                        if (LinkModel(doc, modelPath) == false) {badLinks.Add(fileName);
Logger.Log($"Ошибка",4);}
                        PBCount++;
                        this.bimExportProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.bimExportProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                        this.bimExportProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.bimExportProgressBar.value.Text = PBCount.ToString()));

                    }

                    trans.Commit(); Logger.Log("Закрываем транзакцию", 1);
                }

                

            }
            catch (Exception ex)
            {
                message = ex.Message;
Logger.Log($"Ошибка: {message}",4);
                return Result.Failed;
            }
finally
                    {
                        CloseProgressBarSafely();
                    }
#endregion
            


            //сообщение об ошибке
            if (badLinks.Count > 0) 
            {
                new InfoWindow400($"Не удалось вставить связи: {String.Join(", ",badLinks)}. Необходимо проверить общие координаты.").ShowDialog();
            }

            Logger.Log("Переходим к назначению наборов. Завершение работы.", 5);

            //назначение наборов
            PLW Command1 = new PLW(); Command1.Execute(commandData, ref message, elements);

            return Result.Succeeded;
        }
private void CloseProgressBarSafely()
        {
            if (bimExportProgressBar != null &&
                bimExportProgressBar.Dispatcher != null &&
                !bimExportProgressBar.Dispatcher.HasShutdownStarted)
            {
                bimExportProgressBar.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (bimExportProgressBar.IsLoaded)
                        bimExportProgressBar.Close();
                    // Завершаем цикл сообщений диспетчера, чтобы поток завершился
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }));
            }
        }
    
        private bool LinkModel(Document doc, string serverPath)
        {
            bool success = false;
            try
            {
                // Создание ModelPath из пути Revit Server
                ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(serverPath);

                // Проверка существования связи (альтернативный способ сравнения путей)
                FilteredElementCollector collector = new FilteredElementCollector(doc);
                ICollection<Element> linkTypes = collector
                    .OfClass(typeof(RevitLinkType))
                    .ToElements();

                bool alreadyLinked = false;
                foreach (Element elem in linkTypes)
                {
                    RevitLinkType linkType1 = elem as RevitLinkType;
                    if (linkType1 == null) continue;

                    ExternalFileReference extRef = linkType1.GetExternalFileReference();
                    if (extRef == null) continue;

                    ModelPath existingPath = extRef.GetAbsolutePath();
                    if (existingPath == null) continue;

                    // Сравнение путей как строк
                    string existingPathString = ModelPathUtils.ConvertModelPathToUserVisiblePath(existingPath);
                    string newPathString = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);

                    if (string.Equals(existingPathString, newPathString, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyLinked = true;
                        break;
                    }
                }

                if (alreadyLinked)
                {
                    new InfoWindow400($"Модель {System.IO.Path.GetFileName(serverPath)} уже связана").ShowDialog(); 
                    Logger.Log($"Модель {System.IO.Path.GetFileName(serverPath)} уже связана", 1);
                    
                }

                RevitLinkOptions options = new RevitLinkOptions(false);
                WorksetConfiguration worksetConfig = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
                options.SetWorksetConfiguration(worksetConfig);
                LinkLoadResult loadResult = RevitLinkType.Create(doc, modelPath, options);
                if (loadResult.LoadResult == LinkLoadResultType.LinkLoaded)
                {
                    ElementId linkTypeId = loadResult.ElementId;
                    RevitLinkType linkType = doc.GetElement(linkTypeId) as RevitLinkType;

                    // Проверяем существующие экземпляры
                    FilteredElementCollector collector1 = new FilteredElementCollector(doc);
                    RevitLinkInstance existingInstance = collector1
                        .OfClass(typeof(RevitLinkInstance))
                        .Cast<RevitLinkInstance>()
                        .FirstOrDefault(inst => inst.GetTypeId() == linkType.Id);
                    if (existingInstance != null) { success = false; Logger.Log("Связь уже существует", 4); }

                    // Создаем новый экземпляр
                    try
                    {
                        ImportPlacement placement = ImportPlacement.Shared;
                        RevitLinkInstance.Create(doc, linkType.Id, placement);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(ex.Message, 4);
                        ICollection<ElementId> linksToDelete = new List<ElementId>();
                        linksToDelete.Add(linkType.Id);
                        doc.Delete(linksToDelete);
                        return false;
                    }
                }

            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                Logger.Log($"Ошибка пути файла: {serverPath}\n{ex.Message}", 4); throw new Exception($"Ошибка пути файла: {serverPath}\n{ex.Message}"); 
            }
            catch (Autodesk.Revit.Exceptions.FileAccessException ex)
            {
                Logger.Log($"Ошибка доступа к файлу: {serverPath}\n{ex.Message}", 4); throw new Exception($"Ошибка доступа к файлу: {serverPath}\n{ex.Message}"); 
            }
            catch (Exception ex)
            {
                Logger.Log($"Ошибка при связывании {serverPath}: {ex.Message}", 4); throw new Exception($"Ошибка при связывании {serverPath}: {ex.Message}"); 
            }
            return success;
        }
        
    }


}
