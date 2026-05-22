using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TNovUtils
{
    internal class CategoryComparer : IEqualityComparer<Category>
    {
        public bool Equals(Category x, Category y)
        {
            if (x == y)
                return true;
            return x != null && y != null && x.Id.Equals((object)y.Id);
        }

        public int GetHashCode(Category category) => category == null ? 0 : category.Id.GetHashCode();
    }
}
