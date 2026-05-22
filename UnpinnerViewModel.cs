using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TNovUtils
{
    public class UnpinnerViewModel : INotifyPropertyChanged
    {

        private bool _grids = true;
        public bool grids
        {
            get => _grids;
            set
            {
                _grids = value;
                OnPropertyChanged();
            }
        }
        private bool _levels = false;
        public bool levels
        {
            get => _levels;
            set
            {
                _levels = value;
                OnPropertyChanged();
            }
        }
        private bool _links = false;
        public bool links
        {
            get => _links;
            set
            {
                _links = value;
                OnPropertyChanged();
            }
        }
        public event EventHandler CloseRequest;
        private void RaiseCloseRequest()
        {
            CloseRequest?.Invoke(this, EventArgs.Empty);
        }
        public event PropertyChangedEventHandler PropertyChanged;

        void OnPropertyChanged([CallerMemberName] string PropertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
        }

    }
}
