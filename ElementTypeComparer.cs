using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TNovUtils
{
    public class ElementTypeComparer : IEqualityComparer<ElementType>
    {
        public bool Equals(ElementType x, ElementType y)
        {
            if (x == y)
                return true;
            return x != null && y != null && x.Id.Equals((object)y.Id);
        }

        public int GetHashCode(ElementType elementType)
        {
            return elementType == null ? 0 : elementType.Id.GetHashCode();
        }
    }
}
