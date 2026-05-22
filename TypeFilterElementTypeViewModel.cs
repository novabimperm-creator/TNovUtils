using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TNovUtils
{
    public class TypeFilterElementTypeViewModel : INotifyPropertyChanged
    {
        public ElementType ElementType;
        private string name;
        private bool isSelected;

        public string Name
        {
            get => this.name;
            set
            {
                if (!(this.name != value))
                    return;
                this.name = value;
                this.OnPropertyChanged(nameof(Name));
            }
        }

        public bool IsSelected
        {
            get => this.isSelected;
            set
            {
                if (this.isSelected == value)
                    return;
                this.isSelected = value;
                this.OnPropertyChanged(nameof(IsSelected));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler propertyChanged = this.PropertyChanged;
            if (propertyChanged == null)
                return;
            propertyChanged((object)this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
