using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using CleanLinks.Core;
using CleanLinks.UI;

namespace CleanLinks.Commands
{
    /// <summary>
    /// Кнопка «Выключить в связях»: закрывает во всех RVT-связях рабочие наборы выбранных
    /// групп — оси и уровни, арматура. Выбор и подтверждение сведены в одно окно, дальше
    /// только ход работы.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DisableWorksetsCommand : DisableWorksetsCommandBase
    {
        protected override List<WorksetCategory> ChooseCategories(List<LinkInfo> links)
        {
            using (var picker = new CategoryPickerForm(links))
            {
                return picker.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    ? picker.Selected
                    : null;
            }
        }
    }
}
