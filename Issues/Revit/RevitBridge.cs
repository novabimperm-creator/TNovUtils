using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TNovUtils.Issues.Revit
{
    /// <summary>
    /// Мост «немодальное окно → Revit API». Обращение к Revit API из не-API потока
    /// (WPF-окно) запрещено — действия ставятся в очередь и выполняются через ExternalEvent.
    /// </summary>
    public sealed class RevitActionHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Action<UIApplication>> _queue = new ConcurrentQueue<Action<UIApplication>>();
        public ExternalEvent Event { get; set; }

        public void Enqueue(Action<UIApplication> action)
        {
            _queue.Enqueue(action);
            Event?.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(app); }
                catch (Exception ex)
                {
                    PluginLog.Write("RevitAction FAILED: " + ex);
                    TaskDialog.Show("TNovPRO Вопросы", "Ошибка Revit-операции: " + ex.Message);
                }
            }
        }

        public string GetName() => "TNovPRO Issues — Revit Action";
    }

    public static class RevitBridge
    {
        private static RevitActionHandler _handler;

        public static void Initialize()
        {
            if (_handler != null) return;
            _handler = new RevitActionHandler();
            _handler.Event = ExternalEvent.Create(_handler);
        }

        public static void Enqueue(Action<UIApplication> action)
        {
            if (_handler == null) throw new InvalidOperationException("RevitBridge не инициализирован");
            _handler.Enqueue(action);
        }

#if R2027
        private static ElementId MakeElementId(long value) => new ElementId(value); // Revit 2024+: long-конструктор
#else
        private static ElementId MakeElementId(long value) => new ElementId(unchecked((int)value));
#endif

        /// <summary>C6/M3: выделить элемент, открыть 3D-вид и подрезать его section box'ом
        /// (без изоляции) с запасом — чтобы рядом было видно ближайшее окружение.</summary>
        public static void GoToElement(long elementId)
        {
            Enqueue(app =>
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                var doc = uidoc.Document;
                var id = MakeElementId(elementId);
                var el = doc.GetElement(id);
                if (el == null)
                {
                    TaskDialog.Show("TNovPRO Вопросы", "Элемент не найден в текущей модели (удалён или другая модель).");
                    return;
                }

                var v3 = EnsureView3D(uidoc);                       // открыть/создать 3D-вид
                if (v3 != null && uidoc.ActiveView.Id != v3.Id) uidoc.ActiveView = v3;

                if (v3 != null)                                     // подрезка по bbox элемента
                {
                    var bb = el.get_BoundingBox(v3) ?? el.get_BoundingBox(null);
                    if (bb != null)
                    {
                        const double pad = 6.0; // футы (~1.8 м): запас, чтобы было видно ближайшее окружение
                        var sb = new BoundingBoxXYZ
                        {
                            Min = new XYZ(bb.Min.X - pad, bb.Min.Y - pad, bb.Min.Z - pad),
                            Max = new XYZ(bb.Max.X + pad, bb.Max.Y + pad, bb.Max.Z + pad),
                        };
                        using (var t = new Transaction(doc, "TNovPRO Вопросы: подрезка вида"))
                        {
                            t.Start();
                            v3.IsSectionBoxActive = true;           // НЕ изоляция — именно section box
                            v3.SetSectionBox(sb);
                            t.Commit();
                        }
                    }
                }

                uidoc.Selection.SetElementIds(new List<ElementId> { id });
                uidoc.ShowElements(id);
            });
        }

        /// <summary>
        /// Собрать 3D-фрагмент (.glb) вокруг элемента замечания в API-контексте и вернуть байты колбэком.
        /// ids = выделенные элементы замечания; в .glb они выгружаются вместе с ближайшим окружением
        /// (соседями). Best-effort: при любой ошибке/отсутствии геометрии вернёт null, создание
        /// замечания от этого не зависит.
        /// </summary>
        public static void ExportGeometryGlb(IList<long> ids, Action<byte[]> onResult)
        {
            Enqueue(app =>
            {
                byte[] glb = null;
                try
                {
                    var uidoc = app.ActiveUIDocument;
                    if (uidoc != null && ids != null && ids.Count > 0)
                        glb = GeometryExporter.Export(uidoc.Document, ids)?.Glb;
                }
                catch { glb = null; } // геометрия не критична — молча отдаём null
                onResult?.Invoke(glb);
            });
        }

        private static View3D EnsureView3D(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            if (uidoc.ActiveView is View3D av && !av.IsTemplate) return av;

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);
            if (existing != null) return existing;

            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft == null) return null;

            using (var t = new Transaction(doc, "TNovPRO Вопросы: создать 3D-вид"))
            {
                t.Start();
                var nv = View3D.CreateIsometric(doc, vft.Id);
                t.Commit();
                return nv;
            }
        }

        /// <summary>Прочитать выделенные сейчас ElementId (для добавления в вопрос).</summary>
        public static void ReadSelection(Action<List<long>> onResult)
        {
            Enqueue(app =>
            {
                var uidoc = app.ActiveUIDocument;
                var result = new List<long>();
                if (uidoc != null)
                    foreach (var id in uidoc.Selection.GetElementIds())
#if R2027
                        result.Add(id.Value); // Revit 2024+: 64-битный ElementId
#else
                        result.Add(id.IntegerValue);
#endif
                onResult?.Invoke(result);
            });
        }

        /// <summary>Прочитать нормализованное имя текущей модели (C3) в API-контексте.</summary>
        public static void ReadCurrentModelName(Action<string, bool> onResult)
        {
            Enqueue(app =>
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) { onResult?.Invoke(null, false); return; }
                var name = ModelNameResolver.Resolve(uidoc.Document, app.Application, out bool detached);
                onResult?.Invoke(name, detached);
            });
        }
    }
}
