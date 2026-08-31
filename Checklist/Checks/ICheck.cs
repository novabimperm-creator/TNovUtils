using System;
using System.ComponentModel;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace TNovUtils.Checklist.Checks
{
    /// <summary>
    /// Автопроверка в сайдбаре. Новые проверки — реализация + запись в CheckRegistry.
    /// </summary>
    public interface ICheck : INotifyPropertyChanged
    {
        string Id { get; }
        string Title { get; }
        int Number { get; }
        CheckStatus Status { get; }
        string StatusText { get; }
        DateTime? LastRunAt { get; }
        string LastRunBy { get; }
        string DisplayDate { get; }

        CheckRunResult Run(Document doc);
        UserControl CreateView();
    }
}
