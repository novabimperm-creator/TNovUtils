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
        /// <summary>Все элементы категории на виде.</summary>
        public List<ElementId> ElementIds = new List<ElementId>();
        /// <summary>Параметры, найденные у элементов категории (экземпляра и типа).</summary>
        public Dictionary<ElementId, TypeFilterParameterInfo> Parameters = new Dictionary<ElementId, TypeFilterParameterInfo>();
        private string name;
        private bool isSelected;
        private List<TypeFilterElementTypeViewModel> elementTypes;
        private List<TypeFilterParameterValueViewModel> parameterValues;

        /// <summary>Дочерние строки дерева: типы или значения выбранного параметра.</summary>
        public ICollectionView ChildrenView { get; private set; }

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
                IEnumerable<ITypeFilterItem> items = this.ChildrenView != null && this.ChildrenView.Filter != null
                    ? this.ChildrenView.Cast<ITypeFilterItem>().ToList()
                    : this.Items;
                foreach (ITypeFilterItem item in items)
                    item.IsSelected = this.isSelected;
                // Выбор категории в режиме параметров — это и выбор всех её типов.
                if (this.IsParameterMode && this.ElementTypes != null)
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

        /// <summary>Значения выбранного параметра; null — режим типов.</summary>
        public List<TypeFilterParameterValueViewModel> ParameterValues
        {
            get => this.parameterValues;
            set
            {
                if (this.parameterValues == value)
                    return;
                this.parameterValues = value;
                this.InitChildView();
                this.OnPropertyChanged(nameof(ParameterValues));
            }
        }

        public bool IsParameterMode => this.parameterValues != null;

        public IEnumerable<ITypeFilterItem> Items =>
            this.IsParameterMode
                ? this.parameterValues.Cast<ITypeFilterItem>()
                : (IEnumerable<ITypeFilterItem>)this.elementTypes ?? Enumerable.Empty<ITypeFilterItem>();

        private void InitChildView()
        {
            object source = this.IsParameterMode ? (object)this.parameterValues : this.elementTypes;
            this.ChildrenView = source == null ? null : CollectionViewSource.GetDefaultView(source);
            if (this.ChildrenView != null)
                this.ChildrenView.Filter = (Predicate<object>)null;
            this.OnPropertyChanged(nameof(ChildrenView));
        }

        public void SetFilter(string term)
        {
            if (this.ChildrenView == null)
                this.InitChildView();
            if (this.ChildrenView == null)
                return;
            this.ChildrenView.Filter = !string.IsNullOrWhiteSpace(term) ? (Predicate<object>)(o =>
            {
                string text = ((ITypeFilterItem)o).SearchText;
                return text != null && text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
            }) : (Predicate<object>)null;
            this.ChildrenView.Refresh();
            this.OnPropertyChanged("HasAnyVisible");
        }

        public bool HasAnyVisible
        {
            get => this.ChildrenView != null && this.ChildrenView.Cast<object>().Any<object>();
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
