using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using TNovCommon;

namespace TNovUtils
{
    [Transaction(TransactionMode.Manual)]
    public class TypeFilter : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {

            #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Типофильтр";
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

            #region Окно
            TypeFilterRevitBridge.Initialize();
            try
            {
                TypeFilterData data = TypeFilterActions.Collect(uidoc);
                TypeFilterHost.ShowOrActivate(uiApp, data);
                Logger.Log("Окно Типофильтра открыто", 1);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Log("Ошибка открытия Типофильтра: " + ex, 4);
                message = ex.Message;
                return Result.Failed;
            }
            #endregion
        }
    }
    public class AlphanumComparatorFastString : IComparer<string>
    {
        public int Compare(string s1, string s2)
        {
            if (s1 == null || s2 == null)
                return 0;
            int length1 = s1.Length;
            int length2 = s2.Length;
            int index1 = 0;
            int index2 = 0;
            while (index1 < length1 && index2 < length2)
            {
                char c1 = s1[index1];
                char c2 = s2[index2];
                char[] chArray1 = new char[length1];
                int num1 = 0;
                char[] chArray2 = new char[length2];
                int num2 = 0;
                do
                {
                    chArray1[num1++] = c1;
                    ++index1;
                    if (index1 < length1)
                        c1 = s1[index1];
                    else
                        break;
                }
                while (char.IsDigit(c1) == char.IsDigit(chArray1[0]));
                do
                {
                    chArray2[num2++] = c2;
                    ++index2;
                    if (index2 < length2)
                        c2 = s2[index2];
                    else
                        break;
                }
                while (char.IsDigit(c2) == char.IsDigit(chArray2[0]));
                string s = new string(chArray1);
                string str = new string(chArray2);
                int num3 = !char.IsDigit(chArray1[0]) || !char.IsDigit(chArray2[0]) ? s.CompareTo(str) : CompareDigits(new string(chArray1, 0, num1), new string(chArray2, 0, num2));
                if (num3 != 0)
                    return num3;
            }
            return length1 - length2;
        }

        // Числа любой длины (артикулы, коды) без int.Parse: сначала по количеству значащих цифр.
        private static int CompareDigits(string a, string b)
        {
            a = a.TrimStart('0');
            b = b.TrimStart('0');
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        }
    }
}
