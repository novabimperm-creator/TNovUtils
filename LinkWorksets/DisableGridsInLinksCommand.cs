using Autodesk.Revit.Attributes;

namespace TNovUtils.LinkWorksets
{
    /// <summary>
    /// Гасит оси и уровни во всех RVT-связях сразу — закрывает их рабочие наборы, поэтому
    /// оси исчезают во всех видах, включая 3D и разрезы. Окна выбора нет, только подтверждение.
    /// Кнопка ленты объявляется в TNov/Application.cs:
    ///   new PushButtonData(nameof(DisableGridsInLinksCommand), "Оси\nи уровни",
    ///       typeof(DisableGridsInLinksCommand).Assembly.Location, typeof(DisableGridsInLinksCommand).FullName)
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class DisableGridsInLinksCommand : DisableWorksetGroupCommand
    {
        protected override WorksetGroup Group => WorksetGroup.GridsAndLevels;

        protected override string CommandName => "Оси и уровни в связях";

        protected override string Warning =>
            "Если уровни лежат в одном наборе с осями, они скроются вместе с ними.";
    }
}
