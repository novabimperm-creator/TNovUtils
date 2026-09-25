using Autodesk.Revit.DB;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Shell;
using System.Windows.Threading;
using TNovCommon;

namespace TNovUtils
{
    /// <summary>
    /// Логика взаимодействия для TypeFilterWPF.xaml. Немодальное окно: действия выполняются через TypeFilterRevitBridge.
    /// </summary>
    public partial class TypeFilterWPF : Window, IComponentConnector
    {
        private const string NoParameterText = "Параметр не выбран";
        private const string NoCategoryText = "Выберите категорию";
        private const double CollapsedOpacity = 0.55;

        private TypeFilterData _data;
        private List<TypeFilterCategoryViewModel> _allCategories = new List<TypeFilterCategoryViewModel>();
        private ICollectionView _categoriesView;
        private string _currentSearch = string.Empty;
        /// <summary>Выбранный параметр; null — режим типов.</summary>
        private TypeFilterParameterInfo _parameter;
        private bool _suppressParameterChange;
        private bool _categorySelectionPending;
        private int _valuesRequest;
        private bool _closed;

        private bool _isCollapsed;
        private double _restoreHeight;
        private double _restoreWidth;
        private double _restoreMinHeight;
        private double _restoreMinWidth;
        private Thickness _restoreRootMargin;

        public TypeFilterWPF(TypeFilterData data)
        {
            this.InitializeComponent();
            this.Closed += (s, e) => this._closed = true;
            this.Load(data);
        }

        /// <summary>Показать данные вида (при открытии, по кнопке «Обновить» и при повторном запуске команды).</summary>
        public void Load(TypeFilterData data)
        {
            foreach (TypeFilterCategoryViewModel category in this._allCategories)
                category.PropertyChanged -= this.Category_PropertyChanged;

            this._data = data;
            this._allCategories = data?.Categories ?? new List<TypeFilterCategoryViewModel>();
            this._parameter = null;
            ++this._valuesRequest;
            foreach (TypeFilterCategoryViewModel category in this._allCategories)
            {
                category.ElementTypes = category.ElementTypes ?? new List<TypeFilterElementTypeViewModel>();
                category.PropertyChanged += this.Category_PropertyChanged;
            }
            this.SubTitle.Text = data?.ViewName ?? string.Empty;
            this._categoriesView = CollectionViewSource.GetDefaultView((object)this._allCategories);
            this._categoriesView.Filter = new Predicate<object>(this.CategoryFilter);
            this.categoryTreeView.ItemsSource = (IEnumerable)this._categoriesView;
            this.UpdateParameterOptions();
            this.UpdateModeTexts();
        }

        private bool CategoryFilter(object obj)
        {
            if (!(obj is TypeFilterCategoryViewModel categoryViewModel))
                return false;
            // В режиме параметра показываем только выбранные категории — у них есть список значений.
            if (this._parameter != null && !categoryViewModel.IsParameterMode)
                return false;
            categoryViewModel.SetFilter(this._currentSearch);
            return categoryViewModel.HasAnyVisible;
        }

        private void RefreshTree()
        {
            this._categoriesView.Refresh();
            this.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (string.IsNullOrWhiteSpace(this._currentSearch) && this._parameter == null)
                    this.CollapseAll();
                else
                    this.ExpandAllVisible();
            }), DispatcherPriority.Background);
        }

        private void tbSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            this._currentSearch = (sender is TextBox textBox ? textBox.Text : (string)null) ?? string.Empty;
            this.RefreshTree();
        }

        #region Параметр

        private void Category_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(TypeFilterCategoryViewModel.IsSelected) || this._categorySelectionPending)
                return;
            // Откладываем: изменение приходит из привязки флажка внутри дерева, которое будем перестраивать.
            this._categorySelectionPending = true;
            this.Dispatcher.BeginInvoke((Action)(() =>
            {
                this._categorySelectionPending = false;
                if (this._closed)
                    return;
                // Список общих параметров меняется — выбранный параметр сбрасываем.
                bool wasParameterMode = this._parameter != null;
                this.SetParameter(null);
                this.UpdateParameterOptions();
                if (wasParameterMode)
                    this.RefreshTree();
            }), DispatcherPriority.Background);
        }

        /// <summary>Параметры, которые есть у всех выбранных категорий.</summary>
        private void UpdateParameterOptions()
        {
            List<TypeFilterCategoryViewModel> selected = this._allCategories.Where(c => c.IsSelected).ToList();
            var options = new List<TypeFilterParameterInfo>();
            if (selected.Count == 0)
            {
                options.Add(TypeFilterParameterInfo.Placeholder(NoCategoryText));
            }
            else
            {
                options.Add(TypeFilterParameterInfo.Placeholder(NoParameterText));
                IEnumerable<ElementId> common = selected[0].Parameters.Keys;
                foreach (TypeFilterCategoryViewModel category in selected.Skip(1))
                    common = common.Where(id => category.Parameters.ContainsKey(id));
                List<TypeFilterParameterInfo> parameters = common.Select(id => selected[0].Parameters[id]).ToList();
                // Одноименные параметры (экземпляра и типа, или разные общие) различаем подписью.
                foreach (var sameName in parameters.GroupBy(p => p.Name).Where(g => g.Count() > 1))
                {
                    foreach (TypeFilterParameterInfo info in sameName)
                        info.DisplayName = info.Name + (info.IsTypeParameter ? " (тип)" : " (экземпляр)");
                    foreach (var stillSame in sameName.GroupBy(p => p.DisplayName).Where(g => g.Count() > 1))
                        foreach (TypeFilterParameterInfo info in stillSame)
                            info.DisplayName += " [" + TypeFilterActions.IdValue(info.Id) + "]";
                }
                options.AddRange(parameters.OrderBy(p => p.DisplayName, new AlphanumComparatorFastString()));
            }

            this._suppressParameterChange = true;
            this.cbParameter.ItemsSource = options;
            this.cbParameter.SelectedIndex = 0;
            this.cbParameter.IsEnabled = selected.Count > 0;
            this.cbParameter.Opacity = selected.Count > 0 ? 1 : 0.6;
            this._suppressParameterChange = false;
        }

        private void cbParameter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this._suppressParameterChange)
                return;
            TypeFilterParameterInfo info = this.cbParameter.SelectedItem as TypeFilterParameterInfo;
            if (info?.Id == null)
            {
                this.SetParameter(null);
                this.RefreshTree();
                return;
            }
            this.LoadParameterValues(info);
        }

        private void SetParameter(TypeFilterParameterInfo info)
        {
            ++this._valuesRequest;
            this._parameter = info;
            if (info == null)
            {
                foreach (TypeFilterCategoryViewModel category in this._allCategories)
                    category.ParameterValues = null;
                this.cbParameter.IsEnabled = this._allCategories.Any(c => c.IsSelected);
                this.Cursor = null;
            }
            this.UpdateModeTexts();
        }

        /// <summary>Значения читаются в контексте Revit API; окно ждет ответа без блокировки.</summary>
        private void LoadParameterValues(TypeFilterParameterInfo info)
        {
            TypeFilterData data = this._data;
            List<TypeFilterCategoryViewModel> categories = this._allCategories.Where(c => c.IsSelected).ToList();
            int request = ++this._valuesRequest;
            this.cbParameter.IsEnabled = false;
            this.Cursor = Cursors.Wait;
            Logger.Log("Значения параметра «" + info.Name + "»", 1);

            TypeFilterRevitBridge.Enqueue(app =>
            {
                Dictionary<TypeFilterCategoryViewModel, List<TypeFilterParameterValueViewModel>> values = null;
                try
                {
                    if (data.Document != null && data.Document.IsValidObject)
                        values = TypeFilterActions.CollectValues(data.Document, categories, info);
                }
                catch (Exception ex)
                {
                    Logger.Log("Ошибка чтения значений параметра: " + ex, 4);
                }
                // ExternalEvent выполняется в UI-потоке Revit — том же, что и у окна.
                if (!this._closed && request == this._valuesRequest)
                    this.ApplyParameterValues(info, values);
            });
        }

        private void ApplyParameterValues(TypeFilterParameterInfo info,
            Dictionary<TypeFilterCategoryViewModel, List<TypeFilterParameterValueViewModel>> values)
        {
            this.Cursor = null;
            this.cbParameter.IsEnabled = true;
            if (values == null)
            {
                new InfoWindow280("Не удалось прочитать значения параметра. Нажмите «Обновить».").ShowDialog();
                this._suppressParameterChange = true;
                this.cbParameter.SelectedIndex = 0;
                this._suppressParameterChange = false;
                this.SetParameter(null);
                this.RefreshTree();
                return;
            }
            this._parameter = info;
            foreach (TypeFilterCategoryViewModel category in this._allCategories)
                category.ParameterValues = values.TryGetValue(category, out var list) ? list : null;
            this.UpdateModeTexts();
            this.RefreshTree();
        }

        private void UpdateModeTexts()
        {
            bool parameterMode = this._parameter != null;
            this.searchPlaceholder.Text = parameterMode ? "Поиск значений..." : "Поиск типов...";
            this.tbSearch.ToolTip = parameterMode ? "Поиск по значению параметра" : "Поиск по имени типа";
            this.btn_CreateFilter.ToolTip = parameterMode
                ? "Фильтр вида по выбранным значениям параметра «" + this._parameter.Name + "»"
                : "Фильтр вида по именам выбранных типов";
        }

        #endregion

        #region Действия

        private List<ITypeFilterItem> SelectedItems()
        {
            if (this._parameter != null)
                return this._allCategories
                    .Where(c => c.IsParameterMode)
                    .SelectMany(c => c.ParameterValues)
                    .Where(v => v.IsSelected)
                    .Cast<ITypeFilterItem>()
                    .ToList();
            return this._allCategories
                .SelectMany(c => (IEnumerable<TypeFilterElementTypeViewModel>)c.ElementTypes ?? Enumerable.Empty<TypeFilterElementTypeViewModel>())
                .Where(t => t.IsSelected)
                .Cast<ITypeFilterItem>()
                .ToList();
        }

        private List<ElementId> SelectedElementIds(out bool any)
        {
            List<ITypeFilterItem> items = this.SelectedItems();
            any = items.Count > 0;
            if (!any)
                new InfoWindow280(this._parameter != null ? "Отметьте значения параметра." : "Отметьте типы или категории.").ShowDialog();
            return items.SelectMany(i => i.ElementIds).Distinct().ToList();
        }

        private void RunOnElements(Action<Autodesk.Revit.UI.UIApplication, TypeFilterData, List<ElementId>> action)
        {
            List<ElementId> ids = this.SelectedElementIds(out bool any);
            if (!any)
                return;
            TypeFilterData data = this._data;
            TypeFilterRevitBridge.Enqueue(app => action(app, data, ids));
        }

        private void btn_Hide_Click(object sender, RoutedEventArgs e) => this.RunOnElements(TypeFilterActions.Hide);

        private void btn_Isolate_Click(object sender, RoutedEventArgs e) => this.RunOnElements(TypeFilterActions.Isolate);

        private void btn_Select_Click(object sender, RoutedEventArgs e) => this.RunOnElements(TypeFilterActions.Select);

        private void btn_CreateFilter_Click(object sender, RoutedEventArgs e)
        {
            List<ITypeFilterItem> items = this.SelectedItems();
            if (items.Count == 0)
            {
                new InfoWindow280(this._parameter != null ? "Отметьте значения параметра." : "Отметьте типы или категории.").ShowDialog();
                return;
            }
            TypeFilterData data = this._data;
            TypeFilterParameterInfo parameter = this._parameter;
            string filterName = this.textBox_FilterName.Text;
            if (parameter != null)
            {
                if (string.IsNullOrWhiteSpace(filterName))
                    filterName = parameter.Name;
                List<TypeFilterParameterValueViewModel> values = items.Cast<TypeFilterParameterValueViewModel>().ToList();
                TypeFilterRevitBridge.Enqueue(app => TypeFilterActions.CreateParameterFilter(app, data, parameter, values, filterName));
            }
            else
            {
                List<TypeFilterElementTypeViewModel> types = items.Cast<TypeFilterElementTypeViewModel>().ToList();
                TypeFilterRevitBridge.Enqueue(app => TypeFilterActions.CreateTypeFilter(app, data, types, filterName));
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            TypeFilterRevitBridge.Enqueue(app =>
            {
                if (this._closed || app.ActiveUIDocument == null)
                    return;
                this.Load(TypeFilterActions.Collect(app.ActiveUIDocument));
                this.RefreshTree();
            });
        }

        private void btn_Ok_Click(object sender, RoutedEventArgs e) => this.Close();

        private void VisibilityFilterWPF_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !this.cbParameter.IsDropDownOpen)
                this.Close();
        }

        #endregion

        #region Дерево

        private void ExpandAllVisible()
        {
            this.categoryTreeView.UpdateLayout();
            foreach (object obj in (IEnumerable)this.categoryTreeView.Items)
            {
                if (this.categoryTreeView.ItemContainerGenerator.ContainerFromItem(obj) is TreeViewItem treeViewItem)
                    this.ExpandRecursive(treeViewItem);
            }
        }

        private void ExpandRecursive(TreeViewItem item)
        {
            item.IsExpanded = true;
            item.UpdateLayout();
            foreach (object obj in (IEnumerable)item.Items)
            {
                if (item.ItemContainerGenerator.ContainerFromItem(obj) is TreeViewItem treeViewItem)
                    this.ExpandRecursive(treeViewItem);
            }
        }

        private void CollapseAll()
        {
            this.categoryTreeView.UpdateLayout();
            foreach (object obj in (IEnumerable)this.categoryTreeView.Items)
            {
                if (this.categoryTreeView.ItemContainerGenerator.ContainerFromItem(obj) is TreeViewItem treeViewItem)
                    treeViewItem.IsExpanded = false;
            }
        }

        #endregion

        #region Окно и мини-панель

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            HelpLinks.ShowHelp("Типофильтр");
        }

        private void CollapseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCollapsed)
                ExpandFromStrip();
            else
                CollapseToStrip();
        }

        private void CollapseToStrip()
        {
            _restoreHeight = ActualHeight;
            _restoreWidth = ActualWidth;
            _restoreMinHeight = MinHeight;
            _restoreMinWidth = MinWidth;
            _restoreRootMargin = RootGrid.Margin;

            ContentHost.Visibility = System.Windows.Visibility.Collapsed;
            ContentRow.Height = new GridLength(0);
            RefreshButton.Visibility = System.Windows.Visibility.Collapsed;
            HelpButton.Visibility = System.Windows.Visibility.Collapsed;
            CloseButton.Visibility = System.Windows.Visibility.Collapsed;
            SubTitle.Visibility = System.Windows.Visibility.Collapsed;
            TitleColumn.Width = GridLength.Auto;
            TitlePanel.Margin = new Thickness(0, 0, 12, 0);
            TitleBar.Margin = new Thickness(0);
            RootGrid.Margin = new Thickness(12);
            CollapseButton.Content = "v";
            CollapseButton.ToolTip = "Развернуть";
            CollapseButton.Margin = new Thickness(0);

            MinHeight = 0;
            MinWidth = 0;
            UpdateLayout();
            SizeToContent = SizeToContent.WidthAndHeight;
            Opacity = IsMouseOver ? 1 : CollapsedOpacity;

            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CaptionHeight = 0;
                chrome.ResizeBorderThickness = new Thickness(0);
            }

            _isCollapsed = true;
        }

        private void ExpandFromStrip()
        {
            SizeToContent = SizeToContent.Manual;
            ContentRow.Height = new GridLength(1, GridUnitType.Star);
            ContentHost.Visibility = System.Windows.Visibility.Visible;
            RefreshButton.Visibility = System.Windows.Visibility.Visible;
            HelpButton.Visibility = System.Windows.Visibility.Visible;
            CloseButton.Visibility = System.Windows.Visibility.Visible;
            SubTitle.Visibility = System.Windows.Visibility.Visible;
            TitleColumn.Width = new GridLength(1, GridUnitType.Star);
            TitlePanel.Margin = new Thickness(0);
            TitleBar.Margin = new Thickness(0, 0, 0, 12);
            RootGrid.Margin = _restoreRootMargin;
            CollapseButton.Content = "^";
            CollapseButton.ToolTip = "Свернуть в полоску";
            CollapseButton.Margin = new Thickness(0, 0, 6, 0);

            MinHeight = _restoreMinHeight;
            MinWidth = _restoreMinWidth;
            Width = _restoreWidth;
            Height = _restoreHeight;
            Opacity = 1;

            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                chrome.CaptionHeight = 30;
                chrome.ResizeBorderThickness = new Thickness(5);
            }

            _isCollapsed = false;
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isCollapsed)
                Opacity = 1;
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isCollapsed)
                Opacity = CollapsedOpacity;
        }

        #endregion
    }
}
