using System;
using System.Windows.Forms;
using System.IO;
using System.Linq;
using TNovCommon;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace TNovUtils
{
    public class TreeBuilder
    {
        public static ObservableCollection<Node> BuildTree(IEnumerable<string> paths, IEnumerable<string> existingModels)
        {
            var root = new Node() { Text = "ROOT" };

            foreach (var path in paths)
            {
                var normalizedPath = path
                    .Replace('/', '\\')
                    .TrimEnd('\\');

                var parts = normalizedPath.Split('\\');
                AddPathParts(root, parts, path, existingModels);
            }

            return root.Children;
        }

        private static void AddPathParts(Node parent, string[] pathParts, string path, IEnumerable<string> existingModels)
        {
            var current = parent;

            foreach (var part in pathParts)
            {
                var existing = current.Children.FirstOrDefault(n => n.Text == part);

                string text = part;
                bool isModel = false; bool isLocked = false;
                if (part.EndsWith("rvt")) 
                { 
                    isModel = true;
                    if (existingModels.First() != "-----") 
                    {
                        foreach (var eM in existingModels)
                        {
                            if (eM.Contains(part)) {isLocked = true; text += " (вставлено)"; break;}
                        }
                    }
                    
                }

                if (existing == null)
                {
                    existing = new Node() { Text = text, Path = path, IsModel = isModel, IsLocked = isLocked };
                    current.Children.Add(existing);
                }

                current = existing;

                //if (isLocked) Logger.Log(part + " locked", 2); else Logger.Log(part + " no-locked", 2);
            }
        }
    }

}
