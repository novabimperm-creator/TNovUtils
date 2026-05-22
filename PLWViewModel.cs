using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TNovUtils
{
    public class PLWViewModel : INotifyPropertyChanged
    {
        private bool _pin = true;
        public bool pin
        {
            get => _pin; set { _pin = value; OnPropertyChanged(); }
        }
        private bool _pinUpdater = true;
        public bool pinUpdater
        {
            get => _pinUpdater; set { _pinUpdater = value; OnPropertyChanged(); }
        }
        private bool _levels = true;
        public bool levels
        {
            get => _levels; set { _levels = value; OnPropertyChanged(); }
        }
        private bool _levelsUpdater = true;
        public bool levelsUpdater
        {
            get => _levelsUpdater; set { _levelsUpdater = value; OnPropertyChanged(); }
        }
        private bool _worksets = true;
        public bool worksets
        {
            get => _worksets; set { _worksets = value; OnPropertyChanged(); }
        }
        private bool _worksetsUpdater = true;
        public bool worksetsUpdater
        {
            get => _worksetsUpdater; set { _worksetsUpdater = value; OnPropertyChanged(); }
        }
        private bool _show = false;
        public bool show
        {
            get => _show; set { _show = value; OnPropertyChanged(); }
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
