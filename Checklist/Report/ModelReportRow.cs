#nullable disable
using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace TNovUtils.Checklist.Report
{
    /// <summary>Сводка по одному параметру проверок (Auto или BIM).</summary>
    public sealed class ChecksSummary
    {
        public int Total { get; set; }
        public List<string> Failed { get; } = new List<string>();
        public List<string> Stale { get; } = new List<string>();
        public ReportLevel Level { get; set; } = ReportLevel.NA;
        public string Error { get; set; }

        public double FailedPct => Total > 0 ? 100.0 * Failed.Count / Total : 0;
        public double StalePct => Total > 0 ? 100.0 * Stale.Count / Total : 0;

        public string LevelText => ReportLevelRules.Text(Level);
        public Brush Brush => ReportLevelBrushes.Of(Level);

        /// <summary>Короткая подпись для ячейки: «не пройд. 40% · устар. 20%».</summary>
        public string ShortText =>
            Error != null ? "ошибка чтения"
            : Total == 0 ? "нет проверок"
            : $"не пройд. {FailedPct:0}% · устар. {StalePct:0}%";

        public string FailedText => Failed.Count == 0 ? "—" : string.Join("; ", Failed);
        public string StaleText => Stale.Count == 0 ? "—" : string.Join("; ", Stale);
    }

    public sealed class NwcSummary
    {
        public ReportLevel Level { get; set; } = ReportLevel.NA;
        public string NwcPath { get; set; }
        public DateTime? NwcDate { get; set; }
        public double? LagDays { get; set; }
        public string Note { get; set; }

        public string LevelText => ReportLevelRules.Text(Level);
        public Brush Brush => ReportLevelBrushes.Of(Level);

        public string ShortText
        {
            get
            {
                if (!string.IsNullOrEmpty(Note)) return Note;
                if (LagDays == null) return "—";
                if (LagDays.Value <= 0) return "актуален";
                if (LagDays.Value < 1) return "отстаёт < 1 дн.";
                return $"отстаёт {Math.Floor(LagDays.Value):0} дн.";
            }
        }

        public string DateText => NwcDate?.ToString("dd.MM.yyyy HH:mm") ?? "—";
    }

    public sealed class ModelReportRow
    {
        public string ModelName { get; set; }
        public DateTime LastChanged { get; set; }
        public string LastUser { get; set; }
        public int SyncCount { get; set; }

        public ChecksSummary Auto { get; set; } = new ChecksSummary();
        public ChecksSummary Bim { get; set; } = new ChecksSummary();
        public NwcSummary Nwc { get; set; } = new NwcSummary();

        public string LastChangedText => LastChanged.ToString("dd.MM.yyyy HH:mm");
    }
}
