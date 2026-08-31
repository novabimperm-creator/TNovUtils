using System.Windows;
using TNovCommon;
using TNovUtils.Checklist.Checks;

namespace TNovUtils.Checklist.UI
{
    public sealed class BimChecksViewModel : ObservableObject
    {
        public BimCheckStore Store { get; }
        public RelayCommand2 EditCommentCommand { get; }

        public BimChecksViewModel(BimCheckStore store)
        {
            Store = store;
            EditCommentCommand = new RelayCommand2(p => EditComment(p as BimCheckItem));
        }

        private void EditComment(BimCheckItem item)
        {
            if (item == null) return;

            Store.SetBusy(true);
            try
            {
                var window = new BimCommentWindow(item.Title, item.Comment);
                if (Application.Current != null)
                {
                    foreach (Window w in Application.Current.Windows)
                    {
                        if (w is ChecklistWindow)
                        {
                            window.Owner = w;
                            break;
                        }
                    }
                }

                if (window.ShowDialog() == true)
                    item.Comment = window.CommentText;
            }
            finally
            {
                Store.SetBusy(false);
            }
        }
    }
}
