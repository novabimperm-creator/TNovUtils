using System.Collections.Generic;

namespace TNovUtils
{
    public static class ResponsibleList
    {
        public static List<KeyValuePair<string, string>> List { get; } = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Рошиор А.Г", "roshior.a"),
            new KeyValuePair<string, string>("Порываев И.А", "poryvaev.i"),
            new KeyValuePair<string, string>("Шатохин В.Л", "shatohin.v"),
            new KeyValuePair<string, string>("Чащин Е.А", "chashin.e")
        };
    }
}