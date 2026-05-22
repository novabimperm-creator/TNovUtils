using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using TNovCommon;

namespace TNovUtils
{
    public class TypeFilterCategoryViewModel : INotifyPropertyChanged
    {
        public Category Category;
        private string name;
        private bool isSelected;
        private List<TypeFilterElementTypeViewModel> elementTypes;

        public ICollectionView ElementTypesView { get; private set; }

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
                if (this.ElementTypesView != null && this.ElementTypes != null)
                {
                    foreach (TypeFilterElementTypeViewModel elementTypeViewModel in this.ElementTypesView.Filter == null ? (IEnumerable<TypeFilterElementTypeViewModel>)this.ElementTypes : this.ElementTypesView.Cast<TypeFilterElementTypeViewModel>())
                        elementTypeViewModel.IsSelected = this.isSelected;
                }
                else if (this.ElementTypes != null)
                {
                    foreach (TypeFilterElementTypeViewModel elementType in this.ElementTypes)
                        elementType.IsSelected = this.isSelected;
                }
            }
        }

        public List<TypeFilterElementTypeViewModel> ElementTypes
        {
            get => this.elementTypes;
            set
            {
                if (this.elementTypes == value)
                    return;
                this.elementTypes = value ?? new List<TypeFilterElementTypeViewModel>();
                this.InitChildView();
                this.OnPropertyChanged(nameof(ElementTypes));
            }
        }

        private void InitChildView()
        {
            this.ElementTypesView = CollectionViewSource.GetDefaultView((object)this.ElementTypes);
            this.ElementTypesView.Filter = (Predicate<object>)null;
        }

        public void SetFilter(string term)
        {
            if (this.ElementTypesView == null)
                this.InitChildView();
            this.ElementTypesView.Filter = !string.IsNullOrWhiteSpace(term) ? (Predicate<object>)(o =>
            {
                string name = ((TypeFilterElementTypeViewModel)o).Name;
                return name != null && name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
            }) : (Predicate<object>)null;
            this.ElementTypesView.Refresh();
            this.OnPropertyChanged("HasAnyVisible");
        }

        public bool HasAnyVisible
        {
            get => this.ElementTypesView != null && this.ElementTypesView.Cast<object>().Any<object>();
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
