// Lingua dell'interfaccia, condivisa da AppIP.exe e dall'installer.
//
// Italiano e inglese sono scritti nel codice: L.T("testo italiano", "English text").
// Le altre lingue sono file lang\<codice>.txt (es. lang\de.txt, lang\pt-BR.txt) incorporati
// nell'exe dagli script di build, con righe "English text = traduzione" (vedi lang\README.md).
// "auto" usa la lingua di Windows se disponibile, altrimenti l'inglese.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AppIP
{
    static class L
    {
        public class Language
        {
            public string Code, Name;
            public Dictionary<string, string> Texts;
        }

        const string ResPrefix = "AppIP.lang.";
        static List<Language> all;
        static Language current;

        public static string Setting = "auto";
        public static string Code { get { return current != null ? current.Code : "en"; } }

        // Testo nella lingua corrente; per le lingue da file la chiave è il testo inglese.
        public static string T(string it, string en)
        {
            string code = Code;
            if (code == "it") return it;
            string s;
            if (code != "en" && current.Texts != null && current.Texts.TryGetValue(en, out s)) return s;
            return en;
        }

        // Come T, con segnaposto {0}, {1}... al posto delle parti variabili.
        public static string F(string it, string en, params object[] args)
        {
            string fmt = T(it, en);
            try { return string.Format(fmt, args); }
            catch (FormatException) { return string.Format(en, args); } // traduzione con segnaposto sbagliati
        }

        public static List<Language> All()
        {
            if (all != null) return all;
            all = new List<Language>
            {
                new Language { Code = "it", Name = "Italiano" },
                new Language { Code = "en", Name = "English" }
            };
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                foreach (var res in asm.GetManifestResourceNames().OrderBy(n => n))
                {
                    if (!res.StartsWith(ResPrefix) || !res.EndsWith(".txt")) continue;
                    string code = res.Substring(ResPrefix.Length, res.Length - ResPrefix.Length - 4);
                    if (all.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))) continue;
                    using (var st = asm.GetManifestResourceStream(res))
                    using (var sr = new StreamReader(st, Encoding.UTF8))
                    {
                        var lang = Parse(code, sr.ReadToEnd());
                        if (lang != null) all.Add(lang);
                    }
                }
            }
            catch { }
            return all;
        }

        // Formato: righe "English text = traduzione"; "#" commento; "\n" = a capo;
        // la riga speciale "language = Nome" dà il nome mostrato nel menu.
        static Language Parse(string code, string text)
        {
            var d = new Dictionary<string, string>();
            string name = code;
            foreach (var raw in text.Split('\n'))
            {
                string ln = raw.Trim();
                if (ln.Length == 0 || ln.StartsWith("#")) continue;
                int eq = ln.IndexOf(" = ");
                if (eq <= 0) continue;
                string k = ln.Substring(0, eq).Trim().Replace("\\n", "\n");
                string v = ln.Substring(eq + 3).Trim().Replace("\\n", "\n");
                if (v.Length == 0) continue; // non ancora tradotta: resta l'inglese
                if (k == "language") name = v; else d[k] = v;
            }
            return new Language { Code = code, Name = name, Texts = d };
        }

        static Language Find(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            return All().FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        }

        public static void Set(string setting)
        {
            Language lang = setting == "auto" ? null : Find(setting);
            Setting = lang != null ? lang.Code : "auto";
            if (lang == null)
            {
                var ui = CultureInfo.CurrentUICulture;
                lang = Find(ui.Name) ?? Find(ui.TwoLetterISOLanguageName) ?? Find("en");
            }
            current = lang;
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
