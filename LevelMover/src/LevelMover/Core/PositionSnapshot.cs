using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LevelMover.Core
{
    /// <summary>
    /// Отпечаток положения элемента в модели.
    ///
    /// Весь плагин держится на нём: сдвиг после смены уровня не вычисляется «по формуле»
    /// (у разных категорий уровень влияет на геометрию по-разному, а у некоторых — не влияет
    /// вовсе), а замеряется по факту и компенсируется. Поэтому положение снимается до переноса,
    /// снимается после и сверяется.
    /// </summary>
    internal class PositionSnapshot
    {
        /// <summary>
        /// 0,1 мм в футах. Ниже — шум пересчёта геометрии Revit, выше — сдвиг, который
        /// проектировщик уже увидит на разрезе.
        /// </summary>
        public const double Tolerance = 0.1 / 304.8;

        private readonly List<XYZ> _anchors;
        private readonly XYZ _boxMax;

        private PositionSnapshot(List<XYZ> anchors, XYZ boxMax)
        {
            _anchors = anchors;
            _boxMax = boxMax;
        }

        /// <summary>
        /// Есть ли за что зацепиться при сверке. Если мерить нечего (элемент без геометрии и без
        /// точки привязки), то и сдвинуться ему нечем — такой перенос безопасен.
        /// </summary>
        public bool IsMeasurable => _anchors.Count > 0;

        public static PositionSnapshot Take(Element element)
        {
            var anchors = new List<XYZ>();

            switch (element.Location)
            {
                case LocationPoint point:
                    anchors.Add(point.Point);
                    break;

                case LocationCurve curve when curve.Curve != null:
                    // Концы кривой, а не середина: у наклонной балки середина скроет разворот.
                    anchors.Add(curve.Curve.GetEndPoint(0));
                    anchors.Add(curve.Curve.GetEndPoint(1));
                    break;
            }

            XYZ min = null;
            XYZ max = null;
            try
            {
                // null — габарит в координатах модели, а не подрезанный видом.
                BoundingBoxXYZ box = element.get_BoundingBox(null);
                if (box != null)
                {
                    min = box.Min;
                    max = box.Max;
                }
            }
            catch
            {
                // Элемент без геометрии (марка, спецификационный экземпляр) — сверяемся по Location.
            }

            // Габарит как запасной якорь: без Location сравнивать больше нечего.
            if (anchors.Count == 0 && min != null) anchors.Add(min);

            return new PositionSnapshot(anchors, max);
        }

        /// <summary>
        /// Куда уехала точка привязки относительно исходного снимка. Именно её возвращает
        /// на место нижнее смещение.
        /// </summary>
        public XYZ AnchorShiftFrom(PositionSnapshot before)
        {
            if (before._anchors.Count == 0 || _anchors.Count == 0) return XYZ.Zero;
            return _anchors[0] - before._anchors[0];
        }

        /// <summary>
        /// Насколько уехал верх габарита. Отвечает за верхнюю зависимость: стена или колонна
        /// может сохранить низ и при этом изменить высоту.
        /// </summary>
        public double TopShiftFrom(PositionSnapshot before)
        {
            if (before._boxMax == null || _boxMax == null) return 0.0;
            return _boxMax.Z - before._boxMax.Z;
        }

        /// <summary>
        /// Худшее расхождение по замерам — итоговый приговор переносу. Если оно больше допуска,
        /// элемент откатывается: лучше не перенести, чем молча сдвинуть.
        ///
        /// Габарит целиком здесь не сравнивается намеренно. У стены он «дышит» по горизонтали от
        /// присоединений к соседям, а те при смене уровня рвутся — сравнение по габариту откатывало
        /// бы вполне корректные переносы. Расположение — это точка привязки и отметка верха.
        /// </summary>
        public double WorstShiftFrom(PositionSnapshot before)
        {
            double worst = 0.0;

            int anchors = Math.Min(_anchors.Count, before._anchors.Count);
            for (int i = 0; i < anchors; i++)
            {
                worst = Math.Max(worst, _anchors[i].DistanceTo(before._anchors[i]));
            }

            // Верх нужен и при неподвижном якоре: у стены и колонны низ остаётся на месте,
            // а высота меняется — это тоже потеря расположения.
            return Math.Max(worst, Math.Abs(TopShiftFrom(before)));
        }
    }
}
