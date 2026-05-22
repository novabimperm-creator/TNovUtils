using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using TNovCommon;

namespace TNovUtils
{
    public class IdSelectionViewModel : INotifyPropertyChanged
    {


        private string _elemids = "111";
        public string elemids
        {
            get => _elemids;
            set
            {
                _elemids = value;
                OnPropertyChanged();
            }
        }
        private bool _cut = true;
        public bool cut
        {
            get => _cut;
            set
            {
                _cut = value;
                OnPropertyChanged();
            }
        }
        private bool _isolate = true;
        public bool isolate
        {
            get => _isolate;
            set
            {
                _isolate = value;
                OnPropertyChanged();
            }
        }
        public RelayCommand RunCommand { get; set; }
        public IdSelectionViewModel()
        {
            RunCommand = new RelayCommand(param => { RunIdS(); }, CanRun);
        }
        public void RunIdS()
        {
            RaiseCloseRequest();
        }
        private bool CanRun(object param)
        {
            string ids = elemids.Replace(" ", ""); ids = ids.Replace(";", ","); ids = ids.Replace(".", ","); ids = ids.Replace(",", "");
            return double.TryParse(ids, out _);
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
