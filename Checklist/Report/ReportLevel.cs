#nullable disable
using System.Windows.Media;

namespace TNovUtils.Checklist.Report
{
    /// <summary>
    /// Уровень по параметру отчёта (Auto / BIM / NWC). Порядок — от лучшего к худшему,
    /// NA — нет данных (проект вне CDE, статус stop).
    /// </summary>
    public enum ReportLevel
    {
        NA,
        Ideal,
        Norm,
        Attention,
        High
    }

    public static class ReportLevelRules
    {
        // Каскад: уровень определяется первым сработавшим порогом сверху.
        public const double HighFailedPct = 70, HighStalePct = 50;
        public const double AttentionFailedPct = 50, AttentionStalePct = 25;

        public const int NwcHighDays = 7;
        public const int NwcAttentionDays = 2;

        public static ReportLevel FromChecks(int total, int failed, int stale)
        {
            if (total <= 0) return ReportLevel.NA;
            double f = 100.0 * failed / total;
            double s = 100.0 * stale / total;
            if (f > HighFailedPct || s > HighStalePct) return ReportLevel.High;
            if (f > AttentionFailedPct || s > AttentionStalePct) return ReportLevel.Attention;
            if (failed > 0 || stale > 0) return ReportLevel.Norm;
            return ReportLevel.Ideal;
        }

        /// <summary>lagDays — отставание NWC от последней не-BIM синхронизации (≥0).</summary>
        public static ReportLevel FromNwcLag(double lagDays)
        {
            if (lagDays >= NwcHighDays) return ReportLevel.High;
            if (lagDays >= NwcAttentionDays) return ReportLevel.Attention;
            if (lagDays > 0) return ReportLevel.Norm;
            return ReportLevel.Ideal;
        }

        public static string Text(ReportLevel level)
        {
            switch (level)
            {
                case ReportLevel.High: return "Высокий риск";
                case ReportLevel.Attention: return "Повышенное внимание";
                case ReportLevel.Norm: return "Норма";
                case ReportLevel.Ideal: return "Идеал";
                default: return "н/д";
            }
        }

        /// <summary>Цвет заливки в формате RRGGBB — общий для окна и Excel.</summary>
        public static string Hex(ReportLevel level)
        {
            switch (level)
            {
                case ReportLevel.High: return "E05A5A";
                case ReportLevel.Attention: return "C9A227";
                case ReportLevel.Norm: return "7FB88F";
                case ReportLevel.Ideal: return "37C871";
                default: return "6B7A73";
            }
        }
    }

    public static class ReportLevelBrushes
    {
        private static readonly SolidColorBrush High = Make(ReportLevel.High);
        private static readonly SolidColorBrush Attention = Make(ReportLevel.Attention);
        private static readonly SolidColorBrush Norm = Make(ReportLevel.Norm);
        private static readonly SolidColorBrush Ideal = Make(ReportLevel.Ideal);
        private static readonly SolidColorBrush Na = Make(ReportLevel.NA);

        public static SolidColorBrush Of(ReportLevel level)
        {
            switch (level)
            {
                case ReportLevel.High: return High;
                case ReportLevel.Attention: return Attention;
                case ReportLevel.Norm: return Norm;
                case ReportLevel.Ideal: return Ideal;
                default: return Na;
            }
        }

        private static SolidColorBrush Make(ReportLevel level)
        {
            string hex = ReportLevelRules.Hex(level);
            var color = Color.FromRgb(
                System.Convert.ToByte(hex.Substring(0, 2), 16),
                System.Convert.ToByte(hex.Substring(2, 2), 16),
                System.Convert.ToByte(hex.Substring(4, 2), 16));
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
    }
}
