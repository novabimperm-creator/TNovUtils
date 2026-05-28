using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

namespace TNovUtils
{
    /// <summary>
    /// Логика взаимодействия для TypeFilterWPF.xaml
    /// </summary>
    public partial class TypeFilterWPF : Window, IComponentConnector, IStyleConnector
    {
        public string BTNName;
        public string FilterName;
        public List<TypeFilterElementTypeViewModel> SelectedElementTypes;
        private readonly List<TypeFilterCategoryViewModel> _allCategories;
        private readonly ICollectionView _categoriesView;
        private string _currentSearch = string.Empty;

        public TypeFilterWPF(List<TypeFilterCategoryViewModel> categories)
        {
            this.InitializeComponent();
            this._allCategories = categories ?? new List<TypeFilterCategoryViewModel>();
            foreach (TypeFilterCategoryViewModel allCategory in this._allCategories)
                allCategory.ElementTypes = allCategory.ElementTypes ?? new List<TypeFilterElementTypeViewModel>();
            this._categoriesView = CollectionViewSource.GetDefaultView((object)this._allCategories);
            this._categoriesView.Filter = new Predicate<object>(this.CategoryFilter);
            this.categoryTreeView.ItemsSource = (IEnumerable)this._categoriesView;
        }

        private bool CategoryFilter(object obj)
        {
            if (!(obj is TypeFilterCategoryViewModel categoryViewModel))
                return false;
            categoryViewModel.SetFilter(this._currentSearch);
            return categoryViewModel.HasAnyVisible;
        }

        private void tbSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            this._currentSearch = (sender is TextBox textBox ? textBox.Text : (string)null) ?? string.Empty;
            this._categoriesView.Refresh();
            this.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (string.IsNullOrWhiteSpace(this._currentSearch))
                    this.CollapseAll();
                else
                    this.ExpandAllVisible();
            }), DispatcherPriority.Background);
        }

        private void CategoryCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox checkBox))
                return;

            var dataContext = checkBox.DataContext as TypeFilterCategoryViewModel;
            if (dataContext == null)
                return;

            IEnumerable<TypeFilterElementTypeViewModel> items;

            if (!string.IsNullOrWhiteSpace(this._currentSearch) && dataContext.ElementTypesView?.Filter != null)
            {
                items = dataContext.ElementTypesView
                    .Cast<TypeFilterElementTypeViewModel>()
                    .ToList();
            }
            else
            {
                items = dataContext.ElementTypes ?? Enumerable.Empty<TypeFilterElementTypeViewModel>();
            }

            foreach (var elementTypeViewModel in items)
            {
                elementTypeViewModel.IsSelected = dataContext.IsSelected;
            }
        }

        private void btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            this.BTNName = "btn_Ok";
            this.DialogResult = new bool?(true);
            this.Close();
        }

        private void VisibilityFilterWPF_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return || e.Key == Key.Space)
            {
                this.BTNName = "btn_Ok";
                this.DialogResult = new bool?(true);
                this.Close();
            }
            else
            {
                if (e.Key != Key.Escape)
                    return;
                this.DialogResult = new bool?(false);
                this.Close();
            }
        }

        private void GetSelectedElementTypes()
        {
            this.SelectedElementTypes = this._allCategories
                .SelectMany<TypeFilterCategoryViewModel, TypeFilterElementTypeViewModel>((Func<TypeFilterCategoryViewModel, IEnumerable<TypeFilterElementTypeViewModel>>)(c => (IEnumerable<TypeFilterElementTypeViewModel>)c.ElementTypes ?? Enumerable.Empty<TypeFilterElementTypeViewModel>()))
                .Where<TypeFilterElementTypeViewModel>((Func<TypeFilterElementTypeViewModel, bool>)(t => t.IsSelected))
                .ToList<TypeFilterElementTypeViewModel>();
        }

        private void btn_Hide_Click(object sender, RoutedEventArgs e)
        {
            this.BTNName = "btn_Hide";
            this.GetSelectedElementTypes();
            this.DialogResult = new bool?(true);
            this.Close();
        }

        private void btn_Isolate_Click(object sender, RoutedEventArgs e)
        {
            this.BTNName = "btn_Isolate";
            this.GetSelectedElementTypes();
            this.DialogResult = new bool?(true);
            this.Close();
        }

        private void btn_Select_Click(object sender, RoutedEventArgs e)
        {
            this.BTNName = "btn_Select";
            this.GetSelectedElementTypes();
            this.DialogResult = new bool?(true);
            this.Close();
        }

        private void btn_CreateFilter_Click(object sender, RoutedEventArgs e)
        {
            this.BTNName = "btn_CreateFilter";
            this.FilterName = this.textBox_FilterName.Text;
            this.GetSelectedElementTypes();
            this.DialogResult = new bool?(true);
            this.Close();
        }

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

        void IStyleConnector.Connect(int connectionId, object target)
        {
            if (connectionId != 4)
                return;
            ((ToggleButton)target).Checked += new RoutedEventHandler(this.CategoryCheckBox_Checked);
            ((ToggleButton)target).Unchecked += new RoutedEventHandler(this.CategoryCheckBox_Checked);
        }

        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {

        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            string commandText = @"https://portal.talan.group/knowledge/proektirovanie/tipofiltr/";
            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = commandText;
            proc.StartInfo.UseShellExecute = true;
            proc.Start();
        }
    }
}
