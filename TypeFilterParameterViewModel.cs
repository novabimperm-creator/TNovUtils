using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.ComponentModel;

namespace TNovUtils
{
    /// <summary>
    /// Строка дерева Типофильтра: тип элемента или значение параметра.
    /// </summary>
    public interface ITypeFilterItem
    {
        string Name { get; }
        string SearchText { get; }
        bool IsSelected { get; set; }
        List<ElementId> ElementIds { get; }
    }

    /// <summary>
    /// Параметр в выпадающем списке. Id == null — служебная строка («Параметр не выбран» и т.п.).
    /// </summary>
    public class TypeFilterParameterInfo
    {
        public ElementId Id;
        public BuiltInParameter BuiltIn = BuiltInParameter.INVALID;
        public string Name;
        public bool IsTypeParameter;
        public string DisplayName { get; set; }

        public static TypeFilterParameterInfo Placeholder(string text) =>
            new TypeFilterParameterInfo { Name = text, DisplayName = text };
    }

    /// <summary>
    /// Значение выбранного параметра внутри категории: «220 (22 шт.)».
    /// </summary>
    public class TypeFilterParameterValueViewModel : ITypeFilterItem, INotifyPropertyChanged
    {
        public const string EmptyValue = "(пусто)";
        public const string MissingValue = "(нет параметра)";

        private bool isSelected;

        public TypeFilterCategoryViewModel Category;
        public string Value;
        /// <summary>Параметр есть, но значение не задано.</summary>
        public bool IsEmpty;
        /// <summary>У элемента (и его типа) нет такого параметра — фильтром не скрыть.</summary>
        public bool IsMissing;
        public StorageType StorageType = StorageType.None;
        public HashSet<string> Strings = new HashSet<string>();
        public HashSet<int> Integers = new HashSet<int>();
        public HashSet<ElementId> Ids = new HashSet<ElementId>();
        public double Min = double.MaxValue;
        public double Max = double.MinValue;

        public List<ElementId> ElementIds { get; } = new List<ElementId>();

        public string Name => string.Format("{0} ({1} шт.)", this.Value, this.ElementIds.Count);

        public string SearchText => this.Value;

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
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
