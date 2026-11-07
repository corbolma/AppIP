// Test di velocità della connessione Internet (ADSL, fibra, 4G...): latenza, download e upload.
// Parte solo su richiesta dal menu: nessun traffico automatico.
//
// Server: per impostazione predefinita speed.cloudflare.com (endpoint __down?bytes=N e __up).
// Un URL personalizzato può essere:
//  - la base di un server compatibile con quelle API (es. https://speed.example.com) -> test completo;
//  - l'indirizzo di un file da scaricare (es. https://server/test.bin) -> solo latenza e download.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace AppIP
{
    class SpeedTestForm : Form
    {
        public const string DefaultServer = "https://speed.cloudflare.com";

        const int PingCount = 10;
        const int DownStreams = 6, UpStreams = 4;
        const int PhaseMs = 10000;   // durata di download e upload
        const int WarmupMs = 1500;   // i primi istanti (avvio TCP) non entrano nel risultato
        // Cloudflare blocca per circa un'ora i download da 10 MB in su dopo pochi test: blocchi da 5 MB
        const int DownChunk = 5000000, UpChunk = 25000000;

        static SpeedTestForm open;

        public static void ShowOnce(Icon icon, Func<string> getServer, Action<string> setServer)
        {
            if (open == null || open.IsDisposed) open = new SpeedTestForm(icon, getServer, setServer);
            open.Show();
            open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        readonly Func<string> getServer;
        readonly Action<string> setServer;
        readonly Label lblPing, lblJitter, lblDown, lblUp, lblStatus, lblInfo;
        readonly TextBox txtServer;
        readonly ProgressBar bar;
        readonly Button btnStart, btnCopy;

        volatile bool cancel;
        Thread worker;
        readonly List<HttpWebRequest> active = new List<HttpWebRequest>();
        Exception lastError; // ultimo errore di rete durante download/upload
        double httpLatency = -1;
        string resMethod;
        double resPing = -1, resJitter = -1, resDown = -1, resUp = -1;
        string resServer;
        DateTime resTime;

        SpeedTestForm(Icon icon, Func<string> getServer, Action<string> setServer)
        {
            this.getServer = getServer;
            this.setServer = setServer;
            Text = L.T("Test velocità - AppIP", "Speed test - AppIP");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true; // il widget sta sotto le finestre: questa deve vedersi subito
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;
            ClientSize = new Size(440, 300);
            if (icon != null) Icon = icon;

            var gray = Color.DimGray;
            var bigFont = new Font("Segoe UI Semibold", 20f);
            Label[] values = new Label[3];
            string[] caps = { L.T("Latenza", "Latency"), "Download", "Upload" };
            string[] units = { "ms", "Mbit/s", "Mbit/s" };
            for (int i = 0; i < 3; i++)
            {
                int x = 20 + i * 140;
                Controls.Add(new Label { Text = caps[i], AutoSize = true, Location = new Point(x, 16), ForeColor = gray });
                values[i] = new Label { Text = "–", AutoSize = true, Location = new Point(x - 4, 34), Font = bigFont };
                Controls.Add(values[i]);
                Controls.Add(new Label { Text = units[i], AutoSize = true, Location = new Point(x, 76), ForeColor = gray });
            }
            lblPing = values[0]; lblDown = values[1]; lblUp = values[2];
            lblJitter = new Label { Text = "", AutoSize = true, Location = new Point(20, 94), ForeColor = gray };

            bar = new ProgressBar { Location = new Point(20, 124), Size = new Size(400, 8), Maximum = 1000, Style = ProgressBarStyle.Continuous };
            lblStatus = new Label { Text = L.T("Pronto.", "Ready."), AutoSize = true, Location = new Point(20, 138) };
            lblInfo = new Label { Text = "", AutoSize = true, Location = new Point(20, 158), ForeColor = gray };

            var lblSrv = new Label { Text = L.T("Server:", "Server:"), AutoSize = true, Location = new Point(20, 194) };
            txtServer = new TextBox { Location = new Point(76, 191), Size = new Size(260, 23), Text = getServer() };
            var lnkDefault = new LinkLabel
            {
                Text = L.T("Predefinito", "Default"), AutoSize = true, Location = new Point(344, 194),
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            lnkDefault.LinkClicked += delegate { txtServer.Text = DefaultServer; };
            var tip = new ToolTip();
            tip.SetToolTip(txtServer, L.T(
                "Base di un server compatibile con speed.cloudflare.com (test completo)\noppure indirizzo di un file da scaricare (solo latenza e download).",
                "Base URL of a server compatible with speed.cloudflare.com (full test)\nor URL of a file to download (latency and download only)."));

            btnStart = new Button { Text = L.T("Avvia", "Start"), Size = new Size(100, 30), Location = new Point(20, 234) };
            btnCopy = new Button { Text = L.T("Copia risultato", "Copy result"), Size = new Size(120, 30), Location = new Point(128, 234), Enabled = false };
            var btnClose = new Button { Text = L.T("Chiudi", "Close"), Size = new Size(90, 30), Location = new Point(330, 234) };
            btnStart.Click += delegate { if (worker != null) Stop(); else Start(); };
            btnCopy.Click += delegate { try { Clipboard.SetText(ResultText()); lblStatus.Text = L.T("Risultato copiato.", "Result copied."); } catch { } };
            btnClose.Click += delegate { Close(); };
            AcceptButton = btnStart;
            CancelButton = btnClose;

            Controls.AddRange(new Control[] { lblJitter, bar, lblStatus, lblInfo, lblSrv, txtServer, lnkDefault, btnStart, btnCopy, btnClose });
            ClientSize = new Size(440, 280);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Stop();
            base.OnFormClosing(e);
        }

        // ---------- avvio / arresto ----------
        void Start()
        {
            string server = txtServer.Text.Trim();
            if (server.Length == 0) server = DefaultServer;
            if (!server.Contains("://")) server = "https://" + server;
            Uri uri;
            if (!Uri.TryCreate(server, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                lblStatus.Text = L.T("Indirizzo del server non valido.", "Invalid server address.");
                return;
            }
            txtServer.Text = server;
            setServer(server == DefaultServer ? "" : server);

            cancel = false;
            resPing = resJitter = resDown = resUp = httpLatency = -1;
            resServer = null;
            lastError = null;
            lblPing.Text = lblDown.Text = lblUp.Text = "–";
            lblJitter.Text = lblInfo.Text = "";
            bar.Value = 0;
            btnStart.Text = L.T("Interrompi", "Stop");
            btnCopy.Enabled = false;
            txtServer.ReadOnly = true;

            worker = new Thread(delegate () { Run(uri); });
            worker.IsBackground = true;
            worker.Start();
        }

        void Stop()
        {
            if (worker == null) return;
            cancel = true;
            AbortAll();
        }

        void Finished(string status)
        {
            worker = null;
            btnStart.Text = L.T("Avvia", "Start");
            txtServer.ReadOnly = false;
            lblStatus.Text = status;
            btnCopy.Enabled = resPing >= 0 || resDown >= 0;
            if (!cancel) bar.Value = bar.Maximum;
        }

        void Ui(Action a)
        {
            try { if (!IsDisposed) BeginInvoke(a); } catch { }
        }

        // ---------- test ----------
        void Run(Uri uri)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var sp = ServicePointManager.FindServicePoint(uri);
            sp.ConnectionLimit = Math.Max(sp.ConnectionLimit, 16);
            sp.Expect100Continue = false;

            // Base di un server tipo Cloudflare (nessun percorso) oppure file da scaricare
            bool api = uri.AbsolutePath == "/" && uri.Query.Length == 0;
            string baseUrl = uri.GetLeftPart(UriPartial.Authority);
            string status;
            try
            {
                Ui(delegate { lblStatus.Text = L.T("Misura della latenza...", "Measuring latency..."); });
                MeasureLatency(api ? baseUrl + "/__down?bytes=0" : uri.AbsoluteUri, api);
                if (cancel) throw new OperationCanceledException();

                Ui(delegate { lblStatus.Text = L.T("Download in corso...", "Testing download..."); });
                resDown = Transfer(false, api ? baseUrl + "/__down?bytes=" + DownChunk : uri.AbsoluteUri, DownStreams, 1, lblDown);
                if (cancel) throw new OperationCanceledException();
                if (resDown < 0) throw lastError ?? new Exception(L.T("download non riuscito", "download failed"));

                if (api)
                {
                    Ui(delegate { lblStatus.Text = L.T("Upload in corso...", "Testing upload..."); });
                    resUp = Transfer(true, baseUrl + "/__up", UpStreams, 2, lblUp);
                    if (cancel) throw new OperationCanceledException();
                }
                resTime = DateTime.Now;
                status = resUp < 0 && api
                    ? L.T("Completato (upload non riuscito).", "Completed (upload failed).")
                    : api ? L.T("Completato.", "Completed.")
                          : L.T("Completato (upload non disponibile con un file).", "Completed (no upload test with a file URL).");
            }
            catch (OperationCanceledException) { status = L.T("Interrotto.", "Stopped."); }
            catch (Exception ex)
            {
                // un'interruzione dall'utente fa fallire le richieste in corso: non è un errore
                status = cancel ? L.T("Interrotto.", "Stopped.") : L.F("Errore: {0}", "Error: {0}", ErrorText(ex));
            }
            Ui(delegate
            {
                lblUp.Text = Mbps(resUp);
                lblDown.Text = Mbps(resDown);
                Finished(status);
            });
        }

        static string ErrorText(Exception ex)
        {
            var we = ex as WebException;
            if (we == null) return ex.Message;
            if (we.Status == WebExceptionStatus.NameResolutionFailure) return L.T("server non trovato", "server not found");
            if (we.Status == WebExceptionStatus.Timeout) return L.T("il server non risponde", "no response from the server");
            var resp = we.Response as HttpWebResponse;
            if (resp != null)
            {
                // Cloudflare limita i test ripetuti dallo stesso IP pubblico (in ufficio è condiviso)
                if ((int)resp.StatusCode == 429)
                {
                    int sec;
                    if (int.TryParse(resp.Headers["Retry-After"], out sec) && sec > 0)
                        return L.F("troppi test in poco tempo da questa rete, riprova tra circa {0} minuti", "too many tests from this network, try again in about {0} minutes", Math.Max(1, (sec + 59) / 60));
                    return L.T("troppi test in poco tempo da questa rete, riprova tra qualche minuto", "too many tests from this network, try again in a few minutes");
                }
                return L.F("il server ha risposto {0}", "the server replied {0}", (int)resp.StatusCode + " " + resp.StatusDescription);
            }
            return we.Message;
        }

        // Latenza: ping ICMP verso il server (confrontabile con gli altri speed test); se l'ICMP è bloccato,
        // come spesso nelle reti aziendali, tempo di risposta HTTP meno l'elaborazione dichiarata dal server.
        // La latenza HTTP serve comunque per correggere la misura dell'upload.
        void MeasureLatency(string url, bool api)
        {
            var http = new List<double>();
            for (int i = 0; i <= PingCount && !cancel; i++)
            {
                var req = NewRequest(url);
                if (!api) req.Method = "HEAD";
                var sw = Stopwatch.StartNew();
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    using (var st = resp.GetResponseStream()) st.CopyTo(Stream.Null);
                    sw.Stop();
                    if (i == 0)
                    {
                        // la prima richiesta apre la connessione (DNS, TCP, TLS): non conta
                        string colo = resp.Headers["colo"] ?? resp.Headers["cf-meta-colo"];
                        string host = new Uri(url).Host;
                        resServer = colo != null ? host + " (" + colo + ")" : host;
                        string s = resServer;
                        Ui(delegate { lblInfo.Text = L.F("Misurato con {0}", "Measured with {0}", s); });
                        continue;
                    }
                    double ms = sw.Elapsed.TotalMilliseconds - ServerTime(resp.Headers["Server-Timing"]);
                    http.Add(Math.Max(ms, 0.1));
                }
                int n = i;
                Ui(delegate { bar.Value = n * 50 / PingCount; });
            }
            if (http.Count == 0) return;
            httpLatency = Median(http);

            var icmp = new List<double>();
            try
            {
                using (var ping = new Ping())
                {
                    string host = new Uri(url).Host;
                    for (int i = 0; i < PingCount && !cancel; i++)
                    {
                        try
                        {
                            var r = ping.Send(host, 1000);
                            if (r.Status == IPStatus.Success) icmp.Add(Math.Max(r.RoundtripTime, 0.5));
                        }
                        catch { }
                        if (i == 2 && icmp.Count == 0) break; // ICMP bloccato: inutile insistere
                        int n = i;
                        Ui(delegate { bar.Value = 50 + (n + 1) * 50 / PingCount; });
                        Thread.Sleep(100);
                    }
                }
            }
            catch { }
            var times = icmp.Count >= PingCount / 2 ? icmp : http;
            string method = times == icmp ? "ICMP" : "HTTP";
            resPing = Median(times);
            double jit = 0;
            for (int i = 1; i < times.Count; i++) jit += Math.Abs(times[i] - times[i - 1]);
            resJitter = times.Count > 1 ? jit / (times.Count - 1) : 0;
            resMethod = method;
            Ui(delegate
            {
                lblPing.Text = Ms(resPing);
                lblJitter.Text = L.F("jitter {0} ms", "jitter {0} ms", resJitter.ToString("0.0")) + " · " + method;
            });
        }

        static double Median(List<double> v)
        {
            var sorted = v.OrderBy(t => t).ToList();
            return sorted[sorted.Count / 2];
        }

        // Tempo di elaborazione dichiarato dal server (Server-Timing: ...;dur=N), da togliere alla latenza
        static double ServerTime(string header)
        {
            if (string.IsNullOrEmpty(header)) return 0;
            double max = 0;
            foreach (Match m in Regex.Matches(header, @"dur=([\d.]+)"))
            {
                double d;
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) max = Math.Max(max, d);
            }
            return max;
        }

        // Download o upload con più connessioni in parallelo per PhaseMs; restituisce Mbit/s (-1 se nulla è passato).
        // phase: 1 = download, 2 = upload (per la barra di avanzamento).
        double Transfer(bool upload, string url, int streams, int phase, Label live)
        {
            long total = 0;
            int errors = 0;
            var sw = Stopwatch.StartNew();
            var threads = new List<Thread>();
            byte[] payload = null;
            if (upload)
            {
                payload = new byte[65536];
                new Random().NextBytes(payload); // dati non comprimibili
            }
            // Upload: conta solo le richieste confermate dal server (i byte scritti finiscono prima nel
            // buffer del socket, che Windows ingrandisce durante il test e gonfierebbe il risultato).
            var upBytes = new long[streams];
            var upMs = new double[streams];
            for (int s = 0; s < streams; s++)
            {
                int idx = s;
                var t = new Thread(delegate ()
                {
                    var buf = new byte[65536];
                    long chunk = 256 * 1024; // poi adattato perché ogni invio duri ~2 s (anche su upload ADSL lenti)
                    while (!cancel && sw.ElapsedMilliseconds < PhaseMs)
                    {
                        HttpWebRequest req = null;
                        try
                        {
                            req = NewRequest(url);
                            lock (active) active.Add(req);
                            if (upload)
                            {
                                req.Method = "POST";
                                req.ContentType = "application/octet-stream";
                                req.ContentLength = chunk;
                                req.AllowWriteStreamBuffering = false;
                                double t0 = sw.Elapsed.TotalMilliseconds;
                                bool aborted = false;
                                using (var st = req.GetRequestStream())
                                {
                                    long sent = 0;
                                    while (sent < chunk)
                                    {
                                        int n = (int)Math.Min(payload.Length, chunk - sent);
                                        st.Write(payload, 0, n);
                                        sent += n;
                                        Interlocked.Add(ref total, n); // solo per la velocità mostrata durante il test
                                        if (cancel || sw.ElapsedMilliseconds >= PhaseMs) { req.Abort(); aborted = true; break; }
                                    }
                                }
                                if (!aborted)
                                {
                                    using (var resp = req.GetResponse()) { }
                                    double t1 = sw.Elapsed.TotalMilliseconds, dt = Math.Max(t1 - t0, 1);
                                    // la conferma arriva circa una latenza dopo l'ultimo byte: non è tempo di trasferimento
                                    if (httpLatency > 0) dt = Math.Max(dt - httpLatency, dt / 2);
                                    if (t1 >= WarmupMs && t1 <= PhaseMs) { upBytes[idx] += chunk; upMs[idx] += dt; }
                                    chunk = Math.Max(64 * 1024, Math.Min(UpChunk, (long)(chunk * 2000 / dt)));
                                }
                            }
                            else
                            {
                                using (var resp = req.GetResponse())
                                using (var st = resp.GetResponseStream())
                                {
                                    int n;
                                    while ((n = st.Read(buf, 0, buf.Length)) > 0)
                                    {
                                        Interlocked.Add(ref total, n);
                                        if (cancel || sw.ElapsedMilliseconds >= PhaseMs) { req.Abort(); break; }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (!cancel && sw.ElapsedMilliseconds < PhaseMs) lastError = ex;
                            // errori dovuti all'interruzione sono normali; altrimenti si riprova un po' di volte
                            if (!cancel && sw.ElapsedMilliseconds < PhaseMs && Interlocked.Increment(ref errors) > streams * 3) break;
                            if (!cancel && sw.ElapsedMilliseconds < PhaseMs) Thread.Sleep(200);
                        }
                        finally
                        {
                            if (req != null) lock (active) active.Remove(req);
                        }
                    }
                });
                t.IsBackground = true;
                t.Start();
                threads.Add(t);
            }

            // Campionamento: velocità istantanea a video, risultato sul periodo dopo il riscaldamento
            long startBytes = -1, prevBytes = 0;
            double startMs = 0, prevMs = 0, smooth = -1;
            while (threads.Any(t => t.IsAlive))
            {
                Thread.Sleep(250);
                double now = sw.Elapsed.TotalMilliseconds;
                long b = Interlocked.Read(ref total);
                if (startBytes < 0 && now >= WarmupMs) { startBytes = b; startMs = now; }
                if (now > prevMs)
                {
                    double inst = (b - prevBytes) * 8 / ((now - prevMs) * 1000);
                    smooth = smooth < 0 ? inst : smooth * 0.7 + inst * 0.3;
                    prevBytes = b; prevMs = now;
                }
                double sm = smooth;
                int prog = 100 + (phase - 1) * 450 + (int)(Math.Min(now, PhaseMs) * 450 / PhaseMs);
                Ui(delegate { live.Text = Mbps(sm); bar.Value = Math.Min(prog, bar.Maximum); });
                if (now > PhaseMs + 5000) { AbortAll(); }
            }
            double endMs = Math.Min(sw.Elapsed.TotalMilliseconds, PhaseMs);
            long endBytes = Interlocked.Read(ref total);
            if (endBytes == 0) return -1;
            if (upload)
            {
                // somma delle velocità dei singoli flussi, sugli invii completati dopo il riscaldamento
                double rate = 0;
                for (int i = 0; i < streams; i++) if (upMs[i] > 0) rate += upBytes[i] * 8 / (upMs[i] * 1000);
                if (rate > 0) return rate;
                // nessun invio completato (linea lentissima): stima dai byte scritti
            }
            if (startBytes < 0 || endMs - startMs < 1000) { startBytes = 0; startMs = 0; } // test troppo breve: tutto il periodo
            return (endBytes - startBytes) * 8 / ((endMs - startMs) * 1000);
        }

        void AbortAll()
        {
            lock (active) foreach (var r in active) try { r.Abort(); } catch { }
        }

        static HttpWebRequest NewRequest(string url)
        {
            // parametro casuale per evitare cache intermedie (proxy aziendali)
            url += (url.Contains("?") ? "&" : "?") + "r=" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "AppIP/" + AppVersion.Text;
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            req.KeepAlive = true;
            req.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            req.AutomaticDecompression = DecompressionMethods.None;
            return req;
        }

        static string Ms(double v)
        {
            return v.ToString(v < 10 ? "0.0" : "0");
        }

        static string Mbps(double v)
        {
            if (v < 0) return "–";
            return v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "0");
        }

        string ResultText()
        {
            var parts = new List<string>();
            if (resDown >= 0) parts.Add("Download " + Mbps(resDown) + " Mbit/s");
            if (resUp >= 0) parts.Add("Upload " + Mbps(resUp) + " Mbit/s");
            if (resPing >= 0) parts.Add(L.T("Latenza", "Latency") + " " + Ms(resPing) + " ms (jitter " + resJitter.ToString("0.0") + " ms, " + resMethod + ")");
            string when = (resTime == DateTime.MinValue ? DateTime.Now : resTime).ToString("g");
            return L.T("Test velocità", "Speed test") + " " + when + " - " + string.Join(" · ", parts)
                + (resServer != null ? " - " + resServer : "");
        }
    }
}
