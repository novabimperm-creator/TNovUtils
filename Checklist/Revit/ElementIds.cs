using Autodesk.Revit.DB;

namespace TNovUtils.Checklist.Revit
{
    internal static class ElementIds
    {
        public static string ToStringValue(ElementId id)
        {
#if R2022
            return id.IntegerValue.ToString();
#else
            return id.Value.ToString();
#endif
        }

        public static ElementId Parse(string value)
        {
            long n = long.Parse(value.Trim());
#if R2022
            return new ElementId(unchecked((int)n));
#else
            return new ElementId(n);
#endif
        }
    }
}
