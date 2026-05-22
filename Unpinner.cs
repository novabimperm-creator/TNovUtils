using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using System.Collections.Generic;

using System.Linq;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using System.IO;
using TNovCommon;

namespace TNovUtils
{
    

    [Transaction(TransactionMode.Manual)]
    public class Unpinner : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string TNovClassName = "Откреплятор"; DateTime dateTime = DateTime.Now; string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            //подключение приложения и документа
            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }
            UIDocument uidoc = RevitAPI.UiDocument; Document doc = RevitAPI.Document;
            UIApplication uiApp = RevitAPI.UiApplication; Autodesk.Revit.ApplicationServices.Application rvtApp = uiApp.Application;
            
            //проверка подключения, запись в журнал
            if(ServerUtils.CheckConnection(TNovClassName, TNovVersion)==false) return Result.Failed;

            // создание log - файла
            Logger.Initialize(TNovClassName,dateTime,TNovVersion);
            

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
                if (qok != null && qok == true) { Logger.TurnOffExtendedLogs(); } else Logger.Log( "Расширенные логи вкл", 2);
            }

            Logger.Log("Сбор элементов",1);
            
            int failscount = 0;

            List<Grid> grids = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Grids)      //фильтр по категории Оси
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .Cast<Grid>()                      //элементы категории Оси
                                                                         .ToList();                         //формируем список

            List<Level> levels = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Levels)   //фильтр по категории Уровни
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .Cast<Level>()                     //элементы категории Уровни
                                                                         .ToList();                         //формируем список

            List<RevitLinkInstance> links = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RvtLinks)      //фильтр по категории Связи
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .Cast<RevitLinkInstance>()         //элементы категории Связи
                                                                         .ToList();                         //формируем список
            
            
            List<string> failed = new List<string>(); //пустой список id элементов с недоступным параметром Закрепить

            Logger.Log("Диалоговое окно",1);
            // Диалоговое окно
            var viewModel = new UnpinnerViewModel();
            // Десериализация
            bool forProject = false; 
            json js = new json(in TNovClassName, in forProject, out bool canserialize, out string jsonpath);
            if (canserialize)
            {
                viewModel = JsonConvert.DeserializeObject<UnpinnerViewModel>(File.ReadAllText(jsonpath));
                Logger.Log("Десериализация прошла успешно",1);
            }
            var wpfview = new UnpinnerWPF(viewModel);
            viewModel.CloseRequest += (s, e) => wpfview.Close();
            bool? ok = wpfview.ShowDialog();
            if (ok != null && ok == true) { }
            else { Logger.Log("Запуск отменен пользователем. Завершение работы.", 3); return Result.Cancelled; }
            //Сериализация
            try
            {
                File.WriteAllText(jsonpath, JsonConvert.SerializeObject(viewModel));
                Logger.Log("Сериализация прошла успешно",1);
            }
            catch (Exception ex) { Logger.Log("Ошибка при сериализации: " + ex.Message,4); }

            bool rungrids = viewModel.grids; bool runlevels = viewModel.levels; bool runlinks = viewModel.links;

            
            {
                if (rungrids) { Logger.Log("Оси вкл",2); }
                else { Logger.Log("Оси выкл;", 2); }

                if (runlevels) { Logger.Log("Уровни вкл", 2); }
                else { Logger.Log("Уровни выкл", 2); }

                if (runlinks) { Logger.Log("Связи вкл", 2); }
                else { Logger.Log("Связи выкл", 2); }
            }
            

            if(rungrids||runlevels||runlinks)
            {
                using (Transaction transaction = new Transaction(doc))
                {
                    Logger.Log("Открываем транзакцию",1);
                    transaction.Start("TNov - откреплятор");

                    
                    if (rungrids)
                    {
                        Logger.Log("Откреплятор - оси", 1);
                        foreach (var grid in grids)
                        {
                            string eid = grid.Id.ToString();
                            try
                            {
                                grid.Pinned = false; //Logger.Log("   ось " + eid + ": откреплена;");
                            }
                            catch (Exception ex) 
                            { failed.Add(eid); failscount++; Logger.Log("   ось " + eid + ": Ошибка: " + ex.Message, 4); continue; }
                        }
                    }

                    if (runlevels)
                    {
                        Logger.Log("Откреплятор - уровни:", 1);
                        foreach (var level in levels)
                        {
                            string eid = level.Id.ToString();
                            try
                            {
                                level.Pinned = false; //Logger.Log("   уровень " + eid + ": откреплен;");
                            }
                            catch (Exception ex) 
                            { failed.Add(eid); failscount++; Logger.Log("   уровень " + eid + ": Ошибка: " + ex.Message, 4); continue; }
                        }
                    }

                    if (runlinks)
                    {
                        Logger.Log("Откреплятор - связи:", 1);
                        foreach (var link in links)
                        {
                            string eid = link.Id.ToString();
                            try
                            {
                                link.Pinned = false; //Logger.Log("   связь " + eid + ": откреплена;");
                            }
                            catch (Exception ex) 
                            { failed.Add(eid); failscount++; Logger.Log("   связь " + eid + ": Ошибка: " + ex.Message, 4); continue; }
                        }
                    }
                    
                    Logger.Log("Элементы откреплены.", 1);
                    
                    if (failscount != 0)
                    {
                        Logger.Log("Открываем окно с ID проблемных элементов: " + String.Join(",", failed), 1);
                        // Диалоговое окно
                        ElementsTreeWindow window = new ElementsTreeWindow(uiApp, String.Join(",", failed), TNovClassName, dateTime, TNovVersion);
                        window.Show();
                    }
                    transaction.Commit();
                    Logger.Log("Закрываем транзакцию.",1);
                }
            }
            
            Logger.Log("Завершение работы.",5);
            return Result.Succeeded;
            
        }
    }
}