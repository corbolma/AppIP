// Versione condivisa da AppIP.exe e dall'installer. Cambiala qui prima di una nuova distribuzione.
using System.Reflection;

[assembly: AssemblyProduct("AppIP")]
[assembly: AssemblyCopyright(AppIP.AppVersion.Copyright)]
[assembly: AssemblyVersion("1.2.1.0")]
[assembly: AssemblyFileVersion("1.2.1.0")]

namespace AppIP
{
    static class AppVersion
    {
        // Autore e licenza: compaiono nelle proprietà degli exe e nella finestra dell'installer
        public const string Author = "Mauro Corboli";
        public const string Copyright = "Copyright © 2026 " + Author + " - MIT License";
        public const string RepoUrl = "https://github.com/corbolma/AppIP";
        public const string Text = "1.2.1";
    }
}
