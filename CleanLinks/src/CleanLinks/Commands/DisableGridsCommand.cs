using Autodesk.Revit.Attributes;
using CleanLinks.Core;

namespace CleanLinks.Commands
{
    /// <summary>
    /// Кнопка «Оси и уровни»: гасит их во всех связях сразу, без окна выбора — только
    /// подтверждение с перечнем наборов, которые закроются.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DisableGridsCommand : DisableFixedCategoryCommand
    {
        protected override WorksetCategory Category => WorksetCategories.Grids;

        protected override string Warning =>
            "Если уровни лежат в одном наборе с осями, они скроются вместе с ними.";
    }
}
