using Autodesk.Revit.Attributes;
using CleanLinks.Core;

namespace CleanLinks.Commands
{
    /// <summary>
    /// Кнопка «Арматура»: гасит арматурные наборы во всех связях сразу, без окна выбора —
    /// только подтверждение с перечнем наборов, которые закроются.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DisableRebarCommand : DisableFixedCategoryCommand
    {
        protected override WorksetCategory Category => WorksetCategories.Rebar;
    }
}
