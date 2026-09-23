#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClosedXML.Excel;
using Microsoft.Win32;

namespace TNovUtils.Checklist.Report
{
    /// <summary>
    /// Выгрузка отчёта в .xlsx через ClosedXML (без установленного Excel).
    /// Лист «Свод» — строка на модель, лист «Детали» — строка на каждую проблемную проверку.
    /// </summary>
    public static class ReportExcelExporter
    {
        private static bool _resolverInstalled;

        public static void ExportWithDialog(ReportResult result)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Сохранить отчёт",
                Filter = "Книга Excel (*.xlsx)|*.xlsx",
                FileName = $"BIM-отчет_{result.BuiltAt:yyyy-MM-dd}.xlsx",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true) return;

            EnsureResolverInstalled();
            Write(result, dialog.FileName);
            Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
        }

        // Отдельный метод без инлайна: типы ClosedXML разрешаются только при его JIT,
        // уже после установки обработчика AssemblyResolve.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Write(ReportResult result, string path)
        {
            using (var book = new XLWorkbook())
            {
                WriteSummary(book.AddWorksheet("Свод"), result);
                WriteDetails(book.AddWorksheet("Детали"), result);
                book.SaveAs(path);
            }
        }

        private static void WriteSummary(IXLWorksheet ws, ReportResult result)
        {
            ws.Cell(1, 1).Value = $"Отчёт по чек-листам моделей за {result.Since:dd.MM.yyyy} – {result.BuiltAt:dd.MM.yyyy HH:mm}";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 13;

            string[] headers =
            {
                "Модель", "Изменена", "Пользователь", "Синхр. за период",
                "Auto", "Auto: не пройдено, %", "Auto: устарело, %",
                "BIM", "BIM: не пройдено, %", "BIM: устарело, %",
                "NWC", "NWC: дата файла", "NWC: отставание, дн.", "NWC: примечание"
            };
            const int headerRow = 3;
            for (int c = 0; c < headers.Length; c++)
                ws.Cell(headerRow, c + 1).Value = headers[c];
            var header = ws.Range(headerRow, 1, headerRow, headers.Length);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E2E26");
            header.Style.Font.FontColor = XLColor.White;

            int r = headerRow + 1;
            foreach (var row in result.Rows)
            {
                ws.Cell(r, 1).Value = row.ModelName;
                ws.Cell(r, 2).Value = row.LastChanged;
                ws.Cell(r, 2).Style.DateFormat.Format = "dd.MM.yyyy HH:mm";
                ws.Cell(r, 3).Value = row.LastUser;
                ws.Cell(r, 4).Value = row.SyncCount;

                LevelCell(ws.Cell(r, 5), row.Auto.Level);
                ws.Cell(r, 6).Value = Math.Round(row.Auto.FailedPct);
                ws.Cell(r, 7).Value = Math.Round(row.Auto.StalePct);

                LevelCell(ws.Cell(r, 8), row.Bim.Level);
                ws.Cell(r, 9).Value = Math.Round(row.Bim.FailedPct);
                ws.Cell(r, 10).Value = Math.Round(row.Bim.StalePct);

                LevelCell(ws.Cell(r, 11), row.Nwc.Level);
                if (row.Nwc.NwcDate.HasValue)
                {
                    ws.Cell(r, 12).Value = row.Nwc.NwcDate.Value;
                    ws.Cell(r, 12).Style.DateFormat.Format = "dd.MM.yyyy HH:mm";
                }
                if (row.Nwc.LagDays.HasValue)
                    ws.Cell(r, 13).Value = Math.Round(row.Nwc.LagDays.Value, 1);
                ws.Cell(r, 14).Value = row.Nwc.Note ?? "";
                r++;
            }

            ws.Range(headerRow, 1, Math.Max(headerRow, r - 1), headers.Length).SetAutoFilter();
            ws.SheetView.FreezeRows(headerRow);
            ws.Columns().AdjustToContents(headerRow, r, 10, 60);
        }

        private static void WriteDetails(IXLWorksheet ws, ReportResult result)
        {
            string[] headers = { "Модель", "Параметр", "Проверка", "Причина" };
            for (int c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];
            var header = ws.Range(1, 1, 1, headers.Length);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E2E26");
            header.Style.Font.FontColor = XLColor.White;

            int r = 2;
            void Add(string model, string param, string check, string reason)
            {
                ws.Cell(r, 1).Value = model;
                ws.Cell(r, 2).Value = param;
                ws.Cell(r, 3).Value = check;
                ws.Cell(r, 4).Value = reason;
                r++;
            }

            foreach (var row in result.Rows)
            {
                foreach (var (param, summary) in new[] { ("Auto", row.Auto), ("BIM", row.Bim) })
                {
                    if (summary.Error != null) Add(row.ModelName, param, "—", "Ошибка чтения: " + summary.Error);
                    foreach (var t in summary.Failed) Add(row.ModelName, param, t, "Не пройдена");
                    foreach (var t in summary.Stale) Add(row.ModelName, param, t, "Устарела");
                }
                if (row.Nwc.Level == ReportLevel.High || row.Nwc.Level == ReportLevel.Attention)
                    Add(row.ModelName, "NWC", row.Nwc.NwcPath ?? "—", row.Nwc.ShortText);
            }

            ws.Range(1, 1, Math.Max(1, r - 1), headers.Length).SetAutoFilter();
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents(1, r, 10, 80);
        }

        private static void LevelCell(IXLCell cell, ReportLevel level)
        {
            cell.Value = ReportLevelRules.Text(level);
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#" + ReportLevelRules.Hex(level));
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Font.Bold = true;
        }

        /// <summary>
        /// Зависимости ClosedXML лежат рядом с плагином, а Assembly.Load по короткому имени
        /// ищет рядом с Revit.exe — подсказываем путь (как ExportLibraries в TNovVent).
        /// </summary>
        private static void EnsureResolverInstalled()
        {
            if (_resolverInstalled) return;
            _resolverInstalled = true;
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            AppDomain.CurrentDomain.AssemblyResolve += (s, args) =>
            {
                try
                {
                    string name = new AssemblyName(args.Name).Name;
                    if (string.IsNullOrEmpty(name) || name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                        return null;
                    string candidate = Path.Combine(dir, name + ".dll");
                    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
                }
                catch
                {
                    return null;
                }
            };
        }
    }
}
