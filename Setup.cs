// Installer di AppIP: un solo exe che contiene AppIP.exe.
// Installa per l'utente corrente (nessun permesso di amministratore) in %LOCALAPPDATA%\Programs\AppIP.
//   AppIP-Setup.exe              -> finestra di installazione / aggiornamento
//   AppIP-Setup.exe /silent      -> installazione silenziosa (avvio con Windows attivo, poi avvia l'app)
//   Uninstall.exe /uninstall     -> disinstallazione (registrata in Impostazioni > App)
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Installazione AppIP")]

namespace AppIP.Setup
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Func<string, bool> has = a => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
            bool silent = has("/silent") || has("/s");
            L.LoadFromSettings(); // stessa lingua scelta nell'app, altrimenti quella di Windows
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (has("/uninstall")) return Installer.Uninstall(silent) ? 0 : 1;
                if (silent)
                {
                    // In silenzioso non si torna mai a una versione più vecchia, salvo /force.
                    var inst = Installer.InstalledVersion();
                    if (inst != null && inst > Installer.Current && !has("/force")) return 3;
                    Installer.Install(Installer.StartupEnabled(), false, true);
                    return 0;
                }
                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show(L.T("Errore: ", "Error: ") + ex.Message, "AppIP", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }
    }

    static class Installer
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AppIP";

        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AppIP");
        public static readonly string ExePath = Path.Combine(InstallDir, "AppIP.exe");
        public static readonly string UninstallerPath = Path.Combine(InstallDir, "Uninstall.exe");
        static readonly string StartMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AppIP.lnk");
        static readonly string DesktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AppIP.lnk");

        public static readonly Version Current = Version.Parse(AppVersion.Text);

        // Versione realmente installata: letta dall'exe (non solo dal registro), null se non installata.
        public static Version InstalledVersion()
        {
            if (!File.Exists(ExePath)) return null;
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(ExePath);
                if (fv.FileMajorPart + fv.FileMinorPart + fv.FileBuildPart > 0)
                    return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            }
            catch { }
            using (var k = Registry.CurrentUser.OpenSubKey(UninstKey))
            {
                Version v;
                string s = k != null ? k.GetValue("DisplayVersion") as string : null;
                return s != null && Version.TryParse(s, out v) ? v : new Version(0, 0, 0);
            }
        }

        public static string V(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        public static bool StartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k == null || InstalledVersion() == null || k.GetValue("AppIP") != null;
        }

        // Chiude AppIP se è aperta (usa il comando di uscita dell'app, così pulisce anche la Jump List).
        public static void CloseRunning()
        {
            int session = Process.GetCurrentProcess().SessionId;
            Func<Process[]> mine = () => Process.GetProcessesByName("AppIP").Where(p => p.SessionId == session).ToArray();
            if (mine().Length == 0) return;
            try { EventWaitHandle.OpenExisting("AppIP_Cmd_exit").Set(); } catch { }
            var until = DateTime.Now.AddSeconds(5);
            while (mine().Length > 0 && DateTime.Now < until) Thread.Sleep(200);
            foreach (var p in mine())
                try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        public static void Install(bool startup, bool desktop, bool launch)
        {
            CloseRunning();
            Directory.CreateDirectory(InstallDir);

            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIP.payload"))
            {
                if (src == null) throw new InvalidOperationException("pacchetto AppIP.exe mancante nell'installer");
                string tmp = ExePath + ".new";
                using (var dst = File.Create(tmp)) src.CopyTo(dst);
                if (File.Exists(ExePath)) File.Delete(ExePath);
                File.Move(tmp, ExePath);
            }

            string self = Application.ExecutablePath;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallerPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, UninstallerPath, true);

            string desc = L.T("Mostra IP, nome macchina e DNS", "Shows IP, computer name and DNS");
            CreateShortcut(StartMenuLnk, ExePath, desc);
            if (desktop) CreateShortcut(DesktopLnk, ExePath, desc);
            else if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk);

            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (startup) k.SetValue("AppIP", "\"" + ExePath + "\"");
                else k.DeleteValue("AppIP", false);
            }

            using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
            {
                k.SetValue("DisplayName", "AppIP");
                k.SetValue("DisplayVersion", AppVersion.Text);
                k.SetValue("VersionMajor", Current.Major, RegistryValueKind.DWord);
                k.SetValue("VersionMinor", Current.Minor, RegistryValueKind.DWord);
                k.SetValue("Publisher", "AppIP");
                k.SetValue("DisplayIcon", ExePath + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + UninstallerPath + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + UninstallerPath + "\" /uninstall /silent");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(new FileInfo(ExePath).Length / 1024 + new FileInfo(UninstallerPath).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            if (launch) Process.Start(new ProcessStartInfo(ExePath) { WorkingDirectory = InstallDir });
        }

        public static bool Uninstall(bool silent)
        {
            if (!silent && MessageBox.Show(L.T("Vuoi disinstallare AppIP?", "Do you want to uninstall AppIP?"), L.T("Disinstalla AppIP", "Uninstall AppIP"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            CloseRunning();
            foreach (var f in new[] { StartMenuLnk, DesktopLnk })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue("AppIP", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
            try
            {
                string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppIP");
                if (Directory.Exists(settings)) Directory.Delete(settings, true);
            }
            catch { }
            try { if (File.Exists(ExePath)) File.Delete(ExePath); } catch { }

            // L'uninstaller gira dalla cartella da eliminare: la cancella un cmd dopo la nostra uscita.
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            });

            if (!silent) MessageBox.Show(L.T("AppIP è stata disinstallata.", "AppIP has been uninstalled."), L.T("Disinstalla AppIP", "Uninstall AppIP"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        // Deve coincidere con Program.AppUserModelId in AppIP.cs
        const string AppUserModelId = "AppIP.Widget";

        // Crea il collegamento con l'AppUserModelID: l'icona fissata da qui mostra la Jump List dell'app.
        static void CreateShortcut(string lnkPath, string target, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
            var link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(Path.GetDirectoryName(target));
                link.SetDescription(description);
                link.SetIconLocation(target, 0);

                var store = (IPropertyStore)link;
                var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                var pv = new PropVariant { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(AppUserModelId) };
                try { store.SetValue(ref key, ref pv); store.Commit(); }
                finally { Marshal.FreeCoTaskMem(pv.p); }

                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnkPath, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PropertyKey pkey);
            void GetValue(ref PropertyKey key, out PropVariant pv);
            void SetValue(ref PropertyKey key, ref PropVariant pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr p;
        }
    }

    class SetupForm : Form
    {
        readonly CheckBox cbStartup, cbDesktop, cbLaunch;
        readonly Button btnInstall, btnCancel;
        readonly Label lblStatus;

        public SetupForm()
        {
            var installed = Installer.InstalledVersion();
            var cur = Installer.Current;
            string vi = installed != null ? Installer.V(installed) : null, vc = Installer.V(cur);
            string infoText, action;
            bool warn = false;
            if (installed == null)
            {
                action = L.T("Installa", "Install");
                infoText = L.T("AppIP verrà installata solo per il tuo utente, senza permessi di amministratore, nella cartella:",
                               "AppIP will be installed for your user only, without administrator rights, in the folder:");
            }
            else if (installed < cur)
            {
                action = L.T("Aggiorna", "Update");
                infoText = L.T("Aggiornamento dalla versione " + vi + " alla " + vc + ". Le impostazioni vengono mantenute.",
                               "Update from version " + vi + " to " + vc + ". Your settings will be kept.");
            }
            else if (installed == cur)
            {
                action = L.T("Reinstalla", "Reinstall");
                infoText = L.T("La versione " + vc + " è già installata. Puoi reinstallarla (le impostazioni vengono mantenute).",
                               "Version " + vc + " is already installed. You can reinstall it (your settings will be kept).");
            }
            else
            {
                action = L.T("Installa comunque", "Install anyway");
                warn = true;
                infoText = L.T("Attenzione: è installata una versione più recente (" + vi + "). Questo installer contiene la " + vc +
                           ", più vecchia. Continuando la sostituirai.",
                           "Warning: a newer version (" + vi + ") is installed. This installer contains the older " + vc +
                           ". Continuing will replace it.");
            }

            Text = action + " AppIP";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f); // layout pensato a 96 DPI, scalato in base allo schermo
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(470, 300);
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = Color.FromArgb(37, 99, 235) };
            var pic = new PictureBox { Location = new Point(20, 15), Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            try { using (var ic = new Icon(Icon, 64, 64)) pic.Image = ic.ToBitmap(); } catch { }
            var title = new Label
            {
                Text = "AppIP " + AppVersion.Text, ForeColor = Color.White, AutoSize = true,
                Font = new Font("Segoe UI Semibold", 15f), Location = new Point(88, 16), BackColor = Color.Transparent
            };
            var sub = new Label
            {
                Text = L.T("IP, nome macchina, DNS, velocità e Wi-Fi sul desktop", "IP, computer name, DNS, speed and Wi-Fi on your desktop"), ForeColor = Color.FromArgb(220, 230, 255),
                AutoSize = true, Location = new Point(90, 50), BackColor = Color.Transparent
            };
            header.Controls.AddRange(new Control[] { pic, title, sub });

            var info = new Label
            {
                Location = new Point(20, 100), AutoSize = true, MaximumSize = new Size(430, 0),
                Text = infoText, ForeColor = warn ? Color.FromArgb(190, 60, 0) : SystemColors.ControlText
            };
            var path = new Label
            {
                Location = new Point(20, 136), Size = new Size(430, 18), ForeColor = Color.DimGray,
                Text = Installer.InstallDir, AutoEllipsis = true
            };

            cbStartup = new CheckBox { Text = L.T("Avvia AppIP all'accensione del PC", "Start AppIP when Windows starts"), Location = new Point(22, 164), AutoSize = true, Checked = Installer.StartupEnabled() };
            cbDesktop = new CheckBox { Text = L.T("Crea un collegamento sul desktop", "Create a desktop shortcut"), Location = new Point(22, 188), AutoSize = true };
            cbLaunch = new CheckBox { Text = L.T("Avvia AppIP al termine", "Launch AppIP when finished"), Location = new Point(22, 212), AutoSize = true, Checked = true };

            lblStatus = new Label { Location = new Point(20, 262), Size = new Size(240, 20), ForeColor = Color.DimGray };
            btnInstall = new Button { Text = action, Location = new Point(240, 256), Size = new Size(120, 30) };
            btnCancel = new Button { Text = L.T("Annulla", "Cancel"), Location = new Point(366, 256), Size = new Size(90, 30), DialogResult = DialogResult.Cancel };
            btnInstall.Click += delegate { DoInstall(); };
            AcceptButton = btnInstall;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[] { header, info, path, cbStartup, cbDesktop, cbLaunch, lblStatus, btnInstall, btnCancel });

            // Dispone i controlli sotto il testo informativo, qualunque sia la sua altezza (righe, DPI)
            Load += delegate
            {
                int y = info.Bottom + 6;
                path.Top = y; y = path.Bottom + 12;
                foreach (var cb in new[] { cbStartup, cbDesktop, cbLaunch }) { cb.Top = y; y = cb.Bottom + 4; }
                y += 14;
                btnInstall.Top = btnCancel.Top = y;
                lblStatus.Top = y + 6;
                ClientSize = new Size(ClientSize.Width, btnInstall.Bottom + 14);
            };
        }

        void DoInstall()
        {
            btnInstall.Enabled = btnCancel.Enabled = false;
            lblStatus.Text = L.T("Installazione in corso...", "Installing...");
            Cursor = Cursors.WaitCursor;
            Application.DoEvents();
            try
            {
                Installer.Install(cbStartup.Checked, cbDesktop.Checked, cbLaunch.Checked);
                Cursor = Cursors.Default;
                MessageBox.Show(this,
                    L.T("AppIP è stata installata.\n\n" +
                        "La trovi nel menu Start. Per fissarla alla barra delle applicazioni: " +
                        "Start → tasto destro su AppIP → Aggiungi alla barra delle applicazioni.\n\n" +
                        "Per disinstallarla: Impostazioni → App → App installate → AppIP.",
                        "AppIP has been installed.\n\n" +
                        "You can find it in the Start menu. To pin it to the taskbar: " +
                        "Start → right-click AppIP → Pin to taskbar.\n\n" +
                        "To uninstall it: Settings → Apps → Installed apps → AppIP."),
                    "AppIP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                lblStatus.Text = "";
                btnInstall.Enabled = btnCancel.Enabled = true;
                MessageBox.Show(this, L.T("Installazione non riuscita:\n", "Installation failed:\n") + ex.Message, "AppIP", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
