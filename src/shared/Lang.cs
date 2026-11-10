//  Lang.cs - язык интерфейса (русский / английский).
//
//  Везде, где нужен текст для человека, пишем  L.T("по-русски", "in English").
//  Какой из двух покажется, решает настройка Language:
//      auto (по умолчанию) - как язык системы: русская система -> русский, остальные -> английский
//      ru / en             - принудительно
//  Тексты лежат прямо рядом с кодом, отдельного словаря нет: так проще читать и править.
//  Третий язык проще всего добавить, сделав в T ещё один параметр.
using System;
using System.Globalization;

namespace Gaslight
{
    public static class L
    {
        static bool autoKnown;
        static bool autoRu;

        // true = сейчас показываем русский
        public static bool Ru
        {
            get
            {
                string s = Settings.Language;
                if (string.Equals(s, "ru", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "en", StringComparison.OrdinalIgnoreCase)) return false;
                if (!autoKnown)
                {
                    try { autoRu = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"; }
                    catch (Exception) { autoRu = false; }
                    autoKnown = true;
                }
                return autoRu;
            }
        }

        public static string T(string ru, string en)
        {
            return Ru ? ru : en;
        }

        // Короткий код языка для отчёта: "ru" или "en".
        public static string Code { get { return Ru ? "ru" : "en"; } }
    }
}
