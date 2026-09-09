using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace LevelMover
{
    /// <summary>
    /// Точка входа плагина: строит вкладку «Перенос элементов» на ленте Revit.
    /// </summary>
    public class App : IExternalApplication
    {
        private const string TabName = "Перенос элементов";
        private const string PanelName = "Уровни";

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

            var move = new PushButtonData(
                "LevelMover_Move",
                "Перенести",
                assemblyPath,
                "LevelMover.Commands.MoveToLevelCommand")
            {
                ToolTip = "Меняет уровень выделенных элементов, не сдвигая их с места.",
                LongDescription =
                    "Выделите элементы в проекте и нажмите кнопку. Плагин сменит уровень и тем же " +
                    "шагом поправит смещение от него, поэтому элементы останутся на прежних отметках.\n\n" +
                    "Для стен, колонн и прочих семейств на двух уровнях в окне есть второй список — " +
                    "верхний уровень; верх при этом тоже остаётся на месте.\n\n" +
                    "Элементы, которые перенести без сдвига нельзя, остаются на прежнем уровне и " +
                    "перечисляются в отчёте с причиной."
            };
            SetIcons(move, "move");
            panel.AddItem(move);

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
                string resourceName = "LevelMover.Resources." + fileName;
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
