// Перенесено из monumenthunny-dev/tnovpro-issues-revit (Revit/ExportProgress.cs) для «Модели» TNovPRO:
// вопрос о модели на сайте + синхронизация с центральной моделью (2026-09-28).
using System;

namespace TNovUtils.Issues.ModelSync
{
    /// <summary>
    /// Обратная связь долгой выгрузки: куда писать этап и как спросить об отмене.
    ///
    /// Зачем это есть. Дом со всеми разделами собирается минутами, и всё это время
    /// Revit не отвечает — со стороны неотличимо от зависания. Первый живой прогон
    /// кнопки «Выгрузить для Атласа» 4 августа так и кончился: Revit закрыли, не
    /// дождавшись, и не осталось даже следа, на чём шла работа. Поэтому выгрузка
    /// обязана рассказывать, где она, и уметь остановиться по просьбе.
    ///
    /// 2026-08-10: одного текста мало. «Долго» без цифр — это всегда «висит»,
    /// поэтому этап отдаётся ещё и ЧИСЛАМИ (сделано из всего), а окно само считает
    /// процент, скорость и остаток. Плюс область работы (раздел N из M) — при
    /// выгрузке со связями разделов больше десятка, и без этого непонятно, идёт
    /// первый из двенадцати или последний.
    /// </summary>
    public sealed class ExportProgress
    {
        /// <summary>Текущий этап словами: «ОВ_С1: паспорта 12 340 из 36 118».</summary>
        public Action<string> Status;

        /// <summary>Этап числами: (что делаем, сделано, всего). total = 0 — счётчика нет.</summary>
        public Action<string, long, long> Detail;

        /// <summary>Область работы: (номер раздела с 1, всего разделов, имя раздела).</summary>
        public Action<int, int, string> Scope;

        /// <summary>Вернуть true, чтобы выгрузка прекратилась и отдала собранное.</summary>
        public Func<bool> CancelRequested;

        public bool IsCancelled
        {
            get
            {
                try { return CancelRequested != null && CancelRequested(); }
                catch { return false; }
            }
        }

        public void Report(string text)
        {
            try { Status?.Invoke(text); } catch { /* показ прогресса не должен ронять выгрузку */ }
        }

        /// <summary>Этап со счётчиком: окно рисует полосу и считает остаток.</summary>
        public void Report(string stage, long done, long total)
        {
            try
            {
                if (Detail != null) Detail(stage, done, total);
                else Status?.Invoke(total > 0 ? $"{stage} {done:N0} из {total:N0}" : stage);
            }
            catch { }
        }

        /// <summary>Переход к следующему разделу: «раздел 3 из 12 · ОВ_С1».</summary>
        public void SetScope(int index, int count, string name)
        {
            try { Scope?.Invoke(index, count, name); } catch { }
        }
    }
}
