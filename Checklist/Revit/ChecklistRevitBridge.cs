using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using TNovCommon;

namespace TNovUtils.Checklist.Revit
{
    /// <summary>
    /// Мост «немодальное окно → Revit API». Действия из WPF ставятся в очередь ExternalEvent.
    /// </summary>
    public sealed class ChecklistActionHandler : IExternalEventHandler
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
                    new InfoWindow280("Ошибка Revit-операции: " + ex.Message).ShowDialog();
                }
            }
        }

        public string GetName() => "TNov Чек-лист — Revit Action";
    }

    public static class ChecklistRevitBridge
    {
        private static ChecklistActionHandler _handler;

        public static void Initialize()
        {
            if (_handler != null) return;
            _handler = new ChecklistActionHandler();
            _handler.Event = ExternalEvent.Create(_handler);
        }

        public static void Enqueue(Action<UIApplication> action)
        {
            if (_handler == null)
                throw new InvalidOperationException("ChecklistRevitBridge не инициализирован");
            _handler.Enqueue(action);
        }

        public static void SelectElements(IEnumerable<string> idStrings)
        {
            Enqueue(app =>
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null || idStrings == null) return;

                var ids = new List<Autodesk.Revit.DB.ElementId>();
                foreach (var s in idStrings)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    ids.Add(ElementIds.Parse(s));
                }
                uidoc.Selection.SetElementIds(ids);
            });
        }
    }
}
