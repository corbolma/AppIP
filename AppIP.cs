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
using System.Text;
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
        public bool HasGateway, IsVpn, IsVirtual, IsWifi;
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

        public class Info { public string Ssid; public int Signal = -1; public bool Denied; }

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
                            }
                            finally { WlanFreeMemory(data); }
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
        readonly ToolStripMenuItem miTopMost, miStartup, miShow, miAdapters, miCompact, miHideVirtual;
        readonly ToolStripMenuItem[] miTheme, miOpacity;
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
        string publicIp = "...";
        DateTime lastPublicFetch = DateTime.MinValue;
        int fetching;

        // impostazioni
        bool topMostMode, compact, hideVirtual = true;
        string theme = "auto";
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

        public WidgetForm(string startCmd)
        {
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppIP", "settings.txt");

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Width = 330;
            Height = 200;

            // ----- menu -----
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("AppIP " + AppVersion.Text) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            miShow = new ToolStripMenuItem("Mostra widget", null, delegate { ToggleWidget(); });
            miShow.Checked = true;
            menu.Items.Add(miShow);
            menu.Items.Add(new ToolStripMenuItem("Aggiorna", null, delegate { lastPublicFetch = DateTime.MinValue; RefreshInfo(); }));
            menu.Items.Add(new ToolStripMenuItem("Copia tutto", null, delegate { CopyAll(); }));
            menu.Items.Add(new ToolStripSeparator());

            miAdapters = new ToolStripMenuItem("Schede di rete");
            miAdapters.DropDownItems.Add("(nessuna)");
            miAdapters.DropDownOpening += delegate { BuildAdaptersMenu(); };
            miHideVirtual = new ToolStripMenuItem("Nascondi schede virtuali", null, delegate
            {
                hideVirtual = !hideVirtual; miHideVirtual.Checked = hideVirtual; SaveSettings(); ApplyFilter();
            });
            menu.Items.Add(miAdapters);

            var miLook = new ToolStripMenuItem("Aspetto");
            string[] themeKeys = { "auto", "dark", "light" };
            string[] themeNames = { "Tema automatico", "Tema scuro", "Tema chiaro" };
            miTheme = new ToolStripMenuItem[3];
            for (int i = 0; i < 3; i++)
            {
                string key = themeKeys[i];
                miTheme[i] = new ToolStripMenuItem(themeNames[i], null, delegate { theme = key; ApplyLook(); SaveSettings(); });
                miTheme[i].Tag = key;
                miLook.DropDownItems.Add(miTheme[i]);
            }
            miLook.DropDownItems.Add(new ToolStripSeparator());
            var miOp = new ToolStripMenuItem("Opacità");
            miOpacity = new ToolStripMenuItem[OpacityValues.Length];
            for (int i = 0; i < OpacityValues.Length; i++)
            {
                int v = OpacityValues[i];
                miOpacity[i] = new ToolStripMenuItem(v + "%", null, delegate { opacity = v; ApplyLook(); SaveSettings(); });
                miOpacity[i].Tag = v;
                miOp.DropDownItems.Add(miOpacity[i]);
            }
            miLook.DropDownItems.Add(miOp);
            miCompact = new ToolStripMenuItem("Modalità compatta", null, delegate { compact = !compact; ApplyLook(); SaveSettings(); });
            miLook.DropDownItems.Add(miCompact);
            menu.Items.Add(miLook);

            var miPingMenu = new ToolStripMenuItem("Ping");
            miPing = new ToolStripMenuItem[PingValues.Length];
            for (int i = 0; i < PingValues.Length; i++)
            {
                int v = PingValues[i];
                miPing[i] = new ToolStripMenuItem(v == 0 ? "Disattivato" : "Ogni " + v + " secondi", null, delegate { SetPingInterval(v); SaveSettings(); });
                miPing[i].Tag = v;
                miPingMenu.DropDownItems.Add(miPing[i]);
                if (v == 0) miPingMenu.DropDownItems.Add(new ToolStripSeparator());
            }
            menu.Items.Add(miPingMenu);

            miTopMost = new ToolStripMenuItem("Sempre in primo piano", null, delegate { SetTopMost(!topMostMode); SaveSettings(); });
            menu.Items.Add(miTopMost);
            miStartup = new ToolStripMenuItem("Avvia con Windows", null, delegate { ToggleStartup(); });
            miStartup.Checked = IsStartupEnabled();
            menu.Items.Add(miStartup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Esci", null, delegate { Close(); }));
            ContextMenuStrip = menu;

            // ----- tray -----
            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            Icon = tray.Icon;
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowBalloon(); };
            tray.DoubleClick += delegate { ToggleWidget(); };

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

            LoadSettings();
            ApplyLook();
            SetPingInterval(pingInterval);

            ListenCommand("front", BringToFront);
            ListenCommand("topmost", delegate { ShowWidget(); SetTopMost(!topMostMode); SaveSettings(); });
            ListenCommand("exit", Close);
            if (startCmd == "topmost") Load += delegate { SetTopMost(true); SaveSettings(); };

            Load += delegate { UpdateJumpList(); };

            infoTimer = new WinTimer();
            infoTimer.Interval = 10000;
            infoTimer.Tick += delegate { RefreshInfo(); };
            infoTimer.Start();

            fastTimer = new WinTimer();
            fastTimer.Interval = 1000;
            fastTimer.Tick += delegate { SampleSpeed(); if (Visible) Invalidate(); };
            fastTimer.Start();

            flashTimer = new WinTimer();
            flashTimer.Interval = 1200;
            flashTimer.Tick += delegate { flashTimer.Stop(); flashRect = Rectangle.Empty; Invalidate(); };

            NetworkChange.NetworkAddressChanged += delegate { lastPublicFetch = DateTime.MinValue; SafeRefresh(); };
            NetworkChange.NetworkAvailabilityChanged += delegate { lastPublicFetch = DateTime.MinValue; SafeRefresh(); };
            SystemEvents.UserPreferenceChanged += OnUserPrefChanged;

            var pingThread = new Thread(PingLoop);
            pingThread.IsBackground = true;
            pingThread.Start();

            RefreshInfo();
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
                    Title = "Mostra in primo piano", Description = "Porta il widget sopra le altre finestre",
                    ApplicationPath = exe, Arguments = "/front", IconResourcePath = exe
                });
                jl.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = "Sempre in primo piano (sì/no)", Description = "Attiva o disattiva Sempre in primo piano",
                    ApplicationPath = exe, Arguments = "/topmost", IconResourcePath = exe
                });
                jl.JumpItems.Add(new System.Windows.Shell.JumpTask
                {
                    Title = "Esci da AppIP", Description = "Chiude l'app se è aperta",
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
                    if (uni.Count == 0) continue;

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
                        IPv4 = string.Join(", ", uni.Select(a => a.Address + "/" + PrefixLen(a))),
                        IPv4Plain = string.Join(", ", uni.Select(a => a.Address.ToString())),
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
                string ip = null;
                string[] urls = { "https://api.ipify.org", "https://icanhazip.com", "https://checkip.amazonaws.com" };
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
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
                            if (IPAddress.TryParse(sr.ReadToEnd().Trim(), out parsed)) { ip = parsed.ToString(); break; }
                        }
                    }
                    catch { }
                }
                if (ip == null) lastPublicFetch = DateTime.Now - TimeSpan.FromMinutes(4); // riprova tra ~1 minuto
                fetching = 0;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        publicIp = ip ?? "non disponibile";
                        UpdateTray();
                        Invalidate();
                    }));
                }
                catch { }
            });
        }

        // ===================== Tray / copia =====================
        void UpdateTray()
        {
            string ip = primary != null ? primary.IPv4Plain : "nessuna rete";
            string t = hostName + "\n" + ip + "\nPub: " + publicIp;
            if (t.Length > 63) t = t.Substring(0, 63);
            tray.Text = t;
        }

        void ShowBalloon()
        {
            var sb = new StringBuilder();
            sb.AppendLine("IP pubblico: " + publicIp);
            if (vpn != null) sb.AppendLine("VPN: " + vpn.Name + " (" + vpn.IPv4Plain + ")");
            foreach (var l in lines.Take(2))
                sb.AppendLine(l.Name + ": " + l.IPv4Plain + "\nDNS: " + l.Dns);
            if (pingInterval > 0) sb.Append("Ping: " + FormatPing(netMs));
            tray.ShowBalloonTip(5000, hostName, sb.ToString().TrimEnd(), ToolTipIcon.None);
        }

        string BuildText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Nome macchina: " + hostName + (domain.Length > 0 ? "." + domain : ""));
            sb.AppendLine("IP pubblico:   " + publicIp);
            if (vpn != null) sb.AppendLine("VPN attiva:    " + vpn.Name + " (" + vpn.IPv4Plain + ")");
            foreach (var l in lines)
            {
                sb.AppendLine();
                sb.AppendLine("[" + l.Name + "]" + (l.IsWifi && !string.IsNullOrEmpty(l.WifiSsid) ? " Wi-Fi: " + l.WifiSsid : ""));
                sb.AppendLine("IP:      " + l.IPv4);
                sb.AppendLine("Gateway: " + l.Gateway);
                sb.AppendLine("DNS:     " + l.Dns);
                sb.AppendLine("MAC:     " + l.Mac);
            }
            return sb.ToString();
        }

        void CopyAll()
        {
            try
            {
                Clipboard.SetText(BuildText());
                tray.ShowBalloonTip(1500, "AppIP", "Informazioni copiate negli appunti", ToolTipIcon.Info);
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
        const int ValX = 46;
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
            Width = compact ? 270 : 330;
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
        void Row(Graphics g, Palette p, ref int y, string label, string value, Color vc, string copy)
        {
            if (g != null)
            {
                DrawT(g, label, fLabel, Pad, y + 2, p.Label);
                var rect = new Rectangle(Pad + ValX, y, Width - Pad - ValX - Pad, RowH);
                bool flashing = copy != null && rect == flashRect;
                TextRenderer.DrawText(g, value, fValue, rect, flashing ? p.Good : vc,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                if (flashing)
                {
                    string ok = "copiato ✓";
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

            if (g != null)
            {
                using (var pen = new Pen(p.Border)) g.DrawRectangle(pen, 0, 0, W - 1, Height - 1);
                using (var b = new SolidBrush(vpn != null ? p.Vpn : p.Accent)) g.FillRectangle(b, 0, 0, 3, Height);
            }

            // Titolo + pallino stato Internet
            DrawT(g, hostName, fTitle, Pad, y, p.Value);
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
                DrawT(g, "RETE", fLabel, Pad, y + 2, p.Label);
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
                    DrawT(g, "Nessuna rete attiva", fValue, Pad, y, p.Bad);
                    y += RowH;
                }
                foreach (var l in lines)
                {
                    int x = Pad;
                    x += DrawT(g, l.Name.ToUpperInvariant(), fLabel, x, y, l.IsVpn ? p.Vpn : p.Accent);
                    if (l.IsWifi)
                    {
                        string ssid = !string.IsNullOrEmpty(l.WifiSsid) ? l.WifiSsid : (l.WifiDenied ? "nome nascosto (attiva Posizione)" : "");
                        if (ssid.Length > 0) x += DrawT(g, "  " + ssid, fLabel, x, y, p.Value);
                        if (l.WifiSignal >= 0)
                        {
                            Color sc = l.WifiSignal >= 60 ? p.Good : l.WifiSignal >= 35 ? p.Warn : p.Bad;
                            x += DrawT(g, "  " + l.WifiSignal + "%", fLabel, x, y, sc);
                            if (g != null) DrawSignalBars(g, p, x + 6, y + 2, l.WifiSignal, sc);
                        }
                    }
                    y += 16;
                    Row(g, p, ref y, "IP", l.IPv4, p.Value, l.IPv4Plain);
                    Row(g, p, ref y, "GW", l.Gateway, p.Value, l.Gateway);
                    Row(g, p, ref y, "DNS", l.Dns, p.Value, l.Dns);
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
                DrawT(g, "raccolta dati...", fSmall, r.X + 4, r.Y + 3, p.Label);
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
            if (allLines.Count == 0) miAdapters.DropDownItems.Add(new ToolStripMenuItem("(nessuna scheda attiva)") { Enabled = false });
            foreach (var l in allLines)
            {
                var line = l;
                string label = l.Name + (l.IsVpn ? "  (VPN)" : l.IsVirtual ? "  (virtuale)" : "");
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
                miAdapters.DropDownItems.Add(new ToolStripMenuItem("Ripristina predefinite", null, delegate
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
                File.WriteAllText(settingsPath, sb.ToString());
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            ClearJumpList();
            SystemEvents.UserPreferenceChanged -= OnUserPrefChanged;
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }
    }
}
