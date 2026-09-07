using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace CleanLinks
{
    /// <summary>
    /// Точка входа плагина: строит вкладку «Чистые связи» на ленте Revit.
    /// </summary>
    public class App : IExternalApplication
    {
        private const string TabName = "Чистые связи";
        private const string PanelName = "RVT-связи";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Вкладка уже создана другим плагином или прошлой загрузкой — это нормально.
            }

            RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // Две быстрые кнопки идут первыми: это то, ради чего плагин открывают чаще всего.
            var grids = new PushButtonData(
                "CleanLinks_Grids",
                "Оси\nи уровни",
                assemblyPath,
                "CleanLinks.Commands.DisableGridsCommand")
            {
                ToolTip = "Закрывает во всех RVT-связях рабочие наборы с осями и уровнями. Одно подтверждение — и всё.",
                LongDescription =
                    "Окна выбора нет: показывается точный перечень наборов, которые закроются, и связи, " +
                    "которые останутся нетронутыми. Действует на весь проект целиком, включая 3D-виды и разрезы; " +
                    "связи при этом перезагружаются.\n\n" +
                    "Внимание: если уровни лежат в том же наборе, что и оси, они скроются вместе с ними."
            };
            SetIcons(grids, "grids");
            panel.AddItem(grids);

            var rebar = new PushButtonData(
                "CleanLinks_Rebar",
                "Арматура",
                assemblyPath,
                "CleanLinks.Commands.DisableRebarCommand")
            {
                ToolTip = "Закрывает во всех RVT-связях арматурные рабочие наборы. Одно подтверждение — и всё.",
                LongDescription =
                    "Окна выбора нет: показывается точный перечень наборов, которые закроются, и связи, " +
                    "которые останутся нетронутыми. Действует на весь проект целиком, включая 3D-виды и разрезы; " +
                    "связи при этом перезагружаются."
            };
            SetIcons(rebar, "rebar");
            panel.AddItem(rebar);

            panel.AddSeparator();

            var disable = new PushButtonData(
                "CleanLinks_Disable",
                "Выключить\nв связях",
                assemblyPath,
                "CleanLinks.Commands.DisableWorksetsCommand")
            {
                ToolTip = "Закрывает во всех RVT-связях рабочие наборы выбранных групп: оси и уровни, арматура.",
                LongDescription =
                    "Отметьте, что выключить, и сразу увидите точный список наборов, которые будут закрыты. " +
                    "Действует на весь проект целиком, включая 3D-виды и разрезы.\n\n" +
                    "Связи при этом перезагружаются. Связи, до которых дотянуться нельзя — вложенные, " +
                    "выгруженные, без совместной работы в файле — перечисляются поимённо.\n\n" +
                    "Внимание: если уровни лежат в том же наборе, что и оси, они скроются вместе с ними."
            };
            SetIcons(disable, "spread");
            panel.AddItem(disable);

            var manage = new PushButtonData(
                "CleanLinks_Manage",
                "Связи\nпроекта",
                assemblyPath,
                "CleanLinks.Commands.ManageLinksCommand")
            {
                ToolTip = "Таблица всех RVT-связей: оси и рабочие наборы, выгрузка и загрузка, графика в текущем виде.",
                LongDescription =
                    "Строка на каждую связь. Колонки «Оси и уровни», «Наборы» и «Действие» меняют весь проект " +
                    "целиком — все виды сразу. Колонки «Полутон» и «В текущем виде» действуют только в активном " +
                    "виде и шаблонами не переносятся.\n\n" +
                    "Оси связи гасятся через её рабочие наборы: настройки категорий связи API 2022 не отдаёт. " +
                    "Внимание: если уровни лежат в том же наборе, они скроются вместе с осями."
            };
            SetIcons(manage, "worksets");
            panel.AddItem(manage);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        /// <summary>
        /// Подставляет иконки из встроенных ресурсов. Отсутствие иконки не должно ронять загрузку плагина.
        /// </summary>
        private static void SetIcons(PushButtonData button, string baseName)
        {
            button.Image = LoadEmbeddedImage(baseName + "16.png");
            button.LargeImage = LoadEmbeddedImage(baseName + "32.png");
        }

        private static BitmapImage LoadEmbeddedImage(string fileName)
        {
            try
            {
                string resourceName = "CleanLinks.Resources." + fileName;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;

                    var buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);

                    var image = new BitmapImage();
                    image.BeginInit();
                    image.StreamSource = new MemoryStream(buffer);
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
