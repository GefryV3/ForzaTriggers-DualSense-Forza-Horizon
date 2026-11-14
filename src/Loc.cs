// Localization: every user-facing string is written as T("русский", "English").
// Language: Settings.Language (-1 = follow Windows, 0 = Russian, 1 = English).

using System.Globalization;

namespace ForzaTriggers
{
    public static class Loc
    {
        public static bool En;

        public static string T(string ru, string en) { return En ? en : ru; }

        public static void Apply(int language)
        {
            if (language == 0) En = false;
            else if (language == 1) En = true;
            else
            {
                string l = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                En = !(l == "ru" || l == "uk" || l == "be" || l == "kk");
            }
        }
    }
}
