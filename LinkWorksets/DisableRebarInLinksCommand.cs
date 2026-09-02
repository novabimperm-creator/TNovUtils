using Autodesk.Revit.Attributes;

namespace TNovUtils.LinkWorksets
{
    /// <summary>
    /// Гасит арматуру во всех RVT-связях сразу — закрывает арматурные рабочие наборы, поэтому
    /// арматура исчезает во всех видах, включая 3D и разрезы. Окна выбора нет, только подтверждение.
    /// Кнопка ленты объявляется в TNov/Application.cs:
    ///   new PushButtonData(nameof(DisableRebarInLinksCommand), "Арматура",
    ///       typeof(DisableRebarInLinksCommand).Assembly.Location, typeof(DisableRebarInLinksCommand).FullName)
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class DisableRebarInLinksCommand : DisableWorksetGroupCommand
    {
        protected override WorksetGroup Group => WorksetGroup.Rebar;

        protected override string CommandName => "Арматура в связях";
    }
}
