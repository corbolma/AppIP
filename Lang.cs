// Lingua dell'interfaccia (italiano / inglese), condivisa da AppIP.exe e dall'installer.
// "auto" segue la lingua di Windows: italiano se è italiano, altrimenti inglese.
using System;
using System.Globalization;
using System.IO;

namespace AppIP
{
    static class L
    {
        public static bool En;
        public static string Setting = "auto";

        public static string T(string it, string en) { return En ? en : it; }

        public static bool IsValid(string v) { return v == "auto" || v == "it" || v == "en"; }

        public static void Set(string setting)
        {
            Setting = IsValid(setting) ? setting : "auto";
            if (Setting == "auto")
                En = !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("it", StringComparison.OrdinalIgnoreCase);
            else
                En = Setting == "en";
        }

        // Legge la scelta salvata dall'app in %APPDATA%\AppIP\settings.txt (usata dall'installer).
        public static void LoadFromSettings()
        {
            string v = "auto";
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppIP", "settings.txt");
                if (File.Exists(path))
                    foreach (var ln in File.ReadAllLines(path))
                        if (ln.StartsWith("lang=")) v = ln.Substring(5).Trim();
            }
            catch { }
            Set(v);
        }
    }
}
