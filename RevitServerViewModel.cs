using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using TNovCommon;

namespace TNovUtils
{
    public class Node : INotifyPropertyChanged
    {
        public Node()
        {
            Id = Guid.NewGuid().ToString();
        }

        public ObservableCollection<Node> Children { get; set; } = new ObservableCollection<Node>();
        public Node Parent { get; set; }

        public string Id { get; set; }
        public bool IsModel { get; set; }
        private string _path;
        private string _text;
        private bool _isChecked;
        private bool _isExpanded;
        private bool _isLocked;

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                _isChecked = value;
                foreach (var item in Children)
                {
                    item.IsChecked = value;
                }
                OnPropertyChanged(nameof(IsChecked));
            }
        }
        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                _isLocked = value;
                OnPropertyChanged();
            }
        }

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                OnPropertyChanged();
            }
        }

        public string Path
        {
            get => _path;
            set
            {
                _path = value;
                OnPropertyChanged();
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
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
    public class RevitServerViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<Node> Nodes { get; set; }
        
        public RevitServerViewModel(IEnumerable<string> existingModels)
        {
            /*
            Nodes = new ObservableCollection<Node>();
            string[] files = File.ReadAllLines(nova.novaserver + "_TNov/RS.txt");
            foreach (string file in files)
            {
                Node node = new Node()
                {
                    Text = file,
                    IsModel = true,
                };
                Nodes.Add(node);
            }
            */
            List<string> filePaths = File.ReadAllLines(nova.novaserver + "_TNov/RS.txt").ToList();
            Nodes = TreeBuilder.BuildTree(filePaths,existingModels);
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
