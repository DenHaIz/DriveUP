using System;
using System.Globalization;

namespace DriveUp
{
    /// <summary>
    /// UI language (Russian / English). Strings are written as pairs right where they are used:
    /// L.T("Обновить", "Update"). The choice is remembered in DriveUP.ini; the first run
    /// follows the Windows display language.
    /// </summary>
    static class L
    {
        public static bool En;

        public static string T(string ru, string en)
        {
            return En ? en : ru;
        }

        public static void Init()
        {
            string saved = Settings.Language;
            En = saved == "en" || (saved != "ru" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ru");
        }

        public static void Save()
        {
            Settings.Language = En ? "en" : "ru";
            Settings.Save();
        }

        /// <summary>«name» in Russian, "name" in English.</summary>
        public static string Quote(string name)
        {
            return En ? "\"" + name + "\"" : "«" + name + "»";
        }

        /// <summary>"3 обновления" / "3 updates": the Russian form follows the number (1, 2-4, 5+).</summary>
        public static string Count(int n, string ru1, string ru2, string ru5, string en1, string enMany)
        {
            return n.ToString("N0", En ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("ru-RU")) + " " + Word(n, ru1, ru2, ru5, en1, enMany);
        }

        /// <summary>Just the word of Count(), without the number.</summary>
        public static string Word(int n, string ru1, string ru2, string ru5, string en1, string enMany)
        {
            if (En) return n == 1 ? en1 : enMany;
            int m100 = n % 100, m10 = n % 10;
            if (m100 >= 11 && m100 <= 14) return ru5;
            if (m10 == 1) return ru1;
            if (m10 >= 2 && m10 <= 4) return ru2;
            return ru5;
        }

        /// <summary>Short date in the interface language.</summary>
        public static string Date(DateTime d)
        {
            return d.ToString("d", En ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ru-RU"));
        }

        /// <summary>"сегодня, 10:42" / "вчера" / a date, for "last checked".</summary>
        public static string When(DateTime d)
        {
            var ci = En ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ru-RU");
            if (d.Date == DateTime.Today) return T("сегодня, ", "today, ") + d.ToString("t", ci);
            if (d.Date == DateTime.Today.AddDays(-1)) return T("вчера, ", "yesterday, ") + d.ToString("t", ci);
            return d.ToString("d", ci);
        }

        /// <summary>Human-readable size: 512 КБ, 3,4 ГБ / 512 KB, 3.4 GB.</summary>
        public static string Size(long bytes)
        {
            var ci = En ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("ru-RU");
            if (bytes < 1024) return bytes + (En ? " B" : " Б");
            double v = bytes / 1024.0;
            string[] units = En ? new[] { "KB", "MB", "GB", "TB" } : new[] { "КБ", "МБ", "ГБ", "ТБ" };
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            string fmt = v >= 100 || u == 0 ? "0" : (v >= 10 ? "0.#" : "0.##");
            return v.ToString(fmt, ci) + " " + units[u];
        }
    }
}
