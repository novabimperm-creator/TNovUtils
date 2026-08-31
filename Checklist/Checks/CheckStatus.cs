using System;
using System.Windows.Media;
using TNovCommon;

namespace TNovUtils.Checklist.Checks
{
    public enum CheckStatus
    {
        Outdated,
        Passed,
        Failed
    }

    public static class CheckStatusRules
    {
        public const int StaleCalendarDays = 7;

        public static CheckStatus FromItem(AutoCheckItem item)
        {
            // Журнал создаёт «базовые» пункты без title — проверка ещё не запускалась.
            if (item == null || string.IsNullOrWhiteSpace(item.Title))
                return CheckStatus.Outdated;

            if ((DateTime.Today - item.CreationDate.Date).TotalDays >= StaleCalendarDays)
                return CheckStatus.Outdated;

            return item.IsChecked ? CheckStatus.Passed : CheckStatus.Failed;
        }

        public static string Text(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Passed: return "Пройдена";
                case CheckStatus.Failed: return "Не пройдена";
                default: return "Устарела";
            }
        }
    }

    public static class CheckStatusBrushes
    {
        public static readonly SolidColorBrush Passed = Freeze(0x37, 0xC8, 0x71);
        public static readonly SolidColorBrush Failed = Freeze(0xE0, 0x5A, 0x5A);
        public static readonly SolidColorBrush Outdated = Freeze(0xC9, 0xA2, 0x27);

        public static SolidColorBrush Of(CheckStatus status)
        {
            switch (status)
            {
                case CheckStatus.Passed: return Passed;
                case CheckStatus.Failed: return Failed;
                default: return Outdated;
            }
        }

        private static SolidColorBrush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
    }
}
