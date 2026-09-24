using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;

namespace TNovUtils.Checklist.Revit
{
    /// <summary>
    /// Открытие рабочего набора связи и загрузка связи — общее для автопроверок, которым нужны связи.
    /// </summary>
    internal static class LinkLoading
    {
        /// <summary>
        /// Открыть закрытый набор API не умеет; обход — ShowElements по элементу набора (только с UI).
        /// </summary>
        public static bool TryOpenWorkset(Document doc, Element element)
        {
            try
            {
                new UIDocument(doc).ShowElements(element.Id);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("Чек-лист: не удалось открыть набор через ShowElements: " + ex.Message, 3);
                return false;
            }
        }

        /// <returns>null — связь загружена (или уже была загружена), иначе текст ошибки.</returns>
        public static string TryLoad(Document doc, RevitLinkType type)
        {
            if (RevitLinkType.IsLoaded(doc, type.Id)) return null;
            try
            {
                var result = type.Load().LoadResult;
                return result == LinkLoadResultType.LinkLoaded || result == LinkLoadResultType.UsedExisting
                    ? null
                    : "Revit вернул статус «" + result + "»";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
