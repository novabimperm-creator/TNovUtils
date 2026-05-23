using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using System.Collections.Generic;

using System.Linq;
using System;
using System.Windows.Threading;
using static System.Windows.Forms.LinkLabel;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TNovCommon;

namespace TNovUtils
{
    
    [Transaction(TransactionMode.Manual)]
    public class PLW : IExternalCommand
    {
        private TNovProgressBar plwProgressBar;
        private void ThreadStartingPoint()
        {
            this.plwProgressBar = new TNovProgressBar();
            this.plwProgressBar.Show();
            Dispatcher.Run();
        }
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string TNovClassName = "Закреплятор Уровни Наборы"; DateTime dateTime = DateTime.Now; string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
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
                if (qok != null && qok == true) { Logger.TurnOffExtendedLogs(); } else Logger.Log("Расширенные логи вкл",2);
            }

            Logger.Log("Сбор элементов",1);

            int failscount1 = 0;
            int failscount2 = 0;

            List<Autodesk.Revit.DB.Grid> grids = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Grids)      //фильтр по категории Оси
                                                                                     .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                                     .Cast<Autodesk.Revit.DB.Grid>()    //элементы категории Оси
                                                                                     .ToList();                         //формируем список

            List<Level> levels = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Levels)   //фильтр по категории Уровни
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .Cast<Level>()                     //элементы категории Уровни
                                                                         .ToList();                         //формируем список

            List<RevitLinkInstance> links = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RvtLinks)      //фильтр по категории Связи
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .Cast<RevitLinkInstance>()         //элементы категории Связи
                                                                         .ToList();                         //формируем список

            

            List<Workset> worksets0 = new FilteredWorksetCollector(doc)  //рабочие наборы документа
                .OfKind(WorksetKind.UserWorkset)
                                         .Cast<Workset>()                   //элементы категории Рабочие наборы
                                         .ToList();                         //формируем список

            Logger.Log("Элементы собраны. Создаем списки для работы, проверяем, является ли модель файлом хранилища",1);

            List<string> linkslist = new List<string>(); //пустой список имен связей
            List<string> failed1 = new List<string>(); //пустой список id связей с недоступным параметром Рабочий набор
            List<string> failed2 = new List<string>(); //пустой список id связей с недоступным параметром Закрепить


            List<Workset> worksetsNotRemove = new List<Workset>(); //пустой список неудаляемых РН
            List<RevitLinkInstance> linksToChange = new List<RevitLinkInstance>(); //пустой список изменяемых связей

            bool dws = doc.IsWorkshared; if (!dws) Logger.Log("Документ не является ФХ", 2);
            
            string tname = "TNov - Закреплятор Уровни Наборы"; 

            Logger.Log("Диалоговое окно",1);
            //Вьюмодель (без открытия окна)
            var viewModel = new PLWViewModel();
            // Десериализация
            bool forProject = false;
            json js = new json(in TNovClassName, in forProject, out bool canserialize, out string jsonpath);
            if (canserialize)
            {
                viewModel = JsonConvert.DeserializeObject<PLWViewModel>(File.ReadAllText(jsonpath));
                Logger.Log("Десериализация прошла успешно",1);
            }

            if (viewModel.show)
            {
                var wpfview = new PLWWPF(viewModel);
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
            }

            if (viewModel.pin == false && viewModel.levels == false && viewModel.worksets == false) 
            { Logger.Log("Все галочки сняты. Завершение работы.", 3); return Result.Cancelled; } //ни одна галочка не выбрана...

            if (dws == true && links.Count>0)
            {
                foreach (var link in links)
                {
                    WorksetId lwid = link.WorksetId; //получаем id набора связи
                    Element linkType = doc.GetElement(link.GetTypeId());//тип
                    WorksetId ltypewid = linkType.WorksetId;
                    string lname = link.Name;
                    string[] nameparts = lname.Split(new char[] { ':' });
                    lname = nameparts[0];
                    lname = lname.Replace(".rvt", "");
                    linkslist.Add(lname);
                    bool changeLink = true;

                    foreach (var workset in worksets0)
                    {
                        WorksetId wid = workset.Id;
//нужно сделать гибкое условие: если имя набора содержит
//имя связи, нужно 1) проверить экз и назначить при необходимости через транзакцию
//2) то же, тип
//если в 1 и/или 2 возникли ошибки (транзакции внутри try)
//то {}
//иначе набор добавляем в список неудаляемых и changelink false
//после этого break




                        if (wid == lwid && wid == ltypewid && workset.Name.Contains(lname))
                        {
                            worksetsNotRemove.Add(workset); //если набор связи содержит в названии её имя - добавляем его в список неудаляемых
                            changeLink = false; break;
                        }
                    }

                    if(changeLink) linksToChange.Add(link);
                }
                Logger.Log("Список изменяемых связей:", 1);
                foreach (var l in linksToChange) { Logger.Log("   " + l.Name + ";",1); }
            }


            int ec = 0; // счетчик неправильных имен уровней (ec = error counter)
            List<string> wrongnames = new List<string>();

            int allcount = grids.Count + levels.Count + linksToChange.Count;

            //Транзакция           

            using (Transaction transaction = new Transaction(doc))
            {
                
                transaction.Start(tname);
                Logger.Log("Открываем транзакцию",1);

                Thread thread = new Thread(new ThreadStart(this.ThreadStartingPoint));
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                Thread.Sleep(100);

                int PBCount = 0;
                this.plwProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.plwProgressBar.TNov_ProgressBar.Minimum = (double)PBCount));
                this.plwProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.plwProgressBar.value.Text = PBCount.ToString()));
                this.plwProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.plwProgressBar.TNov_ProgressBar.Maximum = (double)allcount));
                this.plwProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.plwProgressBar.maxvalue.Text = allcount.ToString()));

                if (dws&&viewModel.worksets)
                {
                    //проверяем наличие набора для осей/уровней
                    List<Workset> worksetsGL = new List<Workset>();
                    foreach (Workset ws in worksets0)
                    {
                        string wname = ws.Name;
                        if (wname == "Общие слои и сетки") worksetsGL.Add(ws);
                        if (wname == "Оси и уровни") worksetsGL.Add(ws);
                        if (wname == "Общие уровни и сетки") worksetsGL.Add(ws);
                    }
                    if (worksetsGL.Count == 0)
                    {
                        Workset ws = Workset.Create(doc, "Общие слои и сетки");
                        Logger.Log("Создаем набор Общие слои и сетки",2);
                        worksetsNotRemove.Add(ws);
                    }
                    //проверяем наличие набора для заданий
                    List<Workset> worksets01 = new FilteredWorksetCollector(doc)  //рабочие наборы документа
                        .OfKind(WorksetKind.UserWorkset)
                                         .Cast<Workset>()                   //элементы категории Рабочие наборы
                                         .ToList();                         //формируем список
                    List<Workset> worksetsTasks = new List<Workset>();
                    foreach (Workset ws in worksets01)
                    {
                        string wname = ws.Name;
                        if (wname == "Задания смежникам") worksetsTasks.Add(ws);
                    }
                    if (worksetsTasks.Count == 0)
                    {
                        Workset ws = Workset.Create(doc, "Задания смежникам");
                        Logger.Log("Создаем набор Задания смежникам", 2);
                        worksetsNotRemove.Add(ws);
                    }
                }
                


                foreach (var link in linksToChange)
                {
                    
                    Logger.Log("Связанный файл "+link.Name, 2);

                    string lname = link.Name;
                    string[] nameparts = lname.Split(new char[] { ':' });
                    lname = nameparts[0];
                    lname = lname.Replace(".rvt", "");
                    string linkid = link.Id.ToString();
                    if (dws&&viewModel.worksets)
                    {
                        int worksetScenario = 2;
                        if (link.Name.Contains("-РФ") || link.Name.Contains("_РФ")) worksetScenario = 0;
                        if (link.Name.Contains("Задани") || link.Name.Contains("задани") || link.Name.Contains("-ЗД") || link.Name.Contains("_ЗД") || link.Name.Contains("ЗАДАНИЕ")) worksetScenario = 1;

                        Autodesk.Revit.DB.Parameter param = link.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);//получаем параметр "РН"
                                                                                                                      //тип связи
                        Element linkType = doc.GetElement(link.GetTypeId());//тип
                        Parameter typeparam = linkType.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);

                        switch (worksetScenario)
                        {
                            case 0:
                                List<Workset> worksetsGL = new List<Workset>();
                                List<Workset> worksets10 = new FilteredWorksetCollector(doc)  //рабочие наборы документа
                                    .OfKind(WorksetKind.UserWorkset)
                                         .Cast<Workset>()                   //элементы категории Рабочие наборы
                                         .ToList();                         //формируем список
                                foreach (Workset ws in worksets10) //ищем наличие набора осей и уровней, добавляем его в список РН осей/уровней
                                {
                                    string wname = ws.Name;
                                    if (wname == "Общие слои и сетки") worksetsGL.Add(ws);
                                    if (wname == "Оси и уровни") worksetsGL.Add(ws);
                                    if (wname == "Общие уровни и сетки") worksetsGL.Add(ws);
                                }

                                List<int> widsGL = new List<int>(); //пустой список номеров РН осей/уровней

                                foreach (Workset wsGL in worksetsGL) //заполняем список номеров РН осей/уровней
                                {
                                    int widGL = wsGL.Id.IntegerValue;
                                    widsGL.Add(widGL);
                                }

                                try
                                {
                                    param.Set(widsGL[0]); //назначаем РН экземпляру
                                    typeparam.Set(widsGL[0]); //назначаем РН типу
                                    Logger.Log("   назначен набор " + widsGL[0].ToString(),2);
                                }
                                catch (Exception ex) 
                                { failed1.Add(linkid); failscount1++; Logger.Log("Связь " + link.Name + " Ошибка: " + ex.Message,4);  }
                            break;
                            case 1:
                                List<Workset> worksetsTasks = new List<Workset>();
                                List<Workset> worksets20 = new FilteredWorksetCollector(doc)  //рабочие наборы документа
                                    .OfKind(WorksetKind.UserWorkset)
                                         .Cast<Workset>()                   //элементы категории Рабочие наборы
                                         .ToList();                         //формируем список
                                foreach (Workset ws in worksets20) //ищем наличие набора заданий, добавляем его в список РН заданий
                                {
                                    string wname = ws.Name;
                                    if (wname == "Задания смежникам") worksetsTasks.Add(ws);
                                }

                                List<int> widsTasks = new List<int>(); //пустой список номеров РН заданий

                                foreach (Workset wsTask in worksetsTasks) //заполняем список номеров РН заданий
                                {
                                    int widTask = wsTask.Id.IntegerValue;
                                    widsTasks.Add(widTask);
                                }

                                
                                try
                                {
                                    param.Set(widsTasks[0]); //назначаем РН экземпляру
                                    typeparam.Set(widsTasks[0]); //назначаем РН типу
                                    Logger.Log("   назначен набор " + widsTasks[0].ToString(), 4);
                                }
                                catch (Exception ex) 
                                { failed1.Add(linkid); failscount1++; Logger.Log("Связь " + link.Name + " Ошибка: " + ex.Message, 4);  }
                            break;
                            case 2:
                                try
                                {
                                    Workset ws = Workset.Create(doc, lname); //создаем наборы для связей 
                                    param.Set(ws.Id.IntegerValue); //назначаем РН экземпляру
                                    typeparam.Set(ws.Id.IntegerValue); //назначаем РН типу
                                    Logger.Log("   создан и назначен набор " + lname,2);
                                    worksetsNotRemove.Add(ws); //добавляем созданный РН в список неудаляемых
                                }
                                catch (System.Exception ex)
                                {
                                    failed1.Add(linkid); failscount1++;
                                    Logger.Log("Связь " + lname + " Ошибка: " + ex.Message,4); 
                                }
                            break;
                        }

                    }
                    if (viewModel.pin&&link.Pinned==false)
                    {
                        try
                        {
                            link.Pinned = true; Logger.Log("   связь закреплена",2);
                        }
                        catch (Exception ex) 
                        { failed2.Add(linkid); failscount2++; Logger.Log("Связь " + lname + " Ошибка: " + ex.Message, 4); }
                    }

                    PBCount++;
                    this.plwProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.plwProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                    this.plwProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.plwProgressBar.value.Text = PBCount.ToString()));

                }

                foreach (var grid in grids) //назначаем набор осям и закрепляем
                {
                    string eid = grid.Id.ToString();
                    Logger.Log("Ось " + eid, 2);

                    if (dws && viewModel.worksets)
                    {
                        //Актуализируем список наборов после создания новых
                        List<Workset> worksets1 = new FilteredWorksetCollector(doc)  //рабочие наборы документа
                            .OfKind(WorksetKind.UserWorkset)
                                                    .Cast<Workset>()                   //элементы категории Рабочие наборы
                                                    .ToList();                         //формируем список
                                                                                       //-- FIX 0.9.1 --
                                                                                       //-- Исправлено: worksets1 на worksetsGL

                        
                        List<Workset> worksetsGL = new List<Workset>();
                        foreach (Workset ws in worksets1) //ищем наличие набора осей и уровней, добавляем его в список РН осей/уровней
                        {
                            string wname = ws.Name;
                            if (wname == "Общие слои и сетки") worksetsGL.Add(ws);
                            if (wname == "Оси и уровни") worksetsGL.Add(ws);
                            if (wname == "Общие уровни и сетки") worksetsGL.Add(ws);
                        }

                        List<int> widsGL = new List<int>(); //пустой список номеров РН осей/уровней

                        foreach (Workset wsGL in worksetsGL) //заполняем список номеров РН осей/уровней
                        {
                            int widGL = wsGL.Id.IntegerValue;
                            widsGL.Add(widGL);
                        }

                        Autodesk.Revit.DB.Parameter param = grid.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);//получаем параметр "РН"
                        try
                        {
                            param.Set(widsGL[0]); //берем первое значение из списка номеров РН осей/уровней
                            Logger.Log("   назначен набор " + widsGL[0].ToString(), 2);
                        }
                        catch (Exception ex) { failed1.Add(eid); failscount1++; Logger.Log("Ось " + eid + " Ошибка: " + ex.Message,4);  }
                        
                        
                    }
                    if (viewModel.pin&&grid.Pinned==false)
                    {
                        try
                        {
                            grid.Pinned = true; Logger.Log("   ось закреплена",2);
                        }
                        catch (Exception ex) { failed2.Add(eid); failscount2++; Logger.Log("Ось " + eid + " Ошибка: " + ex.Message, 4); }
                    }

                    PBCount++;
                    this.plwProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.plwProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                    this.plwProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.plwProgressBar.value.Text = PBCount.ToString()));

                }

                foreach (var level in levels) //назначаем набор уровням и закрепляем
                {
                    string eid = level.Id.ToString();
                    Logger.Log("Уровень " + eid, 2);

                    if (dws && viewModel.worksets)
                    {
                        //Актуализируем список наборов после создания новых
                        List<Workset> worksets1 = new FilteredWorksetCollector(doc)
                            .OfKind(WorksetKind.UserWorkset)
                                                    .Cast<Workset>()                   
                                                    .ToList();                         
                        
                        List<Workset> worksetsGL = new List<Workset>();
                        foreach (Workset ws in worksets1) //ищем наличие набора осей и уровней, добавляем его в список РН осей/уровней
                        {
                            string wname = ws.Name;
                            if (wname == "Общие слои и сетки") worksetsGL.Add(ws);
                            if (wname == "Оси и уровни") worksetsGL.Add(ws);
                            if (wname == "Общие уровни и сетки") worksetsGL.Add(ws);
                        }

                        List<int> widsGL = new List<int>(); //пустой список номеров РН осей/уровней

                        foreach (Workset wsGL in worksetsGL) //заполняем список номеров РН осей/уровней
                        {
                            int widGL = wsGL.Id.IntegerValue;
                            widsGL.Add(widGL);
                        }

                        Autodesk.Revit.DB.Parameter param = level.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);//получаем параметр "РН"
                        try
                        {
                            param.Set(widsGL[0]); //берем первое значение из списка номеров РН осей/уровней
                            Logger.Log("   назначен набор " + widsGL[0].ToString(), 2);
                        }
                        catch (Exception ex) { failed1.Add(eid); failscount1++; Logger.Log("Уровень " + eid + " Ошибка: " + ex.Message, 4);  }


                    }
                    if (viewModel.pin&&level.Pinned==false)
                    {
                        try
                        {
                            level.Pinned = true; Logger.Log("   уровень закреплен", 2);
                        }
                        catch (Exception ex) { failed2.Add(eid); failscount2++; Logger.Log("Уровень " + eid + " Ошибка: " + ex.Message, 4); }
                    }

                    if (viewModel.levels)
                    {
                        //переименовка
                        string name0 = level.Name.Replace("_", " "); //получаем имя уровня
                        int i = 0, count = 0;
                        var s = " ";
                        while ((i = name0.IndexOf(s, i)) != -1) { ++count; i += s.Length; } //ищем сколько пробелов в имени уровня
                        if (count < 2)
                        {
                            ec = ++ec; //счетчик неправильных имен уровней
                            wrongnames.Add(level.Name); Logger.Log("Уровень " + level.Name + ": имя не по регламенту",1);
                        }
                        else
                        {
                            string name = name0.Replace("_", " "); //получаем имя уровня
                            string[] nameparts = name.Split(new char[] { ' ' }); //делим имя пробелами
                            string basicelev = nameparts[1];

                            double elev = level.get_Parameter(BuiltInParameter.LEVEL_ELEV).AsDouble(); //получаем отметку уровня
                            elev = elev * 0.3048;

                            string elevstr = string.Format("{0:0.000}", elev);
                            name = name.Replace(basicelev, elevstr);
                            try
                            {
                                level.LookupParameter("Имя")?.Set(name);
                                
                                {
                                    if (name == name0) { Logger.Log("   Уровень " + name0 + ": переименовка не требуется",2); }
                                    else { Logger.Log("Уровень " + name0 + " переименован в " + name,2); }
                                }
                            }
                            catch (Exception ex) { Logger.Log("Уровень " + eid + " Ошибка: " + ex.Message, 4); }

                        }
                    }

                    PBCount++;
                    this.plwProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.plwProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                    this.plwProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.plwProgressBar.value.Text = PBCount.ToString()));


                }



                //после назначения наборов всем связям также обозначим для удаления наборы, содержащие в имени первые 3 символа шифра и не назначенные связям
                if (dws&&viewModel.worksets&&worksetsNotRemove.Count>0 && links.Count > 0)
                {
                    List<Workset> worksets2 = new FilteredWorksetCollector(doc)  //рабочие наборы документа
                        .OfKind(WorksetKind.UserWorkset)
                                                    .Cast<Workset>()                   //элементы категории Рабочие наборы
                                                    .ToList();                         //формируем список

                    Logger.Log("Ищем наборы для удаления",1);
                    List<Workset> worksetsNR = new List<Workset>();

                    foreach (var wnr in worksetsNotRemove)
                    {
                        worksetsNR.Add(wnr);
                    }

                    string firstLinkName = linkslist[0]; Logger.Log("Имя связи для поиска наборов связей: " + firstLinkName, 1);
                    string projectcode = firstLinkName.Substring(0, 2);
                    int j = 0;
                    foreach (var w in worksets2) // --- 2.2.2 --- worksets1 заменено на worksets2
                    {
                        int i = 0;
                        Logger.Log("   Набор" + w.Name,1);

                        foreach (var wnr in worksetsNR)
                        {
                            if (w.Name == wnr.Name) { i++; Logger.Log("      в списке неудаляемых",1); }
                        }
                        if (i == 0 && w.Name.Contains(projectcode))
                        {
                            string datetimenow = DateTime.Now.ToString();
                            datetimenow = datetimenow.Replace("/", "");
                            datetimenow = datetimenow.Replace(".", "");
                            datetimenow = datetimenow.Replace(":", "");
                            WorksetTable.RenameWorkset(doc, w.Id, "удалить" + j.ToString() + " " + datetimenow);
                            Logger.Log("      помечен для удаления",1);
                            j++;
                        }
                    }
                }
                

                transaction.Commit();
                this.plwProgressBar.Dispatcher.Invoke((System.Action)(() => this.plwProgressBar.Close()));
                Logger.Log("Закрываем транзакцию",1);
            }

            List<string> failed = failed1
    .Union(failed2, StringComparer.OrdinalIgnoreCase)
    .ToList();
            if (failed.Count>0)
            {
                Logger.Log("Открываем окно с ID проблемных элементов: " + String.Join(",", failed), 1);
                // Диалоговое окно
                ElementsTreeWindow window = new ElementsTreeWindow(uiApp, String.Join(",", failed), TNovClassName, dateTime, TNovVersion);
                window.Show();
                
            }
            /*
            if (failscount1 > 0) 
            {
                // Диалоговое окно
                var viewModel1 = new InfoWindowTextFieldViewModel();
                viewModel1.headtxt = "Один или несколько экземпляров связанных моделей не помещены в наборы:"; 
                viewModel1.ids = String.Join(",", failed1);
                viewModel1.lowtxt = "Они могут находиться в группах модели.";
                var wpfview = new InfoWindowTextField(viewModel1);
                viewModel1.CloseRequest += (s, e) => wpfview.Close();
                bool? ok = wpfview.ShowDialog();
            }
            if (failscount2 > 0)
            {
                // Диалоговое окно
                var viewModel2 = new InfoWindowTextFieldViewModel();
                viewModel2.headtxt = "Один или несколько элементов не закреплены:";
                viewModel2.ids = String.Join(",", failed2);
                viewModel2.lowtxt = "Они могут находиться в группах модели.";
                var wpfview = new InfoWindowTextField(viewModel2);
                viewModel2.CloseRequest += (s, e) => wpfview.Close(); 
                bool? ok = wpfview.ShowDialog();
            }*/
            if (ec > 0)
            {
                string wn = "";
                int i = 0;
                foreach (string wname in wrongnames)
                {
                    if (i == 0) { wn = wn + wname; }
                    else { wn = wn + ", " + wname; }
                    i++;
                }
                //сообщение об ошибке
                string info1txt = "Уровни " + wn + " названы не по регламенту!\r\n" +
                    "Структура наименования имеет вид(с пробелами без нижних подчеркиваний):\r\n" +
                    "АА ББ ВВ, где\r\nАА – код уровня в цифровом формате(-01, 01, 02…);\r\n" +
                    "ББ – отметка уровня от 0.000(например, -3.200 или + 1.500);\r\n" +
                    "ВВ – название уровня(например, Автостоянка, Подвал, Этаж 7, Покрытие).\r\nПример наименования уровня:\r\n" +
                    "\t - 01 - 3.200 Подвал\r\n" +
                    "\t05 + 12.850 Этаж 5\r\n";
                var info1 = new InfoWindow400(info1txt); info1.ShowDialog();

            }
            Logger.Log("Завершение работы.",5);
            return Result.Succeeded;
        }
        
        
    
    }       

}