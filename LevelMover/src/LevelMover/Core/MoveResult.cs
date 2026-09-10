using Autodesk.Revit.DB;

namespace LevelMover.Core
{
    /// <summary>Что стало с одним элементом. Пропуск всегда назван причиной — молча не теряем ничего.</summary>
    public class MoveResult
    {
        public MoveResult(ElementId id, string description)
        {
            Id = id;
            Description = description;
        }

        public ElementId Id { get; }

        /// <summary>Категория, имя типа и ID — по этой строке элемент находят в проекте.</summary>
        public string Description { get; }

        public bool Moved { get; set; }

        /// <summary>Почему элемент остался на прежнем уровне. Пусто у перенесённых.</summary>
        public string Problem { get; set; }
    }
}
