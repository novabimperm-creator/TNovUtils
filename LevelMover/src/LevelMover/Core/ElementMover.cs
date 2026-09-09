using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

namespace LevelMover.Core
{
    /// <summary>
    /// Перенос элементов на другой уровень с сохранением расположения.
    ///
    /// Порядок работы на каждом элементе один и тот же: снять положение, сменить уровень,
    /// замерить, куда элемент уехал, и вернуть его тем же смещением от нового уровня. Если
    /// вернуть не удалось — элемент откатывается целиком и попадает в отчёт.
    /// </summary>
    internal static class ElementMover
    {
        /// <summary>
        /// Вызывается внутри уже открытой транзакции. Каждый элемент обрабатывается в своей
        /// подтранзакции — так неудача одного не отменяет перенос остальных.
        /// </summary>
        public static List<MoveResult> Run(
            Document document,
            IEnumerable<Element> elements,
            ElementId baseLevelId,
            ElementId topLevelId)
        {
            var results = new List<MoveResult>();

            foreach (Element element in elements)
            {
                // Перенос одного элемента может унести за собой зависимый — например, стена
                // забирает размещённое в ней окно, а вложенный элемент Revit пересоздаёт.
                if (!element.IsValidObject) continue;

                results.Add(MoveOne(document, element, baseLevelId, topLevelId));
            }

            return results;
        }

        private static MoveResult MoveOne(
            Document document,
            Element element,
            ElementId baseLevelId,
            ElementId topLevelId)
        {
            var result = new MoveResult(element.Id, Describe(element));

            Parameter baseLevel = LevelBinding.FindBaseLevel(element);
            if (baseLevel == null)
            {
                result.Problem = "у элемента нет параметра уровня";
                return result;
            }

            if (baseLevel.IsReadOnly)
            {
                result.Problem = "параметр «" + baseLevel.Definition.Name + "» доступен только для чтения — " +
                                 "элемент размещён на грани, в группе или закреплён";
                return result;
            }

            bool topRequested = topLevelId != null && topLevelId != ElementId.InvalidElementId;
            Parameter topLevel = topRequested ? LevelBinding.FindTopLevel(element) : null;
            if (topLevel != null && topLevel.IsReadOnly) topLevel = null;

            bool baseAlready = baseLevel.AsElementId() == baseLevelId;
            bool topAlready = topLevel == null || topLevel.AsElementId() == topLevelId;
            if (baseAlready && topAlready)
            {
                result.Problem = "уже на этом уровне";
                return result;
            }

            // Запоминаем до транзакции: если перенос сорвётся, подсказка про верхний уровень
            // должна опираться на устройство элемента, а не на откатанное состояние.
            bool twoLevelElement = LevelBinding.FindTopLevel(element) != null;

            var subTransaction = new SubTransaction(document);
            subTransaction.Start();

            try
            {
                PositionSnapshot before = PositionSnapshot.Take(element);

                // Оба уровня выставляются до пересчёта: если сначала поднять низ, а верх оставить
                // на старом уровне, стена или колонна на мгновение получит нулевую высоту, и Revit
                // откажет ещё до того, как мы доберёмся до верхней зависимости.
                if (!baseAlready) baseLevel.Set(baseLevelId);
                if (topLevel != null) topLevel.Set(topLevelId);
                document.Regenerate();

                if (!element.IsValidObject)
                {
                    subTransaction.RollBack();
                    result.Problem = "Revit удалил элемент при смене уровня";
                    return result;
                }

                if (!before.IsMeasurable)
                {
                    // Ни точки привязки, ни геометрии — сдвигаться нечему, сверять нечего.
                    subTransaction.Commit();
                    result.Moved = true;
                    return result;
                }

                RestoreBase(document, element, before);
                if (topLevel != null) RestoreTop(document, element, before);

                double shift = PositionSnapshot.Take(element).WorstShiftFrom(before);
                if (shift > PositionSnapshot.Tolerance)
                {
                    subTransaction.RollBack();
                    result.Problem = "расположение не сохранилось, сдвиг " + Millimeters(shift);
                    if (topLevel == null && twoLevelElement)
                    {
                        result.Problem += " — элемент опирается на два уровня, укажите верхний";
                    }

                    return result;
                }

                subTransaction.Commit();
                result.Moved = true;
                return result;
            }
            catch (Exception exception)
            {
                if (subTransaction.HasStarted() && !subTransaction.HasEnded()) subTransaction.RollBack();
                result.Problem = "Revit отклонил перенос: " + exception.Message;
                return result;
            }
        }

        /// <summary>
        /// Возвращает низ элемента на исходную отметку. Сначала штатным смещением от уровня —
        /// это то же самое, что проектировщик набрал бы руками; и только если смещения нет или
        /// его не хватило, элемент двигается целиком.
        /// </summary>
        private static void RestoreBase(Document document, Element element, PositionSnapshot before)
        {
            // Две попытки: первая снимает основную величину, вторая добирает остаток, если Revit
            // на пересчёте сдвинул элемент ещё раз (привязки, наклон, присоединения).
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Parameter offset = LevelBinding.FindBaseOffset(element);
                if (offset == null) break;

                double shift = PositionSnapshot.Take(element).AnchorShiftFrom(before).Z;
                if (Math.Abs(shift) <= PositionSnapshot.Tolerance) return;

                if (!TrySet(offset, offset.AsDouble() - shift)) break;
                document.Regenerate();
            }

            XYZ drift = PositionSnapshot.Take(element).AnchorShiftFrom(before);
            if (drift.GetLength() <= PositionSnapshot.Tolerance) return;

            try
            {
                ElementTransformUtils.MoveElement(document, element.Id, -drift);
                document.Regenerate();
            }
            catch (Exception)
            {
                // Элемент закреплён или его перемещение запрещено — итоговая сверка это поймает.
            }
        }

        /// <summary>
        /// Возвращает верх элемента на исходную отметку после смены верхней зависимости.
        /// Двигать элемент целиком тут нельзя — это увело бы обратно уже выправленный низ.
        /// </summary>
        private static void RestoreTop(Document document, Element element, PositionSnapshot before)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Parameter offset = LevelBinding.FindTopOffset(element);
                if (offset == null) return;

                double shift = PositionSnapshot.Take(element).TopShiftFrom(before);
                if (Math.Abs(shift) <= PositionSnapshot.Tolerance) return;

                if (!TrySet(offset, offset.AsDouble() - shift)) return;
                document.Regenerate();
            }
        }

        /// <summary>
        /// Revit отвергает значения вне допустимого диапазона, например отрицательную высоту.
        /// Это не повод ронять перенос: пусть решает итоговая сверка.
        /// </summary>
        private static bool TrySet(Parameter parameter, double value)
        {
            try
            {
                return parameter.Set(value);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Describe(Element element)
        {
            string category = element.Category != null ? element.Category.Name : "Без категории";

            string name;
            try
            {
                name = element.Name;
            }
            catch (Exception)
            {
                name = string.Empty;
            }

            string id = "(ID " + element.Id.IntegerValue.ToString(CultureInfo.InvariantCulture) + ")";
            return string.IsNullOrEmpty(name) ? category + " " + id : category + ": " + name + " " + id;
        }

        private static string Millimeters(double feet)
        {
            double millimeters = UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
            return millimeters.ToString("0.#", CultureInfo.CurrentCulture) + " мм";
        }
    }
}
