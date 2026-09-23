using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;
using TNovUtils.Checklist.Report;

namespace TNovUtils.Checklist.Commands
{
    /// <summary>
    /// «Отчет» (панель «BIM Общие»): свод чек-листов по моделям, которые за последние
    /// 7 дней синхронизировали пользователи не из группы BIM. Открытая модель не нужна —
    /// данные читаются из {ServerPath}projects\.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class ShowChecklistReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (RevitAPI.UiApplication == null) RevitAPI.Initialize(commandData);

            // LoadConfig(имя, версия) пишет usage.txt и требует открытый документ — здесь его может не быть.
            TNovConfig config = TNovConfigLoad.LoadConfig();
            if (config == null) return Result.Failed;

            if (config.LicenseType != "corp")
            {
                new InfoWindow280("Отчет доступен только для корпоративной лицензии: журнал синхронизаций ведётся только в ней.").ShowDialog();
                return Result.Cancelled;
            }

            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            const string commandName = "Отчет";
            Logger.Initialize(commandName, DateTime.Now, version);
            Logger.Log("Открытие отчёта по чек-листам", 0);

            ReportViewModel.ErrorLog = text => Logger.Log(text, 4);

            try
            {
                ReportWindow.ShowOrActivate(config.ServerPath, commandData.Application.MainWindowHandle);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Log("Ошибка открытия отчёта: " + ex, 4);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
