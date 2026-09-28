// Перенесено из monumenthunny-dev/tnovpro-issues-revit (Revit/PropertyCollector.cs) для «Модели» TNovPRO:
// вопрос о модели на сайте + синхронизация с центральной моделью (2026-09-28).
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TNovUtils.Issues.Revit;
using TNovCommon;

namespace TNovUtils.Issues.ModelSync
{
    /// <summary>
    /// Снимает «паспорт» элемента Revit — то, что во вьювере показывается в панели свойств
    /// (уровень Tangl): категория, семейство, тип, уровень, материалы + все параметры
    /// экземпляра и типа. Складывается в extras .glb рядом с геометрией — вьюверу не нужен
    /// отдельный запрос, свойства едут вместе с моделью.
    ///
    /// Значения — СТРОКИ в том виде, как их показывает Revit (AsValueString учитывает
    /// единицы проекта: «200 мм», а не «0.656168»), плюс сырое числовое значение там,
    /// где оно есть (для фильтров «толщина > 200» во вьювере).
    /// </summary>
    public static class PropertyCollector
    {
        // Потолок на элемент: у некоторых семейств сотни параметров, а в панели их всё
        // равно не листают. Берём осмысленные и режем хвост, чтобы .glb не пух.
        private const int MaxParamsPerElement = 80;

        /// <summary>
        /// Параметры, которые ЧИТАЕТ движок сметы (server/athena: engine.mjs,
        /// formulas.mjs, mep.mjs) плюс наименования для примечаний и ведомости
        /// «не расценено». Список — копия белого списка десктопа
        /// (<c>desktop-cpp/app/EstimateController.cpp</c>, <c>estimateNeedsParam</c>):
        /// клиент режет паспорта ЭТИМ ЖЕ отбором перед отправкой, 94,5 МБ → 13 МБ.
        ///
        /// 🔴 До 2026-08-17 плагин собирал ВСЕ параметры (до 80 на элемент) и для
        /// каждого числового звал <c>AsValueString()</c> — форматирование по единицам
        /// проекта, самая дорогая работа на параметре. На боевой секции 76-СУЗДАЛ
        /// (226 782 элемента) это миллионы вызовов ради полей «Шероховатость»,
        /// «Кладочный план», «Термостойкость», которые клиент тут же выбрасывал.
        /// Отбор перенесён СЮДА: то же тело запроса, но работа не делается вовсе.
        ///
        /// ⚠️ Добавили параметр в движок — впишите его И сюда, И в
        /// <c>estimateNeedsParam</c>. Списки обязаны совпадать: расхождение молча
        /// уносит объёмы из сметы.
        /// </summary>
        private static readonly HashSet<string> EstimateParams = new HashSet<string>(StringComparer.Ordinal)
        {
            // объёмы и размеры
            "Объем", "Площадь", "Т Площадь", "Длина", "Уровень", "Базовый уровень", "Толщина",
            // арматура
            "Масса", "Общая масса", "Вес", "Арматура: полная длина", "Полная длина",
            // инженерка
            "Диаметр", "Условный диаметр", "Внешний диаметр", "Наружный диаметр", "Внутренний диаметр",
            "Размер", "Размер трубы", "Ширина", "Ширина лотка",
            "Имя системы", "Тип системы", "Сокращение для системы", "Классификация системы",
            // наименования: примечания сметы и ведомость «не расценено».
            // «Наименование» без префикса читает mep.mjs (param(e, [...])) — в
            // списке десктопа его нет, здесь оно есть: лишнее имя дешевле потери.
            "Т_Наименование", "ADSK_Наименование", "Наименование",
            "Т_Обозначение", "Т_Толщина стенки",
            "Семейство и типоразмер", "Тип изоляции", "Толщина изоляции",
        };

        // Параметры ТИПА одинаковы у всех его экземпляров, а достаются дорого:
        // AsValueString форматирует каждое значение по единицам проекта. На доме в
        // 110 тысяч элементов это десятки тысяч повторов одной и той же работы —
        // типов там несколько тысяч. Разбираем тип один раз за выгрузку.
        //
        // Ключ включает имя документа: ElementId уникален только внутри своего .rvt,
        // а в выгрузке дома документов полтора десятка (у ВК и ОВ 1034 общих номера).
        private static readonly Dictionary<string, Dictionary<string, object>> TypeCache
            = new Dictionary<string, Dictionary<string, object>>();

        /// <summary>Сбросить кэш типов — зовётся в начале каждой выгрузки.</summary>
        public static void ResetTypeCache()
        {
            lock (TypeCache) TypeCache.Clear();
        }

        private static Dictionary<string, object> TypeParameters(Document doc, ElementType type, HashSet<string> keep)
        {
            // Отбор входит в ключ: один и тот же тип в смете и в полном паспорте
            // даёт разные наборы, а кэш живёт всю выгрузку.
            var key = (keep == null ? "*:" : "E:") + (doc?.Title ?? "") + ":" + type.Id.LongValue();
            lock (TypeCache)
            {
                if (TypeCache.TryGetValue(key, out var cached)) return cached;
            }
            var map = new Dictionary<string, object>();
            AddParameters(map, type, keep);
            lock (TypeCache) TypeCache[key] = map;
            return map;
        }

        /// <param name="forEstimate">
        /// Паспорт для сметы: берутся только параметры из <see cref="EstimateParams"/>
        /// и не собираются материалы — движок Афины их не читает вовсе (в паспорте
        /// ему нужны category, type, level, name и parameters).
        /// </param>
        public static Dictionary<string, object> Collect(Document doc, Element el, bool forEstimate = false)
        {
            var keep = forEstimate ? EstimateParams : null;
            if (el == null) return null;

            var info = new Dictionary<string, object>();
            try
            {
                info["name"] = el.Name ?? "";
                if (el.Category != null) info["category"] = el.Category.Name;
                // Вложенное общее семейство (полотно, ручка двери) — отдельный элемент
                // той же категории, что и дверь. Без родителя сайт считал бы ручки
                // дверями: на 59-КОСМ1-1-АР было 214 «дверей» вместо 136.
                if (el is FamilyInstance fi && fi.SuperComponent != null)
                    info["superComponent"] = fi.SuperComponent.Id.LongValue();

                var typeId = el.GetTypeId();
                var type = (typeId != null && typeId != ElementId.InvalidElementId)
                    ? doc.GetElement(typeId) as ElementType : null;
                if (type != null)
                {
                    if (!string.IsNullOrEmpty(type.FamilyName)) info["family"] = type.FamilyName;
                    info["type"] = type.Name;
                }

                var levelId = el.LevelId;
                if (levelId != null && levelId != ElementId.InvalidElementId)
                {
                    var lvl = doc.GetElement(levelId);
                    if (lvl != null) info["level"] = lvl.Name;
                }

                // Материалы смете не нужны (движок читает только category/type/level/
                // name/parameters), а GetMaterialIds обращается к геометрии элемента —
                // на боевом разделе это сотни тысяч дорогих вызовов впустую.
                if (!forEstimate)
                {
                    var materials = CollectMaterials(doc, el);
                    if (materials.Count > 0) info["materials"] = materials;
                }

                var parameters = new Dictionary<string, object>();
                AddParameters(parameters, el, keep);        // параметры экземпляра
                if (type != null)
                {
                    // Параметры типа доливаются из кэша и НЕ перетирают экземплярные.
                    foreach (var kv in TypeParameters(doc, type, keep))
                    {
                        if (parameters.Count >= MaxParamsPerElement) break;
                        if (!parameters.ContainsKey(kv.Key)) parameters[kv.Key] = kv.Value;
                    }
                }
                if (parameters.Count > 0) info["parameters"] = parameters;
            }
            catch (Exception ex)
            {
                PluginLog.Write("PropertyCollector: " + ex.Message);
            }
            return info.Count > 0 ? info : null;
        }

        private static List<string> CollectMaterials(Document doc, Element el)
        {
            var names = new List<string>();
            try
            {
                foreach (ElementId mid in el.GetMaterialIds(false))
                {
                    if (doc.GetElement(mid) is Material m && !string.IsNullOrEmpty(m.Name) && !names.Contains(m.Name))
                        names.Add(m.Name);
                    if (names.Count >= 12) break;
                }
            }
            catch { /* у некоторых элементов материалы недоступны */ }
            return names;
        }

        private static void AddParameters(Dictionary<string, object> into, Element source, HashSet<string> keep)
        {
            foreach (Parameter p in source.Parameters)
            {
                if (into.Count >= MaxParamsPerElement) return;
                try
                {
                    if (p == null || p.Definition == null) continue;
                    var key = p.Definition.Name;
                    if (string.IsNullOrEmpty(key) || into.ContainsKey(key)) continue;
                    // 🔴 Отбор — ДО чтения значения. AsValueString форматирует по
                    // единицам проекта и стоит дороже всего остального в цикле;
                    // отсеяв имя, мы этой работы не делаем вовсе.
                    if (keep != null && !keep.Contains(key)) continue;

                    switch (p.StorageType)
                    {
                        case StorageType.String:
                            {
                                var s = p.AsString();
                                if (!string.IsNullOrEmpty(s)) into[key] = s;
                                break;
                            }
                        case StorageType.Double:
                            {
                                // Показываем как Revit (с единицами проекта), а сырое значение
                                // (внутренние единицы) кладём рядом — для числовых фильтров.
                                var display = p.AsValueString();
                                if (!string.IsNullOrEmpty(display))
                                    into[key] = new Dictionary<string, object>
                                    {
                                        ["text"] = display,
                                        ["value"] = Math.Round(p.AsDouble(), 6),
                                    };
                                break;
                            }
                        case StorageType.Integer:
                            {
                                var display = p.AsValueString();
                                into[key] = string.IsNullOrEmpty(display) ? (object)p.AsInteger() : display;
                                break;
                            }
                        case StorageType.ElementId:
                            {
                                var display = p.AsValueString();
                                if (!string.IsNullOrEmpty(display)) into[key] = display;
                                break;
                            }
                    }
                }
                catch { /* битый параметр не должен ронять экспорт */ }
            }
        }
    }
}
