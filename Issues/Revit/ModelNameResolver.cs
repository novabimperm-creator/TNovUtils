using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace TNovUtils.Issues.Revit
{
    /// <summary>
    /// Каноническое имя модели (PLAN §C3). Имя = ключ идентификации модели и
    /// основа авто-привязки проекта по префиксу. GUID НЕ используем (Revit Server,
    /// центральные/локальные файлы). Это «предоставленный код» нормализации doc.Title.
    /// </summary>
    public static class ModelNameResolver
    {
        /// <returns>каноническое имя; null если файл «отсоединён» (detached=true).</returns>
        public static string Resolve(Document doc, Application rvtApp, out bool detached)
        {
            detached = false;

            // Порядок строго по ТЗ: нормализация, затем проверка «отсоединено».
            string docName = doc.Title.ToString();
            docName = docName.Replace(",", " ");
            string userName = rvtApp.Username.Replace(",", "");
            docName = docName.Replace("_" + userName, ""); // отсечь суффикс локальной копии
            docName = docName.Replace(",", "");

            // Отсоединённый файл не привязан к центральной модели — привязывать нечего.
            if (docName.Contains("отсоединено"))
            {
                detached = true;
                return null;
            }
            return docName;
        }
    }
}
