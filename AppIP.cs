// AppIP - widget desktop + icona nella tray: nome macchina, IP locale/pubblico, DNS,
// velocità di rete, Wi-Fi, ping, VPN.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace AppIP
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        // Identificativo fisso per la barra delle applicazioni: deve coincidere con quello scritto
        // dall'installer nel collegamento del menu Start, così la Jump List appare sull'icona fissata.
        public const string AppUserModelId = "AppIP.Widget";

        // Comandi da riga di comando (usati anche dalla Jump List della barra delle applicazioni):
        //   (nessuno) / /front  -> mostra il widget e lo porta in primo piano
        //   /topmost            -> attiva/disattiva "Sempre in primo piano"
        //   /exit               -> chiude l'app se è aperta
        [STAThread]
        static void Main(string[] args)
        {
            // /demo [light] [vpn] [compact] [en]: finestra con dati fittizi per gli screenshot, indipendente dall'istanza normale
            if (args.Length > 0 && args[0].Equals("/demo", StringComparison.OrdinalIgnoreCase))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm("demo", args));
                return;
            }

            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }
            string cmd = args.Length > 0 ? args[0].TrimStart('/', '-').ToLowerInvariant() : "front";
            if (cmd != "exit" && cmd != "topmost") cmd = "front";

            bool createdNew;
            using (var mutex = new Mutex(true, "AppIP_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // Già in esecuzione: inoltra il comando all'istanza aperta.
                    AllowSetForegroundWindow(-1);
                    try { EventWaitHandle.OpenExisting(WidgetForm.EventPrefix + cmd).Set(); } catch { }
                    return;
                }
                if (cmd == "exit") return; // non era aperta: niente da chiudere
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm(cmd));
            }
        }
    }

    class NetLine
    {
        public string Id, Name, Description, IPv4, IPv4Plain, Gateway, Dns, Mac;
        public string IPv6 = "", IPv6All = "", Dns6 = "", DhcpServer = "";
        public bool HasGateway, IsVpn, IsVirtual, IsWifi;
        public bool Dhcp;
        public DateTime LeaseEnd = DateTime.MinValue; // scadenza del lease DHCP (MinValue = sconosciuta)
        public Wifi.Info Wi;                         // dettagli Wi-Fi (banda, canale, velocità)
        public string WifiSsid;
        public int WifiSignal = -1;
        public bool WifiDenied;
        public NetworkInterface Ni;
    }

    class Palette
    {
        public Color Back, Border, Label, Value, Accent, Pub, Good, Warn, Bad, Vpn;
    }

    class Hit
    {
        public Rectangle R;
        public string Text;
    }

    // ---------- Wi-Fi tramite Native Wifi API (wlanapi.dll) ----------
    static class Wifi
    {
        [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint ver, IntPtr res, out uint neg, out IntPtr h);
        [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr h, IntPtr res, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr h, ref Guid g, int op, IntPtr res, out int size, out IntPtr data, IntPtr type);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
        [DllImport("wlanapi.dll")] static extern int WlanGetNetworkBssList(IntPtr h, ref Guid g, IntPtr ssid, int bssType, bool secEnabled, IntPtr res, out IntPtr list);

        public class Info
        {
            public string Ssid; public int Signal = -1; public bool Denied;
            public int Phy = -1, RxMbps = -1, TxMbps = -1, Channel = -1, FreqMHz = -1, Rssi;
            public byte[] Bssid;

            public string Band()
            {
                int f = FreqMHz;
                if (f >= 5925) return "6 GHz";
                if (f >= 4900) return "5 GHz";
                if (f >= 2400 && f < 2500) return "2.4 GHz";
                if (Channel > 0) return Channel <= 14 ? "2.4 GHz" : "5 GHz";
                return "";
            }

            public int Chan()
            {
                int f = FreqMHz;
                if (f >= 5925) return f == 5935 ? 2 : (f - 5950) / 5;
                if (f >= 5000) return (f - 5000) / 5;
                if (f == 2484) return 14;
                if (f >= 2412 && f < 2484) return (f - 2407) / 5;
                return Channel;
            }

            // Generazione dello standard (DOT11_PHY_TYPE)
            public string Gen()
            {
                switch (Phy)
                {
                    case 4: return "802.11a";
                    case 5: return "802.11b";
                    case 6: return "802.11g";
                    case 7: return "Wi-Fi 4";
                    case 8: return "Wi-Fi 5";
                    case 10: return Band() == "6 GHz" ? "Wi-Fi 6E" : "Wi-Fi 6";
                    case 11: return "Wi-Fi 7";
                }
                return "";
            }
        }

        static IntPtr handle = IntPtr.Zero;
        static bool unavailable;

        public static Dictionary<string, Info> Query()
        {
            var res = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (handle == IntPtr.Zero)
                {
                    if (unavailable) return res;
                    uint neg;
                    if (WlanOpenHandle(2, IntPtr.Zero, out neg, out handle) != 0) { unavailable = true; handle = IntPtr.Zero; return res; }
                }
                IntPtr list;
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return res;
                try
                {
                    int n = Marshal.ReadInt32(list, 0);
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr item = IntPtr.Add(list, 8 + i * 532);
                        var gb = new byte[16];
                        Marshal.Copy(item, gb, 0, 16);
                        var guid = new Guid(gb);
                        if (Marshal.ReadInt32(item, 16 + 512) != 1) continue; // 1 = connesso

                        var info = new Info();
                        int size; IntPtr data;
                        int rc = WlanQueryInterface(handle, ref guid, 7, IntPtr.Zero, out size, out data, IntPtr.Zero);
                        if (rc == 0)
                        {
                            try
                            {
                                int len = Marshal.ReadInt32(data, 520);
                                if (len > 0 && len <= 32)
                                {
                                    var s = new byte[len];
                                    Marshal.Copy(IntPtr.Add(data, 524), s, 0, len);
                                    info.Ssid = Encoding.UTF8.GetString(s);
                                }
                                if (string.IsNullOrEmpty(info.Ssid)) info.Ssid = Marshal.PtrToStringUni(IntPtr.Add(data, 8));
                                info.Signal = Marshal.ReadInt32(data, 576);
                                // WLAN_ASSOCIATION_ATTRIBUTES: BSSID @560, tipo PHY @568, velocità rx/tx in kbps @580/@584
                                info.Bssid = new byte[6];
                                Marshal.Copy(IntPtr.Add(data, 560), info.Bssid, 0, 6);
                                info.Phy = Marshal.ReadInt32(data, 568);
                                int rx = Marshal.ReadInt32(data, 580), tx = Marshal.ReadInt32(data, 584);
                                if (rx > 0) info.RxMbps = rx / 1000;
                                if (tx > 0) info.TxMbps = tx / 1000;
                            }
                            finally { WlanFreeMemory(data); }
                            int csize; IntPtr cdata; // canale (wlan_intf_opcode_channel_number)
                            if (WlanQueryInterface(handle, ref guid, 8, IntPtr.Zero, out csize, out cdata, IntPtr.Zero) == 0)
                            {
                                try { info.Channel = Marshal.ReadInt32(cdata); }
                                finally { WlanFreeMemory(cdata); }
                            }
                            ReadBss(guid, info);
                        }
                        else if (rc == 5) info.Denied = true;
                        res[guid.ToString("B")] = info;
                    }
                }
                finally { WlanFreeMemory(list); }
            }
            catch { }
            return res;
        }

        // Frequenza e RSSI della rete a cui si è connessi, dalla lista BSS in cache (distingue 5 e 6 GHz).
        static void ReadBss(Guid guid, Info info)
        {
            if (info.Bssid == null) return;
            IntPtr bl;
            if (WlanGetNetworkBssList(handle, ref guid, IntPtr.Zero, 3, false, IntPtr.Zero, out bl) != 0) return;
            try
            {
                int n = Marshal.ReadInt32(bl, 4);
                var b = new byte[6];
                for (int i = 0; i < n && i < 512; i++)
                {
                    IntPtr e = IntPtr.Add(bl, 8 + i * 360); // sizeof(WLAN_BSS_ENTRY) = 360
                    Marshal.Copy(IntPtr.Add(e, 40), b, 0, 6);
                    if (!b.SequenceEqual(info.Bssid)) continue;
                    info.Rssi = Marshal.ReadInt32(e, 56);
                    info.FreqMHz = Marshal.ReadInt32(e, 92) / 1000;
                    break;
                }
            }
            finally { WlanFreeMemory(bl); }
        }
    }

    // Finestra "Informazioni su AppIP": versione, autore, licenza e link al progetto.
    class AboutForm : Form
    {
        static AboutForm open;

        public static void ShowOnce(Icon icon)
        {
            if (open == null || open.IsDisposed) open = new AboutForm(icon);
            open.Show();
            open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        AboutForm(Icon icon)
        {
            Text = L.T("Informazioni su AppIP", "About AppIP");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true; // il widget sta sotto le finestre: questa deve vedersi subito
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;
            ClientSize = new Size(360, 200);
            if (icon != null) Icon = icon;

            var blue = Color.FromArgb(37, 99, 235);
            var pic = new PictureBox { Location = new Point(20, 20), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom };
            try
            {
                // Le immagini in appip.ico sono PNG: si legge direttamente quella da 64 px
                // (Icon.ToBitmap non decodifica bene le voci PNG)
                using (var st = typeof(AboutForm).Assembly.GetManifestResourceStream("AppIP.appip.ico"))
                using (var ms = new MemoryStream())
                {
                    st.CopyTo(ms);
                    byte[] d = ms.ToArray();
                    int n = BitConverter.ToUInt16(d, 4);
                    for (int i = 0; i < n; i++)
                    {
                        int e = 6 + 16 * i;
                        if (d[e] != 64) continue;
                        int size = BitConverter.ToInt32(d, e + 8), off = BitConverter.ToInt32(d, e + 12);
                        using (var img = Image.FromStream(new MemoryStream(d, off, size))) pic.Image = new Bitmap(img);
                        break;
                    }
                }
            }
            catch { }
            var title = new Label { Text = "AppIP " + AppVersion.Text, AutoSize = true, Location = new Point(82, 18), Font = new Font("Segoe UI Semibold", 14f) };
            var desc = new Label
            {
                Text = L.T("IP, nome macchina, DNS, velocità e Wi-Fi sul desktop", "IP, computer name, DNS, speed and Wi-Fi on your desktop"),
                AutoSize = true, MaximumSize = new Size(260, 0), Location = new Point(84, 50), ForeColor = Color.DimGray
            };
            var author = new Label { Text = "© 2026 " + AppVersion.Author, AutoSize = true, Location = new Point(84, 90) };

            string lic = L.T("Licenza MIT", "MIT License");
            var links = new LinkLabel
            {
                Text = lic + "  ·  GitHub", AutoSize = true, Location = new Point(84, 112),
                LinkColor = blue, ActiveLinkColor = blue, LinkBehavior = LinkBehavior.HoverUnderline
            };
            links.Links.Clear();
            links.Links.Add(0, lic.Length, AppVersion.RepoUrl + "/blob/main/LICENSE");
            links.Links.Add(lic.Length + 5, 6, AppVersion.RepoUrl);
            links.LinkClicked += (s, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo((string)e.Link.LinkData) { UseShellExecute = true }); } catch { }
            };

            var ok = new Button { Text = "OK", Size = new Size(90, 30), DialogResult = DialogResult.OK };
            ok.Click += delegate { Close(); };
            AcceptButton = CancelButton = ok;
            Controls.AddRange(new Control[] { pic, title, desc, author, links, ok });

            Load += delegate
            {
                // Testi sotto la descrizione, qualunque sia la sua altezza (lingua, DPI)
                author.Top = desc.Bottom + 14;
                links.Top = author.Bottom + 4;
                ok.Location = new Point(ClientSize.Width - 16 - ok.Width, links.Bottom + 16);
                ClientSize = new Size(ClientSize.Width, ok.Bottom + 14);
            };
        }
    }

    class WidgetForm : Form
    {
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int idx, IntPtr val);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLong(IntPtr hWnd, int idx, IntPtr val);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        const int GWL_HWNDPARENT = -8;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WM_NCLBUTTONDOWN = 0xA1;
        const int HTCAPTION = 2;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "AppIP";
        public const string EventPrefix = "AppIP_Cmd_";
        readonly List<EventWaitHandle> cmdEvents = new List<EventWaitHandle>();
        bool tempFront; // portato in primo piano dalla Jump List, torna sotto quando clicchi altrove

        static readonly string[] VpnWords = { "vpn", "wireguard", "wintun", "openvpn", "tap-windows", "fortinet", "forticlient",
            "anyconnect", "globalprotect", "pangp", "juniper", "pulse secure", "sonicwall", "zscaler", "tailscale",
            "zerotier", "nordlynx", "protonvpn", "check point", "netextender", "softether" };
        static readonly string[] VirtualWords = { "hyper-v", "vethernet", "vmware", "virtualbox", "wsl", "docker",
            "bluetooth", "npcap", "loopback", "virtual adapter", "kernel debug" };

        readonly NotifyIcon tray;
        readonly WinTimer infoTimer, fastTimer, flashTimer;
        readonly ToolStripMenuItem miTopMost, miStartup, miShow, miAdapters, miCompact, miHideVirtual, miUpdateAvail;
        readonly ToolStripMenuItem[] miTheme, miOpacity, miLang;
        readonly List<Action> relabel = new List<Action>(); // ritraduce le voci del menu al cambio di lingua
        static readonly int[] OpacityValues = { 100, 90, 80, 70, 60, 50 };
        readonly Font fTitle = new Font("Segoe UI Semibold", 13f);
        readonly Font fLabel = new Font("Segoe UI", 8.5f);
        readonly Font fSmall = new Font("Segoe UI", 7.5f);
        readonly Font fValue = new Font("Consolas", 10f);
        readonly string settingsPath;

        // stato
        string hostName = "", domain = "";
        List<NetLine> allLines = new List<NetLine>();
        List<NetLine> lines = new List<NetLine>();
        NetLine primary, vpn;
        string publicIp = "...", publicIp6 = "...";
        DateTime lastPublicFetch = DateTime.MinValue;
        int fetching;

        // impostazioni
        bool topMostMode, compact, hideVirtual = true;
        bool showIpv6, showMac = true, showDhcp = true, showWifiInfo = true;
        bool autoUpdate; // controllo automatico degli aggiornamenti: disattivato di default (niente traffico non richiesto)
        string theme = "auto";
        string speedServer = ""; // server del test velocità; vuoto = predefinito (Cloudflare)
        int opacity = 90;
        HashSet<string> hiddenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> shownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // velocità
        string speedId;
        long lastRx = -1, lastTx = -1;
        DateTime lastSample;
        double rxRate, txRate;
        readonly List<double> rxHist = new List<double>(), txHist = new List<double>();
        const int HistLen = 60;

        // ping (-2 = in attesa, -1 = timeout, -3 = nessun gateway); intervallo 0 = disattivato
        static readonly int[] PingValues = { 0, 2, 5, 10, 30, 60 };
        volatile int pingInterval = 0;
        readonly AutoResetEvent pingWake = new AutoResetEvent(false);
        ToolStripMenuItem[] miPing;
        volatile string pingGateway;
        volatile int gwMs = -2, netMs = -2;

        // interazione
        List<Hit> hits = new List<Hit>();
        bool pressing;
        Point downPt;
        Rectangle flashRect = Rectangle.Empty;

        bool demo;

        // aggiornamenti
        string updVersion, updUrl, updSha; // ultima release più recente di questa (null = nessuna)
        int checkingUpd;
        DateTime lastUpdCheck = DateTime.MinValue;
        bool balloonIsUpdate;
        WinTimer updTimer;

        // Dati fittizi per gli screenshot (indirizzi di documentazione RFC 5737 / reti private di esempio).
        void FillDemo(bool withVpn)
        {
            hostName = "DESKTOP-APPIP";
            domain = L.T("esempio.local", "example.local");
            publicIp = "203.0.113.42";
            publicIp6 = "2001:db8:85a3::42";
            var list = new List<NetLine>
            {
                new NetLine { Id = "demo-wifi", Name = "Wi-Fi", IPv4 = "192.168.1.50/24", IPv4Plain = "192.168.1.50",
                    Gateway = "192.168.1.1", HasGateway = true, Dns = "192.168.1.1, 1.1.1.1", Mac = "00:11:22:33:44:55",
                    IsWifi = true, WifiSsid = L.T("Ufficio-WiFi", "Office-WiFi"), WifiSignal = 82,
                    IPv6 = "2001:db8:1234:5678::50", IPv6All = "2001:db8:1234:5678::50", Dhcp = true, DhcpServer = "192.168.1.1",
                    LeaseEnd = DateTime.Now.AddHours(7.5),
                    Wi = new Wifi.Info { Phy = 10, FreqMHz = 5180, RxMbps = 866, TxMbps = 780, Rssi = -54 } },
                new NetLine { Id = "demo-eth", Name = "Ethernet", IPv4 = "10.0.0.25/24", IPv4Plain = "10.0.0.25",
                    Gateway = "-", Dns = "10.0.0.10", Mac = "00:11:22:33:44:66" }
            };
            if (withVpn)
                list.Add(new NetLine { Id = "demo-vpn", Name = "WireGuard", IPv4 = "10.8.0.6/32", IPv4Plain = "10.8.0.6",
                    Gateway = "-", Dns = "10.8.0.1", Mac = "-", IsVpn = true });
            allLines = list;
            lines = list.Where(l => !l.IsVpn).ToList();
            primary = lines[0];
            vpn = allLines.FirstOrDefault(l => l.IsVpn);

            var rnd = new Random(7);
            rxHist.Clear(); txHist.Clear();
            for (int i = 0; i < HistLen; i++)
            {
                double wave = Math.Max(0, Math.Sin(i / 6.0) + 0.6 * Math.Sin(i / 2.3 + 1));
                rxHist.Add((180 + 900 * wave + rnd.Next(120)) * 1024);
                txHist.Add((40 + 160 * wave + rnd.Next(40)) * 1024);
            }
            rxRate = rxHist[HistLen - 1]; txRate = txHist[HistLen - 1];
        }

        public WidgetForm(string startCmd, string[] demoArgs = null)
        {
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppIP", "settings.txt");

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Width = 330;
            Height = 200;

            // ----- menu -----
            // La lingua serve già per le etichette del menu (in demo: /demo lang=de, oppure "en"; predefinito italiano)
            if (demoArgs != null)
            {
                string dl = "it";
                foreach (var a in demoArgs.Select(x => x.TrimStart('/', '-')))
                    if (a.Equals("en", StringComparison.OrdinalIgnoreCase)) dl = "en";
                    else if (a.StartsWith("lang=", StringComparison.OrdinalIgnoreCase)) dl = a.Substring(5);
                L.Set(dl);
            }
            else
                L.Set(ReadSetting("lang"));

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("AppIP " + AppVersion.Text) { Enabled = false });
            // Compare solo quando c'è una versione più recente
            miUpdateAvail = new ToolStripMenuItem("", null, delegate { OfferUpdate(); }) { Visible = false };
            miUpdateAvail.Font = new Font(miUpdateAvail.Font, FontStyle.Bold);
            relabel.Add(delegate { if (updVersion != null) miUpdateAvail.Text = L.F("Aggiorna ad AppIP {0}...", "Update to AppIP {0}...", updVersion); });
            menu.Items.Add(miUpdateAvail);
            menu.Items.Add(new ToolStripSeparator());
            miShow = Mi("Mostra widget", "Show widget", delegate { ToggleWidget(); });
            miShow.Checked = true;
            menu.Items.Add(miShow);
            menu.Items.Add(Mi("Aggiorna", "Refresh", delegate { lastPublicFetch = DateTime.MinValue; RefreshInfo(); }));
            menu.Items.Add(Mi("Copia tutto", "Copy all", delegate { CopyAll(); }));
            menu.Items.Add(Mi("Test velocità...", "Speed test...", delegate
            {
                SpeedTestForm.ShowOnce(tray.Icon,
                    () => speedServer.Length > 0 ? speedServer : SpeedTestForm.DefaultServer,
                    v => { speedServer = v; SaveSettings(); });
            }));
            menu.Items.Add(new ToolStripSeparator());

            miAdapters = Mi("Schede di rete", "Network adapters", null);
            miAdapters.DropDownItems.Add("-");
            miAdapters.DropDownOpening += delegate { BuildAdaptersMenu(); };
            miHideVirtual = Mi("Nascondi schede virtuali", "Hide virtual adapters", delegate
            {
                hideVirtual = !hideVirtual; miHideVirtual.Checked = hideVirtual; SaveSettings(); ApplyFilter();
            });
            menu.Items.Add(miAdapters);

            var miDetails = Mi("Dettagli da mostrare", "Details to show", null);
            Toggle(miDetails, "IPv6", "IPv6", () => showIpv6, v =>
            {
                showIpv6 = v;
                if (v) { publicIp6 = "..."; lastPublicFetch = DateTime.MinValue; }
                RefreshInfo();
            });
            Toggle(miDetails, "Indirizzo MAC", "MAC address", () => showMac, v => showMac = v);
            Toggle(miDetails, "DHCP o statico (con scadenza)", "DHCP or static (with lease time)", () => showDhcp, v => showDhcp = v);
            Toggle(miDetails, "Dettagli Wi-Fi (banda, canale, velocità)", "Wi-Fi details (band, channel, speed)", () => showWifiInfo, v => showWifiInfo = v);
            menu.Items.Add(miDetails);

            var miLook = Mi("Aspetto", "Appearance", null);
            string[] themeKeys = { "auto", "dark", "light" };
            string[] themeIt = { "Tema automatico", "Tema scuro", "Tema chiaro" };
            string[] themeEn = { "Automatic theme", "Dark theme", "Light theme" };
            miTheme = new ToolStripMenuItem[3];
            for (int i = 0; i < 3; i++)
            {
                string key = themeKeys[i];
                miTheme[i] = Mi(themeIt[i], themeEn[i], delegate { theme = key; ApplyLook(); SaveSettings(); });
                miTheme[i].Tag = key;
                miLook.DropDownItems.Add(miTheme[i]);
            }
            miLook.DropDownItems.Add(new ToolStripSeparator());
            var miOp = Mi("Opacità", "Opacity", null);
            miOpacity = new ToolStripMenuItem[OpacityValues.Length];
            for (int i = 0; i < OpacityValues.Length; i++)
            {
                int v = OpacityValues[i];
                miOpacity[i] = new ToolStripMenuItem(v + "%", null, delegate { opacity = v; ApplyLook(); SaveSettings(); });
                miOpacity[i].Tag = v;
                miOp.DropDownItems.Add(miOpacity[i]);
            }
            miLook.DropDownItems.Add(miOp);
            miCompact = Mi("Modalità compatta", "Compact mode", delegate { compact = !compact; ApplyLook(); SaveSettings(); });
            miLook.DropDownItems.Add(miCompact);
            menu.Items.Add(miLook);

            var miPingMenu = new ToolStripMenuItem("Ping");
            miPing = new ToolStripMenuItem[PingValues.Length];
            for (int i = 0; i < PingValues.Length; i++)
            {
                int v = PingValues[i];
                miPing[i] = v == 0 ? Mi("Disattivato", "Off", null) : Mi("Ogni {0} secondi", "Every {0} seconds", null, v);
                miPing[i].Click += delegate { SetPingInterval(v); SaveSettings(); };
                miPing[i].Tag = v;
                miPingMenu.DropDownItems.Add(miPing[i]);
                if (v == 0) miPingMenu.DropDownItems.Add(new ToolStripSeparator());
            }
            menu.Items.Add(miPingMenu);

            // Etichetta fissa in italiano e inglese: si trova anche se non si capisce la lingua attuale.
            // Le lingue elencate sono quelle incorporate nell'exe (italiano, inglese e i file lang\*.txt).
            var miLangMenu = new ToolStripMenuItem("Lingua / Language");
            var langKeys = new List<string> { "auto" };
            langKeys.AddRange(L.All().Select(x => x.Code));
            miLang = new ToolStripMenuItem[langKeys.Count];
            for (int i = 0; i < langKeys.Count; i++)
            {
                string key = langKeys[i];
                miLang[i] = i == 0 ? Mi("Automatica (come Windows)", "Automatic (same as Windows)", null)
                                   : new ToolStripMenuItem(L.All()[i - 1].Name);
                miLang[i].Click += delegate { SetLanguage(key); };
                miLang[i].Tag = key;
                miLangMenu.DropDownItems.Add(miLang[i]);
                if (i == 0) miLangMenu.DropDownItems.Add(new ToolStripSeparator());
            }
            menu.Items.Add(miLangMenu);
            UpdateLangChecks();

            miTopMost = Mi("Sempre in primo piano", "Always on top", delegate { SetTopMost(!topMostMode); SaveSettings(); });
            menu.Items.Add(miTopMost);
            miStartup = Mi("Avvia con Windows", "Start with Windows", delegate { ToggleStartup(); });
            miStartup.Checked = IsStartupEnabled();
            menu.Items.Add(miStartup);
            var miUpd = Mi("Aggiornamenti", "Updates", null);
            miUpd.DropDownItems.Add(Mi("Controlla ora", "Check now", delegate { CheckUpdates(true); }));
            Toggle(miUpd, "Controlla automaticamente (una volta al giorno)", "Check automatically (once a day)", () => autoUpdate, v =>
            {
                autoUpdate = v;
                if (v) CheckUpdates(false);
            });
            menu.Items.Add(miUpd);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Mi("Informazioni su AppIP", "About AppIP", delegate { AboutForm.ShowOnce(tray.Icon); }));
            menu.Items.Add(Mi("Esci", "Exit", delegate { Close(); }));
            ContextMenuStrip = menu;

            // ----- tray -----
            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            Icon = tray.Icon;
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowBalloon(); };
            tray.DoubleClick += delegate { ToggleWidget(); };
            tray.BalloonTipClicked += delegate { if (balloonIsUpdate) { balloonIsUpdate = false; OfferUpdate(); } };
            tray.BalloonTipClosed += delegate { balloonIsUpdate = false; };

            // ----- mouse: trascina per spostare, clic su un valore per copiarlo -----
            MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { pressing = true; downPt = e.Location; } };
            MouseMove += (s, e) =>
            {
                if (pressing && (Math.Abs(e.X - downPt.X) > 4 || Math.Abs(e.Y - downPt.Y) > 4))
                {
                    pressing = false;
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    SaveSettings();
                    return;
                }
                Cursor = HitAt(e.Location) != null ? Cursors.Hand : Cursors.Default;
            };
            MouseUp += (s, e) =>
            {
                if (!pressing) return;
                pressing = false;
                var h = HitAt(e.Location);
                if (h != null) CopyValue(h);
            };

            infoTimer = new WinTimer();
            fastTimer = new WinTimer();
            flashTimer = new WinTimer();
            flashTimer.Interval = 1200;
            flashTimer.Tick += delegate { flashTimer.Stop(); flashRect = Rectangle.Empty; Invalidate(); };

            if (demoArgs != null)
            {
                // Modalità demo: dati fittizi, nessuna impostazione letta/salvata, nessuna rete, niente tray/Jump List.
                demo = true;
                tray.Visible = false;
                var opts = new HashSet<string>(demoArgs.Select(a => a.TrimStart('/', '-').ToLowerInvariant()));
                theme = opts.Contains("light") ? "light" : "dark";
                compact = opts.Contains("compact");
                showIpv6 = opts.Contains("ipv6");
                opacity = 100;
                Location = new Point(60, 60);
                FillDemo(opts.Contains("vpn"));
                ApplyLook();
                pingInterval = 5; gwMs = 2; netMs = 14;
                ResizeToContent();
                return;
            }

            LoadSettings();
            ApplyLook();
            SetPingInterval(pingInterval);

            ListenCommand("front", BringToFront);
            ListenCommand("topmost", delegate { ShowWidget(); SetTopMost(!topMostMode); SaveSettings(); });
            ListenCommand("exit", Close);
            if (startCmd == "topmost") Load += delegate { SetTopMost(true); SaveSettings(); };

            Load += delegate { UpdateJumpList(); };

            infoTimer.Interval = 10000;
            infoTimer.Tick += delegate { RefreshInfo(); };
            infoTimer.Start();

            fastTimer.Interval = 1000;
            fastTimer.Tick += delegate { SampleSpeed(); if (Visible) Invalidate(); };
            fastTimer.Start();

            // Aggiornamenti: primo controllo un minuto dopo l'avvio, poi ogni 24 ore (solo se attivato)
            updTimer = new WinTimer();
            updTimer.Interval = 60000;
            updTimer.Tick += delegate
            {
                updTimer.Interval = 600000;
                if (autoUpdate && DateTime.Now - lastUpdCheck > TimeSpan.FromHours(24)) CheckUpdates(false);
            };
            updTimer.Start();

            NetworkChange.NetworkAddressChanged += delegate { lastPublicFetch = DateTime.MinValue; SafeRefresh(); };
            NetworkChange.NetworkAvailabilityChanged += delegate { lastPublicFetch = DateTime.MinValue; SafeRefresh(); };
            SystemEvents.UserPreferenceChanged += OnUserPrefChanged;

            var pingThread = new Thread(PingLoop);
            pingThread.IsBackground = true;
            pingThread.Start();

            RefreshInfo();
        }

        // Voce di menu con testo italiano/inglese, aggiornata automaticamente al cambio di lingua.
        // Con args, il testo contiene segnaposto {0}, {1}... (vedi L.F).
        ToolStripMenuItem Mi(string it, string en, EventHandler click, params object[] args)
        {
            Func<string> text = () => args.Length > 0 ? L.F(it, en, args) : L.T(it, en);
            var m = new ToolStripMenuItem(text(), null, click);
            relabel.Add(delegate { m.Text = text(); });
            return m;
        }

        // Voce con spunta in un sottomenu: legge/scrive un'opzione, salva e ridisegna.
        ToolStripMenuItem Toggle(ToolStripMenuItem parent, string it, string en, Func<bool> get, Action<bool> set)
        {
            var m = Mi(it, en, null);
            m.Click += delegate
            {
                set(!get());
                m.Checked = get();
                SaveSettings();
                ResizeToContent();
                Invalidate();
            };
            parent.DropDownItems.Add(m);
            parent.DropDownOpening += delegate { m.Checked = get(); };
            return m;
        }

        void SetLanguage(string key)
        {
            L.Set(key);
            foreach (var a in relabel) a();
            UpdateLangChecks();
            SaveSettings();
            if (!demo) UpdateJumpList();
            IPAddress parsedIp; // "non disponibile" va ritradotto; "..." e un indirizzo restano
            if (publicIp != "..." && !IPAddress.TryParse(publicIp, out parsedIp)) publicIp = L.T("non disponibile", "unavailable");
            if (publicIp6 != "..." && !IPAddress.TryParse(publicIp6, out parsedIp)) publicIp6 = L.T("non disponibile", "unavailable");
            UpdateTray();
            ResizeToContent();
            Invalidate();
        }

        void UpdateLangChecks()
        {
            foreach (var m in miLang) m.Checked = (string)m.Tag == L.Setting;
        }

        // Legge un singolo valore da settings.txt (serve prima di costruire il menu).
        string ReadSetting(string key)
        {
            try
            {
                if (File.Exists(settingsPath))
                    foreach (var ln in File.ReadAllLines(settingsPath))
                        if (ln.StartsWith(key + "=")) return ln.Substring(key.Length + 1).Trim();
            }
            catch { }
            return null;
        }

        void ListenCommand(string name, Action action)
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventPrefix + name);
            cmdEvents.Add(ev);
            ThreadPool.RegisterWaitForSingleObject(ev, delegate
            {
                try { BeginInvoke(action); } catch { }
            }, null, -1, false);
        }

        // Mostra il widget sopra le altre finestre finché non clicchi altrove.
        new void BringToFront()
        {
            ShowWidget();
            if (topMostMode) { Activate(); return; }
            tempFront = true;
            TopMost = true;
            Activate();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (tempFront)
            {
                tempFront = false;
                if (!topMostMode) TopMost = false; // il WndProc lo rimanda sotto alle altre finestre
            }
        }

        // ---------- Jump List (tasto destro sull'icona fissata nella barra delle applicazioni) ----------
        void UpdateJumpList()
        {
            try
            {
                string exe = Application.ExecutablePath;
                var jl = new System.Windows.Shell.JumpList();
                jl.ShowFrequentCategory = false;
                jl.ShowRecentCategory = false;
                jl.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = L.T("Mostra in primo piano", "Bring to front"),
                    Description = L.T("Porta il widget sopra le altre finestre", "Show the widget above other windows"),
                    ApplicationPath = exe, Arguments = "/front", IconResourcePath = exe
                });
                jl.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = L.T("Sempre in primo piano (sì/no)", "Always on top (on/off)"),
                    Description = L.T("Attiva o disattiva Sempre in primo piano", "Turn Always on top on or off"),
                    ApplicationPath = exe, Arguments = "/topmost", IconResourcePath = exe
                });
                jl.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = L.T("Esci da AppIP", "Exit AppIP"),
                    Description = L.T("Chiude l'app se è aperta", "Closes the app if it is running"),
                    ApplicationPath = exe, Arguments = "/exit", IconResourcePath = exe
                });
                jl.Apply();
            }
            catch { }
        }

        // All'uscita svuota la Jump List: le voci esistono solo mentre l'app è in esecuzione.
        static void ClearJumpList()
        {
            try
            {
                var jl = new System.Windows.Shell.JumpList();
                jl.ShowFrequentCategory = false;
                jl.ShowRecentCategory = false;
                jl.Apply();
            }
            catch { }
        }

        void OnUserPrefChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (theme == "auto") try { BeginInvoke(new Action(ApplyLook)); } catch { }
        }

        // ===================== Finestra / z-order =====================
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW; // niente Alt+Tab
                return cp;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS
        {
            public IntPtr hwnd, hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }
        const int WM_WINDOWPOSCHANGING = 0x46;
        const uint SWP_NOZORDER = 0x4;
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        // Modalità widget: tiene la finestra sempre sotto le altre, anche quando ci clicchi sopra.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_WINDOWPOSCHANGING && !topMostMode && !tempFront)
            {
                var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
                wp.hwndInsertAfter = HWND_BOTTOM;
                wp.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(wp, m.LParam, false);
            }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!topMostMode) PinToDesktop();
        }

        // Rende il widget "figlio" del desktop: resta visibile anche con Win+D.
        void PinToDesktop() { SetOwner(FindWindow("Progman", null)); }

        void SetOwner(IntPtr owner)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr(Handle, GWL_HWNDPARENT, owner);
            else SetWindowLong(Handle, GWL_HWNDPARENT, owner);
        }

        void SetTopMost(bool on)
        {
            topMostMode = on;
            miTopMost.Checked = on;
            if (!IsHandleCreated) return;
            if (on) { SetOwner(IntPtr.Zero); TopMost = true; }
            else { TopMost = false; PinToDesktop(); }
        }

        // ===================== Raccolta dati =====================
        void SafeRefresh()
        {
            try { if (IsHandleCreated) BeginInvoke(new Action(RefreshInfo)); } catch { }
        }

        static bool ContainsAny(string s, string[] words)
        {
            s = s.ToLowerInvariant();
            foreach (var w in words) if (s.Contains(w)) return true;
            return false;
        }

        void RefreshInfo()
        {
            if (demo) return;
            hostName = Environment.MachineName;
            try { domain = IPGlobalProperties.GetIPGlobalProperties().DomainName ?? ""; }
            catch { domain = ""; }

            var wifi = Wifi.Query();
            var result = new List<NetLine>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    string text = ni.Name + " " + ni.Description;
                    bool isVpn = ni.NetworkInterfaceType == NetworkInterfaceType.Ppp || ContainsAny(text, VpnWords);
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel && !isVpn) continue;

                    var props = ni.GetIPProperties();
                    var uni = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
                    // IPv6 utili: globali (2000::/3) e ULA (fc00::/7); esclusi link-local, site-local e Teredo
                    var uni6 = props.UnicastAddresses.Where(a => IsUseful6(a.Address)).ToList();
                    if (uni.Count == 0 && (!showIpv6 || uni6.Count == 0)) continue;
                    // Da mostrare: preferito, globale prima di ULA, e quello stabile prima dei temporanei (durata maggiore)
                    var best6 = uni6
                        .OrderBy(a => Dad(a) == DuplicateAddressDetectionState.Preferred ? 0 : 1)
                        .ThenBy(a => (a.Address.GetAddressBytes()[0] & 0xE0) == 0x20 ? 0 : 1)
                        .ThenByDescending(a => PrefLife(a))
                        .FirstOrDefault();
                    var dns6 = props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetworkV6 && !d.IsIPv6SiteLocal).Select(d => d.ToString()).ToList();

                    bool dhcp = false;
                    try { var v4 = props.GetIPv4Properties(); dhcp = v4 != null && v4.IsDhcpEnabled; } catch { }
                    var leaseEnd = DateTime.MinValue;
                    if (dhcp)
                    {
                        // AddressValidLifetime di un indirizzo DHCP = secondi che mancano alla scadenza del lease
                        var da = uni.FirstOrDefault(a => a.PrefixOrigin == PrefixOrigin.Dhcp);
                        long left = da != null ? ValidLife(da) : 0;
                        if (left > 0 && left < uint.MaxValue) leaseEnd = DateTime.Now.AddSeconds(left);
                    }
                    string dhcpSrv = "";
                    try { dhcpSrv = string.Join(", ", props.DhcpServerAddresses.Select(a => a.ToString())); } catch { }

                    var gw = props.GatewayAddresses
                        .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                        .Select(g => g.Address.ToString()).ToList();
                    var dns = props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList();
                    if (dns.Count == 0)
                        dns = props.DnsAddresses.Where(d => !d.IsIPv6SiteLocal).Select(d => d.ToString()).ToList();

                    var line = new NetLine
                    {
                        Id = ni.Id,
                        Name = ni.Name,
                        Description = ni.Description,
                        IPv4 = uni.Count > 0 ? string.Join(", ", uni.Select(a => a.Address + "/" + PrefixLen(a))) : "-",
                        IPv4Plain = uni.Count > 0 ? string.Join(", ", uni.Select(a => a.Address.ToString())) : "-",
                        IPv6 = best6 != null ? best6.Address.ToString() : "",
                        IPv6All = string.Join(", ", uni6.Select(a => a.Address + "/" + PrefixLen(a))),
                        Dns6 = string.Join(", ", dns6),
                        Dhcp = dhcp && uni.Count > 0,
                        LeaseEnd = leaseEnd,
                        DhcpServer = dhcpSrv,
                        Gateway = gw.Count > 0 ? string.Join(", ", gw) : "-",
                        HasGateway = gw.Count > 0,
                        Dns = dns.Count > 0 ? string.Join(", ", dns) : "-",
                        Mac = FormatMac(ni.GetPhysicalAddress()),
                        IsVpn = isVpn,
                        IsWifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                        Ni = ni
                    };
                    line.IsVirtual = !isVpn && ContainsAny(text, VirtualWords);
                    Wifi.Info wi;
                    if (wifi.TryGetValue(ni.Id, out wi))
                    {
                        line.IsWifi = true;
                        line.WifiSsid = wi.Ssid;
                        line.WifiSignal = wi.Signal;
                        line.WifiDenied = wi.Denied;
                        line.Wi = wi;
                    }
                    result.Add(line);
                }
            }
            catch { }

            // Prima le schede "vere" con gateway, poi VPN, poi il resto
            allLines = result.OrderBy(l => l.HasGateway && !l.IsVpn ? 0 : l.IsVpn ? 1 : 2).ToList();
            ApplyFilter();

            if (DateTime.Now - lastPublicFetch > TimeSpan.FromMinutes(5))
                FetchPublicIp();
        }

        bool IsShown(NetLine l)
        {
            if (shownIds.Contains(l.Id)) return true;
            if (hiddenIds.Contains(l.Id)) return false;
            return !(hideVirtual && l.IsVirtual);
        }

        void ApplyFilter()
        {
            lines = allLines.Where(IsShown).ToList();
            primary = lines.FirstOrDefault(l => l.HasGateway && !l.IsVpn) ?? lines.FirstOrDefault(l => !l.IsVpn) ?? lines.FirstOrDefault();
            vpn = allLines.FirstOrDefault(l => l.IsVpn);
            pingGateway = primary != null && primary.HasGateway ? primary.Gateway.Split(',')[0].Trim() : null;

            string id = primary != null ? primary.Id : null;
            if (id != speedId)
            {
                speedId = id;
                lastRx = lastTx = -1;
                rxRate = txRate = 0;
                rxHist.Clear(); txHist.Clear();
            }

            UpdateTray();
            ResizeToContent();
            Invalidate();
        }

        static int PrefixLen(UnicastIPAddressInformation a)
        {
            try { return a.PrefixLength; } catch { return 0; }
        }

        static long PrefLife(UnicastIPAddressInformation a)
        {
            try { return a.AddressPreferredLifetime; } catch { return 0; }
        }

        static long ValidLife(UnicastIPAddressInformation a)
        {
            try { return a.AddressValidLifetime; } catch { return 0; }
        }

        static DuplicateAddressDetectionState Dad(UnicastIPAddressInformation a)
        {
            try { return a.DuplicateAddressDetectionState; } catch { return DuplicateAddressDetectionState.Preferred; }
        }

        static bool IsUseful6(IPAddress a)
        {
            if (a.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || a.IsIPv6Teredo || IPAddress.IsLoopback(a)) return false;
            byte b = a.GetAddressBytes()[0];
            return (b & 0xE0) == 0x20 || (b & 0xFE) == 0xFC;
        }

        // Tempo che manca alla scadenza del lease DHCP, in forma breve (45m, 7h, 3g)
        static string LeftShort(DateTime end)
        {
            var t = end - DateTime.Now;
            if (t.TotalMinutes < 1) return "<1m";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalHours < 48) return (int)t.TotalHours + "h";
            return (int)t.TotalDays + L.T("g", "d");
        }

        string DhcpTag(NetLine l)
        {
            if (!l.Dhcp) return L.T("statico", "static");
            return l.LeaseEnd > DateTime.Now ? "DHCP · " + LeftShort(l.LeaseEnd) : "DHCP";
        }

        // Riga Wi-Fi per il widget: "5 GHz · ch 36 · Wi-Fi 6 · 866 Mbps"
        static string WifiShort(Wifi.Info w)
        {
            if (w == null) return "";
            var parts = new List<string>();
            if (w.Band().Length > 0) parts.Add(w.Band());
            if (w.Chan() > 0) parts.Add("ch " + w.Chan());
            if (w.Gen().Length > 0) parts.Add(w.Gen());
            int rate = Math.Max(w.RxMbps, w.TxMbps);
            if (rate > 0) parts.Add(rate + " Mbps");
            return string.Join(" · ", parts);
        }

        // Versione completa per "Copia tutto"
        static string WifiLong(Wifi.Info w)
        {
            if (w == null) return "";
            var parts = new List<string>();
            if (w.Band().Length > 0) parts.Add(w.Band());
            if (w.Chan() > 0) parts.Add(L.T("canale", "channel") + " " + w.Chan());
            if (w.Gen().Length > 0) parts.Add(w.Gen());
            if (w.RxMbps > 0 || w.TxMbps > 0) parts.Add(L.F("velocità {0}/{1} Mbps (ricezione/invio)", "rate {0}/{1} Mbps (receive/send)", Math.Max(0, w.RxMbps), Math.Max(0, w.TxMbps)));
            if (w.Rssi < 0) parts.Add(w.Rssi + " dBm");
            return string.Join(", ", parts);
        }

        static string FormatMac(PhysicalAddress pa)
        {
            var b = pa.GetAddressBytes();
            if (b.Length == 0) return "-";
            return string.Join(":", b.Select(x => x.ToString("X2")));
        }

        // ---------- Velocità ----------
        void SampleSpeed()
        {
            if (primary == null || primary.Ni == null) return;
            try
            {
                var st = primary.Ni.GetIPStatistics();
                long rx = st.BytesReceived, tx = st.BytesSent;
                var now = DateTime.UtcNow;
                if (lastRx >= 0)
                {
                    double sec = (now - lastSample).TotalSeconds;
                    if (sec > 0.2)
                    {
                        rxRate = Math.Max(0, (rx - lastRx) / sec);
                        txRate = Math.Max(0, (tx - lastTx) / sec);
                        rxHist.Add(rxRate); txHist.Add(txRate);
                        if (rxHist.Count > HistLen) { rxHist.RemoveAt(0); txHist.RemoveAt(0); }
                    }
                }
                lastRx = rx; lastTx = tx; lastSample = now;
            }
            catch { }
        }

        static string FormatRate(double b)
        {
            if (b < 1024) return b.ToString("0") + " B/s";
            if (b < 1024 * 1024) return (b / 1024).ToString(b < 10240 ? "0.0" : "0") + " KB/s";
            return (b / 1048576).ToString("0.0") + " MB/s";
        }

        // ---------- Ping ----------
        void SetPingInterval(int seconds)
        {
            pingInterval = seconds;
            gwMs = netMs = -2;
            foreach (var m in miPing) m.Checked = (int)m.Tag == seconds;
            pingWake.Set(); // sveglia il thread: parte subito col nuovo intervallo (o si ferma)
            ResizeToContent();
            Invalidate();
        }

        void PingLoop()
        {
            using (var p = new Ping())
            {
                while (true)
                {
                    int iv = pingInterval;
                    if (iv <= 0) { pingWake.WaitOne(); continue; } // disattivato: nessun traffico
                    string gw = pingGateway;
                    int g = gw == null ? -3 : DoPing(p, gw);
                    int n = DoPing(p, "1.1.1.1");
                    if (n < 0) n = DoPing(p, "8.8.8.8");
                    if (pingInterval == iv) { gwMs = g; netMs = n; }
                    pingWake.WaitOne(iv * 1000);
                }
            }
        }

        static int DoPing(Ping p, string host)
        {
            try
            {
                var r = p.Send(host, 1000);
                return r.Status == IPStatus.Success ? (int)r.RoundtripTime : -1;
            }
            catch { return -1; }
        }

        static string FormatPing(int ms)
        {
            if (ms == -2) return "...";
            if (ms == -3) return "-";
            if (ms < 0) return "timeout";
            if (ms < 1) return "<1 ms";
            return ms + " ms";
        }

        Color PingColor(int ms, Palette p)
        {
            if (ms == -2 || ms == -3) return p.Label;
            if (ms < 0) return p.Bad;
            if (ms < 60) return p.Good;
            if (ms < 150) return p.Warn;
            return p.Bad;
        }

        // ---------- IP pubblico ----------
        void FetchPublicIp()
        {
            if (Interlocked.Exchange(ref fetching, 1) == 1) return;
            lastPublicFetch = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                string ip = FetchIp(AddressFamily.InterNetwork, "https://api.ipify.org", "https://icanhazip.com", "https://checkip.amazonaws.com");
                // IPv6 pubblico solo se richiesto dal menu (nessuna connessione in più altrimenti)
                bool want6 = showIpv6;
                string ip6 = want6 ? FetchIp(AddressFamily.InterNetworkV6, "https://api6.ipify.org", "https://ipv6.icanhazip.com") : null;
                if (ip == null) lastPublicFetch = DateTime.Now - TimeSpan.FromMinutes(4); // riprova tra ~1 minuto
                fetching = 0;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        publicIp = ip ?? L.T("non disponibile", "unavailable");
                        if (want6) publicIp6 = ip6 ?? L.T("non disponibile", "unavailable");
                        UpdateTray();
                        Invalidate();
                    }));
                }
                catch { }
            });
        }

        static string FetchIp(AddressFamily family, params string[] urls)
        {
            foreach (var url in urls)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 5000;
                    req.UserAgent = "AppIP";
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream()))
                    {
                        IPAddress parsed;
                        if (IPAddress.TryParse(sr.ReadToEnd().Trim(), out parsed) && parsed.AddressFamily == family) return parsed.ToString();
                    }
                }
                catch { }
            }
            return null;
        }

        // ===================== Aggiornamenti =====================
        // Legge l'ultima release da GitHub; propone il download solo dell'installer pubblicato nel repository.
        void CheckUpdates(bool manual)
        {
            if (demo) return;
            if (Interlocked.Exchange(ref checkingUpd, 1) == 1) return;
            lastUpdCheck = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string tag = null, url = null, sha = null;
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    var req = (HttpWebRequest)WebRequest.Create(AppVersion.RepoUrl.Replace("https://github.com/", "https://api.github.com/repos/") + "/releases/latest");
                    req.Timeout = 10000;
                    req.UserAgent = "AppIP/" + AppVersion.Text;
                    req.Accept = "application/vnd.github+json";
                    string json;
                    using (var resp = req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream())) json = sr.ReadToEnd();
                    var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(\\.[0-9]+){1,3})\"");
                    if (m.Success) tag = m.Groups[1].Value;
                    m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(" + Regex.Escape(AppVersion.RepoUrl + "/releases/download/") + "[^\"]+\\.exe)\"");
                    if (m.Success) url = m.Groups[1].Value;
                    m = Regex.Match(json, "SHA-256:\\s*`?([0-9a-fA-F]{64})");
                    if (m.Success) sha = m.Groups[1].Value.ToLowerInvariant();
                }
                catch { }
                checkingUpd = 0;
                try { BeginInvoke(new Action(delegate { OnUpdateResult(tag, url, sha, manual); })); } catch { }
            });
        }

        void OnUpdateResult(string tag, string url, string sha, bool manual)
        {
            Version latest;
            if (tag == null || !Version.TryParse(tag, out latest))
            {
                if (manual) Msg(L.T("Impossibile controllare gli aggiornamenti. Verifica la connessione a Internet e riprova.",
                                    "Could not check for updates. Check your Internet connection and try again."), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (latest > new Version(AppVersion.Text))
            {
                bool isNew = updVersion != tag;
                updVersion = tag; updUrl = url; updSha = sha;
                miUpdateAvail.Text = L.F("Aggiorna ad AppIP {0}...", "Update to AppIP {0}...", tag);
                miUpdateAvail.Visible = true;
                if (manual) OfferUpdate();
                else if (isNew)
                {
                    tray.ShowBalloonTip(10000, L.F("AppIP {0} disponibile", "AppIP {0} available", tag),
                        L.T("Clicca qui per aggiornare.", "Click here to update."), ToolTipIcon.Info);
                    balloonIsUpdate = true;
                }
            }
            else if (manual)
                Msg(L.F("Hai già l'ultima versione di AppIP ({0}).", "You already have the latest version of AppIP ({0}).", AppVersion.Text),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OfferUpdate()
        {
            if (updVersion == null) return;
            string page = AppVersion.RepoUrl + "/releases/tag/v" + updVersion;
            if (updUrl == null) { OpenUrl(page); return; }
            var r = Msg(L.F("È disponibile AppIP {0} (installata: {1}).\n\nScaricare e installare ora? L'installer chiuderà AppIP durante l'aggiornamento; le impostazioni restano.\n\nNovità: {2}",
                            "AppIP {0} is available (installed: {1}).\n\nDownload and install it now? The installer will close AppIP during the update; your settings are kept.\n\nWhat's new: {2}",
                            updVersion, AppVersion.Text, page), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) DownloadUpdate(updUrl, updSha, updVersion, page);
        }

        void DownloadUpdate(string url, string sha, string ver, string page)
        {
            balloonIsUpdate = false;
            tray.ShowBalloonTip(3000, "AppIP", L.F("Download di AppIP {0} in corso...", "Downloading AppIP {0}...", ver), ToolTipIcon.Info);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string file = Path.Combine(Path.GetTempPath(), "AppIP-Setup-" + ver + ".exe");
                string err = null;
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "AppIP/" + AppVersion.Text;
                        wc.DownloadFile(url, file);
                    }
                    if (sha != null)
                    {
                        // Confronta con lo SHA-256 scritto nelle note della release
                        string got;
                        using (var st = File.OpenRead(file))
                        using (var h = SHA256.Create())
                            got = BitConverter.ToString(h.ComputeHash(st)).Replace("-", "").ToLowerInvariant();
                        if (got != sha)
                        {
                            File.Delete(file);
                            err = L.T("il file scaricato non corrisponde a quello pubblicato (SHA-256 diverso).", "the downloaded file does not match the published one (SHA-256 mismatch).");
                        }
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (err == null)
                        {
                            try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); return; }
                            catch (Exception ex) { err = ex.Message; }
                        }
                        if (Msg(L.F("Aggiornamento non riuscito: {0}\n\nAprire la pagina di download?", "Update failed: {0}\n\nOpen the download page?", err),
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            OpenUrl(page);
                    }));
                }
                catch { }
            });
        }

        // MessageBox visibile sopra le altre finestre (il widget sta sotto, sul desktop)
        static DialogResult Msg(string text, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using (var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                          StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0 })
            {
                owner.Show();
                owner.Activate();
                return MessageBox.Show(owner, text, "AppIP", buttons, icon);
            }
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ===================== Tray / copia =====================
        void UpdateTray()
        {
            string ip = primary != null ? primary.IPv4Plain : L.T("nessuna rete", "no network");
            string t = hostName + "\n" + ip + "\nPub: " + publicIp;
            if (t.Length > 63) t = t.Substring(0, 63);
            tray.Text = t;
        }

        void ShowBalloon()
        {
            var sb = new StringBuilder();
            balloonIsUpdate = false;
            sb.AppendLine(L.T("IP pubblico", "Public IP") + ": " + publicIp);
            if (vpn != null) sb.AppendLine("VPN: " + vpn.Name + " (" + vpn.IPv4Plain + ")");
            foreach (var l in lines.Take(2))
                sb.AppendLine(l.Name + ": " + l.IPv4Plain + "\nDNS: " + l.Dns);
            if (pingInterval > 0) sb.Append("Ping: " + FormatPing(netMs));
            tray.ShowBalloonTip(5000, hostName, sb.ToString().TrimEnd(), ToolTipIcon.None);
        }

        string BuildText()
        {
            var sb = new StringBuilder();
            // Etichette allineate qualunque sia la loro lunghezza nella lingua corrente
            string[] head = { L.T("Nome macchina", "Computer name"), L.T("IP pubblico", "Public IP"), L.T("VPN attiva", "Active VPN"), L.T("IP pubblico IPv6", "Public IPv6") };
            int pad = head.Max(h => h.Length) + 2;
            sb.AppendLine((head[0] + ":").PadRight(pad) + hostName + (domain.Length > 0 ? "." + domain : ""));
            sb.AppendLine((head[1] + ":").PadRight(pad) + publicIp);
            if (showIpv6) sb.AppendLine((head[3] + ":").PadRight(pad) + publicIp6);
            if (vpn != null) sb.AppendLine((head[2] + ":").PadRight(pad) + vpn.Name + " (" + vpn.IPv4Plain + ")");
            foreach (var l in lines)
            {
                sb.AppendLine();
                sb.AppendLine("[" + l.Name + "]" + (l.IsWifi && !string.IsNullOrEmpty(l.WifiSsid) ? " Wi-Fi: " + l.WifiSsid : ""));
                sb.AppendLine("IP:      " + l.IPv4);
                if (l.IPv6All.Length > 0) sb.AppendLine("IPv6:    " + l.IPv6All);
                sb.AppendLine("Gateway: " + l.Gateway);
                sb.AppendLine("DNS:     " + l.Dns);
                if (l.Dns6.Length > 0 && l.Dns6 != l.Dns) sb.AppendLine("DNS6:    " + l.Dns6);
                sb.AppendLine("MAC:     " + l.Mac);
                if (l.IPv4Plain != "-")
                {
                    string d = !l.Dhcp ? L.T("no (IP statico)", "no (static IP)")
                        : L.T("sì", "yes")
                          + (l.DhcpServer.Length > 0 ? ", server " + l.DhcpServer : "")
                          + (l.LeaseEnd > DateTime.Now ? ", " + L.F("lease fino al {0}", "lease until {0}", l.LeaseEnd.ToString("g")) : "");
                    sb.AppendLine("DHCP:    " + d);
                }
                string wl = WifiLong(l.Wi);
                if (wl.Length > 0) sb.AppendLine("Wi-Fi:   " + wl);
            }
            return sb.ToString();
        }

        void CopyAll()
        {
            try
            {
                Clipboard.SetText(BuildText());
                balloonIsUpdate = false;
                tray.ShowBalloonTip(1500, "AppIP", L.T("Informazioni copiate negli appunti", "Information copied to clipboard"), ToolTipIcon.Info);
            }
            catch { }
        }

        Hit HitAt(Point p)
        {
            foreach (var h in hits) if (h.R.Contains(p)) return h;
            return null;
        }

        void CopyValue(Hit h)
        {
            try { Clipboard.SetText(h.Text); } catch { return; }
            flashRect = h.R;
            flashTimer.Stop();
            flashTimer.Start();
            Invalidate();
        }

        // ===================== Disegno =====================
        const int Pad = 14;
        const int RowH = 18;
        const int MinValX = 46;
        int ValX = MinValX; // colonna dei valori: si allarga se un'etichetta tradotta è più lunga
        const int GraphH = 34;

        Palette Pal()
        {
            bool light = theme == "light" || (theme == "auto" && SystemIsLight());
            if (light)
                return new Palette
                {
                    Back = Color.FromArgb(248, 249, 251), Border = Color.FromArgb(205, 210, 220),
                    Label = Color.FromArgb(105, 112, 125), Value = Color.FromArgb(25, 28, 35),
                    Accent = Color.FromArgb(30, 110, 220), Pub = Color.FromArgb(20, 140, 80),
                    Good = Color.FromArgb(25, 150, 70), Warn = Color.FromArgb(190, 130, 0),
                    Bad = Color.FromArgb(200, 50, 50), Vpn = Color.FromArgb(210, 110, 0)
                };
            return new Palette
            {
                Back = Color.FromArgb(24, 26, 32), Border = Color.FromArgb(60, 66, 80),
                Label = Color.FromArgb(140, 150, 165), Value = Color.FromArgb(235, 238, 242),
                Accent = Color.FromArgb(90, 170, 255), Pub = Color.FromArgb(120, 220, 160),
                Good = Color.FromArgb(80, 210, 120), Warn = Color.FromArgb(240, 190, 60),
                Bad = Color.FromArgb(240, 100, 100), Vpn = Color.FromArgb(255, 170, 60)
            };
        }

        static bool SystemIsLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        void ApplyLook()
        {
            BackColor = Pal().Back;
            Opacity = opacity / 100.0;
            foreach (var m in miTheme) m.Checked = (string)m.Tag == theme;
            foreach (var m in miOpacity) m.Checked = (int)m.Tag == opacity;
            miCompact.Checked = compact;
            ResizeToContent();
            Invalidate();
        }

        void ResizeToContent()
        {
            int w = compact ? 270 : showIpv6 ? 380 : 330; // gli indirizzi IPv6 sono lunghi
            if (!compact && showWifiInfo)
                foreach (var l in lines)
                {
                    string ws = WifiShort(l.Wi);
                    if (ws.Length > 0) w = Math.Max(w, Math.Min(400, 2 * Pad + ValX + TextW(ws, fValue) + 4));
                }
            Width = w;
            Height = Render(null);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            hits = new List<Hit>();
            int h = Render(e.Graphics);
            if (h != Height) BeginInvoke(new Action(ResizeToContent));
        }

        int TextW(string s, Font f)
        {
            return TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding).Width;
        }

        int DrawT(Graphics g, string s, Font f, int x, int y, Color c)
        {
            if (g != null) TextRenderer.DrawText(g, s, f, new Point(x, y), c, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return TextW(s, f);
        }

        // Riga "etichetta  valore"; se copy != null il valore è cliccabile.
        // tag: testo piccolo allineato a destra (es. "DHCP · 7h"), non cliccabile.
        void Row(Graphics g, Palette p, ref int y, string label, string value, Color vc, string copy, string tag = null)
        {
            if (g != null)
            {
                DrawT(g, label, fLabel, Pad, y + 2, p.Label);
                var rect = new Rectangle(Pad + ValX, y, Width - Pad - ValX - Pad, RowH);
                if (tag != null)
                {
                    int tw = TextW(tag, fSmall);
                    DrawT(g, tag, fSmall, rect.Right - tw, y + 4, p.Label);
                    rect.Width -= tw + 8;
                }
                bool flashing = copy != null && rect == flashRect;
                TextRenderer.DrawText(g, value, fValue, rect, flashing ? p.Good : vc,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                if (flashing)
                {
                    string ok = L.T("copiato ✓", "copied ✓");
                    int w = TextW(ok, fSmall);
                    using (var b = new SolidBrush(p.Back)) g.FillRectangle(b, rect.Right - w - 6, y, w + 6, RowH);
                    DrawT(g, ok, fSmall, rect.Right - w, y + 3, p.Good);
                }
                if (copy != null && value != "-")
                    hits.Add(new Hit { R = rect, Text = copy });
            }
            y += RowH;
        }

        int Render(Graphics g)
        {
            var p = Pal();
            int W = Width;
            int y = Pad;
            ValX = Math.Max(MinValX, TextW(L.T("RETE", "SPEED"), fLabel) + 10);

            if (g != null)
            {
                using (var pen = new Pen(p.Border)) g.DrawRectangle(pen, 0, 0, W - 1, Height - 1);
                using (var b = new SolidBrush(vpn != null ? p.Vpn : p.Accent)) g.FillRectangle(b, 0, 0, 3, Height);
            }

            // Titolo (cliccabile: copia il nome macchina) + pallino stato Internet
            if (g != null)
            {
                var nameRect = new Rectangle(Pad, y, TextW(hostName, fTitle), 24);
                bool flashing = hostName.Length > 0 && nameRect == flashRect;
                DrawT(g, hostName, fTitle, Pad, y, flashing ? p.Good : p.Value);
                if (flashing) DrawT(g, L.T("copiato ✓", "copied ✓"), fSmall, nameRect.Right + 8, y + 8, p.Good);
                if (hostName.Length > 0) hits.Add(new Hit { R = nameRect, Text = hostName });
            }
            if (g != null)
            {
                int tagRight = W - Pad;
                if (pingInterval > 0)
                {
                    using (var b = new SolidBrush(PingColor(netMs, p)))
                        g.FillEllipse(b, W - Pad - 10, y + 7, 10, 10);
                    tagRight -= 16;
                }
                if (vpn != null)
                {
                    string tag = "VPN";
                    int tw = TextW(tag, fSmall) + 8;
                    var r = new Rectangle(tagRight - tw, y + 4, tw, 16);
                    using (var b = new SolidBrush(p.Vpn)) g.FillRectangle(b, r);
                    TextRenderer.DrawText(g, tag, fSmall, r, p.Back, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            y += 26;
            if (!compact && domain.Length > 0) { DrawT(g, domain, fLabel, Pad, y, p.Label); y += 16; }
            y += 4;

            if (compact && primary != null)
                Row(g, p, ref y, "IP", primary.IPv4Plain, p.Value, primary.IPv4Plain);
            Row(g, p, ref y, "PUB", publicIp, p.Pub, publicIp.Contains(".") || publicIp.Contains(":") ? publicIp : null);
            if (showIpv6 && !compact)
                Row(g, p, ref y, "PUB6", publicIp6, p.Pub, publicIp6.Contains(":") ? publicIp6 : null);

            // Ping (solo se attivato)
            if (pingInterval > 0)
            {
                if (g != null)
                {
                    DrawT(g, "PING", fLabel, Pad, y + 2, p.Label);
                    int x = Pad + ValX;
                    x += DrawT(g, "GW ", fLabel, x, y + 2, p.Label);
                    x += DrawT(g, FormatPing(gwMs), fValue, x, y + 1, PingColor(gwMs, p)) + 12;
                    x += DrawT(g, "NET ", fLabel, x, y + 2, p.Label);
                    DrawT(g, FormatPing(netMs), fValue, x, y + 1, PingColor(netMs, p));
                }
                y += RowH;
            }

            if (vpn != null)
                Row(g, p, ref y, "VPN", vpn.IPv4Plain + "  " + vpn.Name, p.Vpn, vpn.IPv4Plain);

            // Velocità
            if (g != null)
            {
                DrawT(g, L.T("RETE", "SPEED"), fLabel, Pad, y + 2, p.Label);
                int x = Pad + ValX;
                x += DrawT(g, "↓ " + FormatRate(rxRate), fValue, x, y + 1, p.Accent) + 14;
                DrawT(g, "↑ " + FormatRate(txRate), fValue, x, y + 1, p.Good);
            }
            y += RowH;
            if (!compact)
            {
                y += 2;
                if (g != null) DrawGraph(g, p, new Rectangle(Pad, y, W - 2 * Pad, GraphH));
                y += GraphH + 4;
            }

            // Schede di rete
            if (!compact)
            {
                y += 6;
                if (lines.Count == 0)
                {
                    DrawT(g, L.T("Nessuna rete attiva", "No active network"), fValue, Pad, y, p.Bad);
                    y += RowH;
                }
                foreach (var l in lines)
                {
                    int x = Pad;
                    x += DrawT(g, l.Name.ToUpperInvariant(), fLabel, x, y, l.IsVpn ? p.Vpn : p.Accent);
                    if (l.IsWifi)
                    {
                        string ssid = !string.IsNullOrEmpty(l.WifiSsid) ? l.WifiSsid : (l.WifiDenied ? L.T("nome nascosto (attiva Posizione)", "name hidden (turn on Location)") : "");
                        if (ssid.Length > 0) x += DrawT(g, "  " + ssid, fLabel, x, y, p.Value);
                        if (l.WifiSignal >= 0)
                        {
                            Color sc = l.WifiSignal >= 60 ? p.Good : l.WifiSignal >= 35 ? p.Warn : p.Bad;
                            x += DrawT(g, "  " + l.WifiSignal + "%", fLabel, x, y, sc);
                            if (g != null) DrawSignalBars(g, p, x + 6, y + 2, l.WifiSignal, sc);
                        }
                    }
                    y += 16;
                    Row(g, p, ref y, "IP", l.IPv4, p.Value, l.IPv4Plain, showDhcp && l.IPv4Plain != "-" ? DhcpTag(l) : null);
                    if (showIpv6 && l.IPv6.Length > 0) Row(g, p, ref y, "IPv6", l.IPv6, p.Value, l.IPv6);
                    Row(g, p, ref y, "GW", l.Gateway, p.Value, l.Gateway);
                    Row(g, p, ref y, "DNS", l.Dns, p.Value, l.Dns);
                    if (showMac && l.Mac != "-") Row(g, p, ref y, "MAC", l.Mac, p.Value, l.Mac);
                    string ws = showWifiInfo ? WifiShort(l.Wi) : "";
                    if (ws.Length > 0) Row(g, p, ref y, "Wi-Fi", ws, p.Value, ws);
                    y += 8;
                }
                y -= 8;
            }

            return y + Pad;
        }

        void DrawSignalBars(Graphics g, Palette p, int x, int y, int q, Color c)
        {
            int bars = q >= 80 ? 4 : q >= 55 ? 3 : q >= 30 ? 2 : q > 0 ? 1 : 0;
            for (int i = 0; i < 4; i++)
            {
                int h = 3 + i * 3;
                using (var b = new SolidBrush(i < bars ? c : Color.FromArgb(70, p.Label)))
                    g.FillRectangle(b, x + i * 4, y + 12 - h, 3, h);
            }
        }

        void DrawGraph(Graphics g, Palette p, Rectangle r)
        {
            using (var b = new SolidBrush(Color.FromArgb(18, p.Label))) g.FillRectangle(b, r);
            int n = rxHist.Count;
            if (n < 2)
            {
                DrawT(g, L.T("raccolta dati...", "collecting data..."), fSmall, r.X + 4, r.Y + 3, p.Label);
                return;
            }
            double max = 10 * 1024;
            for (int i = 0; i < n; i++) max = Math.Max(max, Math.Max(rxHist[i], txHist[i]));
            float step = (float)r.Width / (HistLen - 1);

            Func<List<double>, PointF[]> pts = hist =>
            {
                var a = new PointF[n];
                for (int i = 0; i < n; i++)
                    a[i] = new PointF(r.Right - (n - 1 - i) * step, (float)(r.Bottom - 1 - hist[i] / max * (r.Height - 4)));
                return a;
            };

            var down = pts(rxHist);
            var poly = new List<PointF>(down);
            poly.Add(new PointF(down[n - 1].X, r.Bottom));
            poly.Add(new PointF(down[0].X, r.Bottom));
            using (var b = new SolidBrush(Color.FromArgb(60, p.Accent))) g.FillPolygon(b, poly.ToArray());
            using (var pen = new Pen(p.Accent, 1.5f)) g.DrawLines(pen, down);
            using (var pen = new Pen(p.Good, 1.2f)) g.DrawLines(pen, pts(txHist));

            DrawT(g, "max " + FormatRate(max), fSmall, r.X + 4, r.Y + 2, p.Label);
        }

        // ===================== Icona =====================
        static Icon MakeIcon()
        {
            // Icona incorporata (appip.ico) alla dimensione giusta per la tray e il DPI corrente
            try
            {
                using (var st = typeof(WidgetForm).Assembly.GetManifestResourceStream("AppIP.appip.ico"))
                    if (st != null) return new Icon(st, SystemInformation.SmallIconSize);
            }
            catch { }
            return DrawFallbackIcon();
        }

        static Icon DrawFallbackIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Color.FromArgb(40, 120, 220)))
                    g.FillEllipse(b, 0, 0, 31, 31);
                using (var f = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("IP", f, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                return icon;
            }
        }

        // ===================== Menu schede =====================
        void BuildAdaptersMenu()
        {
            miAdapters.DropDownItems.Clear();
            if (allLines.Count == 0) miAdapters.DropDownItems.Add(new ToolStripMenuItem(L.T("(nessuna scheda attiva)", "(no active adapters)")) { Enabled = false });
            foreach (var l in allLines)
            {
                var line = l;
                string label = l.Name + (l.IsVpn ? "  (VPN)" : l.IsVirtual ? "  (" + L.T("virtuale", "virtual") + ")" : "");
                var item = new ToolStripMenuItem(label, null, delegate
                {
                    if (IsShown(line)) { shownIds.Remove(line.Id); hiddenIds.Add(line.Id); }
                    else { hiddenIds.Remove(line.Id); shownIds.Add(line.Id); }
                    SaveSettings();
                    ApplyFilter();
                });
                item.Checked = IsShown(l);
                item.ToolTipText = l.Description;
                miAdapters.DropDownItems.Add(item);
            }
            miAdapters.DropDownItems.Add(new ToolStripSeparator());
            miHideVirtual.Checked = hideVirtual;
            miAdapters.DropDownItems.Add(miHideVirtual);
            if (hiddenIds.Count > 0 || shownIds.Count > 0)
                miAdapters.DropDownItems.Add(new ToolStripMenuItem(L.T("Ripristina predefinite", "Restore defaults"), null, delegate
                {
                    hiddenIds.Clear(); shownIds.Clear(); SaveSettings(); ApplyFilter();
                }));
        }

        // ===================== Impostazioni =====================
        void ToggleWidget()
        {
            if (Visible) Hide(); else { Show(); if (!topMostMode) PinToDesktop(); }
            miShow.Checked = Visible;
            SaveSettings();
        }

        void ShowWidget()
        {
            if (!Visible) ToggleWidget();
        }

        bool IsStartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(AppName) != null;
        }

        void ToggleStartup()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                if (IsStartupEnabled()) k.DeleteValue(AppName, false);
                else k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            }
            miStartup.Checked = IsStartupEnabled();
        }

        void LoadSettings()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 20, wa.Top + 20);
            bool visible = true;
            try
            {
                if (File.Exists(settingsPath))
                {
                    string raw = File.ReadAllText(settingsPath).Trim();
                    var d = new Dictionary<string, string>();
                    if (!raw.Contains("="))
                    {
                        // vecchio formato: x;y;topmost;visibile
                        var o = raw.Split(';');
                        string[] keys = { "x", "y", "topmost", "visible" };
                        for (int i = 0; i < o.Length && i < keys.Length; i++) d[keys[i]] = o[i];
                    }
                    else
                    {
                        foreach (var ln in raw.Split('\n'))
                        {
                            int eq = ln.IndexOf('=');
                            if (eq > 0) d[ln.Substring(0, eq).Trim()] = ln.Substring(eq + 1).Trim();
                        }
                    }
                    string v;
                    if (d.ContainsKey("x") && d.ContainsKey("y"))
                    {
                        var pt = new Point(int.Parse(d["x"]), int.Parse(d["y"]));
                        if (SystemInformation.VirtualScreen.Contains(pt)) Location = pt;
                    }
                    if (d.TryGetValue("topmost", out v)) topMostMode = v == "1";
                    if (d.TryGetValue("visible", out v)) visible = v != "0";
                    if (d.TryGetValue("compact", out v)) compact = v == "1";
                    if (d.TryGetValue("hideVirtual", out v)) hideVirtual = v != "0";
                    if (d.TryGetValue("ipv6", out v)) showIpv6 = v == "1";
                    if (d.TryGetValue("mac", out v)) showMac = v != "0";
                    if (d.TryGetValue("dhcp", out v)) showDhcp = v != "0";
                    if (d.TryGetValue("wifiinfo", out v)) showWifiInfo = v != "0";
                    if (d.TryGetValue("updates", out v)) autoUpdate = v == "1";
                    if (d.TryGetValue("speedtest", out v)) speedServer = v;
                    if (d.TryGetValue("theme", out v) && (v == "auto" || v == "dark" || v == "light")) theme = v;
                    int op;
                    if (d.TryGetValue("ping", out v) && int.TryParse(v, out op) && PingValues.Contains(op)) pingInterval = op;
                    if (d.TryGetValue("opacity", out v) && int.TryParse(v, out op) && op >= 30 && op <= 100) opacity = op;
                    if (d.TryGetValue("hidden", out v)) foreach (var s in v.Split('|')) if (s.Length > 0) hiddenIds.Add(s);
                    if (d.TryGetValue("shown", out v)) foreach (var s in v.Split('|')) if (s.Length > 0) shownIds.Add(s);
                }
            }
            catch { }
            miTopMost.Checked = topMostMode;
            TopMost = topMostMode;
            miHideVirtual.Checked = hideVirtual;
            if (!visible)
            {
                miShow.Checked = false;
                Load += delegate { BeginInvoke(new Action(Hide)); };
            }
        }

        void SaveSettings()
        {
            if (demo) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                var sb = new StringBuilder();
                sb.Append("x=").Append(Left).Append('\n');
                sb.Append("y=").Append(Top).Append('\n');
                sb.Append("topmost=").Append(topMostMode ? "1" : "0").Append('\n');
                sb.Append("visible=").Append(Visible ? "1" : "0").Append('\n');
                sb.Append("compact=").Append(compact ? "1" : "0").Append('\n');
                sb.Append("hideVirtual=").Append(hideVirtual ? "1" : "0").Append('\n');
                sb.Append("theme=").Append(theme).Append('\n');
                sb.Append("opacity=").Append(opacity).Append('\n');
                sb.Append("ping=").Append(pingInterval).Append('\n');
                sb.Append("hidden=").Append(string.Join("|", hiddenIds)).Append('\n');
                sb.Append("shown=").Append(string.Join("|", shownIds)).Append('\n');
                sb.Append("lang=").Append(L.Setting).Append('\n');
                sb.Append("ipv6=").Append(showIpv6 ? "1" : "0").Append('\n');
                sb.Append("mac=").Append(showMac ? "1" : "0").Append('\n');
                sb.Append("dhcp=").Append(showDhcp ? "1" : "0").Append('\n');
                sb.Append("wifiinfo=").Append(showWifiInfo ? "1" : "0").Append('\n');
                sb.Append("updates=").Append(autoUpdate ? "1" : "0").Append('\n');
                sb.Append("speedtest=").Append(speedServer).Append('\n');
                File.WriteAllText(settingsPath, sb.ToString());
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            if (!demo) ClearJumpList();
            SystemEvents.UserPreferenceChanged -= OnUserPrefChanged;
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }
    }
}
