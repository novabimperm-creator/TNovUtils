namespace CleanLinks.Core
{
    /// <summary>
    /// Обратная связь о ходе долгой операции. Интерфейс живёт в Core, чтобы ядро
    /// не знало ничего про WinForms: реализация — окно прогресса, но с тем же успехом
    /// это может быть лог или заглушка.
    /// </summary>
    public interface IProgressReporter
    {
        /// <summary>Пользователь нажал «Прервать». Проверяется между связями.</summary>
        bool IsCancelled { get; }

        /// <summary>Сообщает, что началась обработка шага <paramref name="index"/> из <paramref name="total"/>.</summary>
        void Report(string caption, int index, int total);

        /// <summary>Уточняет, что происходит внутри текущего шага.</summary>
        void ReportDetail(string detail);
    }
}
