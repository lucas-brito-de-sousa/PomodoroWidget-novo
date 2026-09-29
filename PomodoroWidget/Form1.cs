using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PomodoroWidget
{
    public partial class Form1 : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;

        private string appDataPath;
        private bool isWebBrowserReady = false;
        private NotifyIcon trayIcon;
        private bool _isExiting = false;

        // ── monitoramento de foco ocular (processo Python externo) ─────────
        private Process _gazeProcess;
        private readonly object _gazeLock = new object();
        // Ajuste estes caminhos para a sua instalação local do GazeTracking.
        // Ex: python.exe dentro do venv criado na pasta do projeto GazeTracking.
        private static readonly string GazePythonExe =
            @"C:\Users\Lucas Brito\GazeTracking\.venv\Scripts\python.exe";
        private static readonly string GazeScriptPath =
            @"C:\Users\Lucas Brito\GazeTracking\gaze_monitor_.py";
        private int _gazeSkipFrames = 1; // 1=processa todo frame (original); 3=validado nos testes (~5-6Hz)

        // Controle de reconexão automática: se o processo cair sozinho
        // (crash) enquanto ainda deveria estar rodando, tentamos reiniciar
        // com backoff, até um limite de tentativas — evita tanto a UI
        // "congelada" no último valor quanto um loop infinito de restarts
        // caso o Python esteja de fato quebrado (ex: venv corrompida).
        private bool _gazeShouldBeRunning = false;
        private int _gazeRestartAttempts = 0;
        private const int GazeMaxRestartAttempts = 4;
        private static readonly int[] GazeRestartDelaysMs = { 5000, 10000, 20000, 30000 };
        // Controla se o processo deve subir com --show-window (janela de
        // debug do vídeo). Precisa reiniciar o processo pra aplicar/remover,
        // já que é um argumento de linha de comando fixo na inicialização.
        private bool _gazeShowWindow = false;

        // Janela de debug do vídeo (WinForms + WebView2) — substitui a antiga
        // janela nativa do OpenCV. Nula quando fechada; recriada sob demanda.
        private GazeCameraForm _gazeCameraForm;

        // ── overlay de crosshair em tela cheia (todos os monitores do Windows) ──
        private CrosshairOverlayForm _crosshairOverlayForm;

        // ── overlay de BPM (processo Python externo) ─────────────────────
        private Process _bpmProcess;
        private readonly object _bpmLock = new object();
        private static readonly string BpmPythonExe =
            @"C:\BpmAnalyzer-main\venv\Scripts\python.exe";
        private static readonly string BpmScriptPath =
            @"C:\BpmAnalyzer-main\bpm_daemon.py";
        private BpmOverlayForm _bpmOverlayForm;
        private bool _bpmShouldBeRunning = false;

        // ── autenticação loopback (OAuth + PKCE) ────────────────────────────
        private TaskCompletionSource<string> _authCodeTcs;
        private string _authState;
        private string _authCodeVerifier;

        public Form1()
        {
            InitializeComponent();
            appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PomodoroWidget");
            Directory.CreateDirectory(appDataPath);
            SetupForm();
            SetupTrayIcon();
            Log("Widget iniciado — WebView2 + fetch()");
        }


        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            await SetupWebBrowser();


        }
        private void SetupForm()
        {
            this.Text = "Pomodoro Widget";
            this.Size = new Size(350, 500);
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(
                Screen.PrimaryScreen.WorkingArea.Width - this.Width - 20,
                Screen.PrimaryScreen.WorkingArea.Height - this.Height - 20);
            this.FormBorderStyle = FormBorderStyle.None;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.DoubleBuffered = true;
            SetRoundRegion(22);
            SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }

        private void SetRoundRegion(int r)
        {
            IntPtr rgn = CreateRoundRectRgn(0, 0, this.Width + 1, this.Height + 1, r, r);
            SetWindowRgn(this.Handle, rgn, true);
            DeleteObject(rgn);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); SetRoundRegion(22); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F12 && isWebBrowserReady)
            {
                webBrowser1.CoreWebView2.OpenDevToolsWindow();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
        }

        private async Task SetupWebBrowser()
        {
            string userDataFolder = Path.Combine(appDataPath, "WebView2Data");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await webBrowser1.EnsureCoreWebView2Async(env);

            var s = webBrowser1.CoreWebView2.Settings;
            s.AreDevToolsEnabled = true;
            s.IsScriptEnabled = true;
            s.AreDefaultContextMenusEnabled = true;
            s.AreDefaultScriptDialogsEnabled = true;
            s.IsStatusBarEnabled = false;
            s.AreHostObjectsAllowed = true;

            webBrowser1.CoreWebView2.AddHostObjectToScript("external", new HostObject(this));
            webBrowser1.NavigationCompleted += (o, e) => { isWebBrowserReady = true; Log("WebView2 pronto"); };

            string tempDir = Path.Combine(Path.GetTempPath(), "PomodoroWidget");
            Directory.CreateDirectory(tempDir);
            string htmlFile = Path.Combine(tempDir, "index.html");
            File.WriteAllText(htmlFile, BuildHtml(), System.Text.Encoding.UTF8);

            // Copia alert.mp3 da pasta do .exe para o tempDir sempre (garante atualização)
            string alertSrc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "alert.mp3");
            string alertDst = Path.Combine(tempDir, "alert.mp3");
            if (File.Exists(alertSrc))
                File.Copy(alertSrc, alertDst, overwrite: true);
            else
                Log("AVISO: alert.mp3 não encontrado em " + alertSrc);

            webBrowser1.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "pomodoro.local",
                tempDir,
                CoreWebView2HostResourceAccessKind.Allow);

            webBrowser1.CoreWebView2.Navigate("https://pomodoro.local/index.html");
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.AutoDual)]
        public class HostObject
        {
            private Form1 f;
            public HostObject(Form1 form) { f = form; }

            public void CloseApp() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.Close(); }
                catch { }
            }));

            public void StartDrag()
            {
                try
                {
                    if (f.IsDisposed || !f.IsHandleCreated) return;
                    f.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (!f.IsHandleCreated || f.IsDisposed) return;
                            ReleaseCapture();
                            SendMessage(f.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                        }
                        catch (Exception ex) { f.Log("StartDrag inner erro: " + ex.Message); }
                    }));
                }
                catch (Exception ex) { f.Log("StartDrag outer erro: " + ex.Message); }
            }

            public void MinimizeWidget() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.DoMinimize(); }
                catch (Exception ex) { f.Log("MinimizeWidget erro: " + ex.Message); }
            }));

            public void StartGazeMonitor() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.StartGazeMonitorProcess(); }
                catch (Exception ex) { f.Log("StartGazeMonitor erro: " + ex.Message); }
            }));

            public void StopGazeMonitor() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.StopGazeMonitorProcess(); }
                catch (Exception ex) { f.Log("StopGazeMonitor erro: " + ex.Message); }
            }));

            public void ShowGazeCameraWindow() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.ShowGazeCameraWindowInternal(); }
                catch (Exception ex) { f.Log("ShowGazeCameraWindow erro: " + ex.Message); }
            }));

            public void HideGazeCameraWindow() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.HideGazeCameraWindowInternal(); }
                catch (Exception ex) { f.Log("HideGazeCameraWindow erro: " + ex.Message); }
            }));

            public void ToggleBpmOverlay() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.ToggleBpmOverlayInternal(); }
                catch (Exception ex) { f.Log("ToggleBpmOverlay erro: " + ex.Message); }
            }));

            public void ShowFullscreenCrosshair() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.ShowFullscreenCrosshairInternal(); }
                catch (Exception ex) { f.Log("ShowFullscreenCrosshair erro: " + ex.Message); }
            }));

            public void HideFullscreenCrosshair() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.HideFullscreenCrosshairInternal(); }
                catch (Exception ex) { f.Log("HideFullscreenCrosshair erro: " + ex.Message); }
            }));

            public void OpenLink(string url) => f.Invoke(new Action(() =>
            {
                try
                {
                    if (f.IsDisposed) return;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
                }
                catch (Exception ex) { f.Log("OpenLink erro: " + ex.Message); }
            }));

            public void LogFromJS(string msg) => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.Log("[JS] " + msg); }
                catch { }
            }));

            public void ConectarSistema()
            {
                if (f.IsDisposed) return;
                _ = f.ConectarSistemaAsync();
            }

            public void SubmitManualCode(string code)
            {
                if (f.IsDisposed) return;
                f.SubmitManualCode(code);
            }
        }

        public void DoMinimize()
        {
            if (this.Height > 100)
            {
                this.Height = 57;
                _ = webBrowser1.ExecuteScriptAsync(
                    "var c=document.getElementById('pomodoroContent');if(c)c.style.display='none';" +
                    "var b=document.getElementById('pomodoroMinimize');if(b)b.textContent='▶';");
            }
            else
            {
                this.Height = 430;
                _ = webBrowser1.ExecuteScriptAsync(
                    "var c=document.getElementById('pomodoroContent');if(c)c.style.display='flex';" +
                    "var b=document.getElementById('pomodoroMinimize');if(b)b.textContent='▼';");
            }
            SetRoundRegion(22);
        }

        // ══════════════════════════════════════════════════════════════════
        //  👁 MONITORAMENTO DE FOCO OCULAR — processo Python (GazeTracking)
        //  Roda o script como processo filho, lê o stdout linha a linha
        //  (JSON) e repassa o status pra UI via ExecuteScriptAsync.
        // ══════════════════════════════════════════════════════════════════

        public void StartGazeMonitorProcess(bool isRestart = false)
        {
            lock (_gazeLock)
            {
                if (_gazeProcess != null && !_gazeProcess.HasExited) return; // já rodando

                if (!File.Exists(GazePythonExe) || !File.Exists(GazeScriptPath))
                {
                    string msg = "python.exe ou gaze_monitor.py não encontrados. Verifique GazePythonExe/GazeScriptPath em Form1.cs.";
                    Log("GazeMonitor: " + msg);
                    NotifyGazeError(msg);
                    _gazeShouldBeRunning = false;
                    return;
                }

                // Um start "manual" (chamado pelo JS via StartGazeMonitor) sempre
                // reseta o contador de tentativas — só o auto-restart por crash
                // incrementa esse contador.
                if (!isRestart) _gazeRestartAttempts = 0;
                _gazeShouldBeRunning = true;

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = GazePythonExe,
                        // -u força stdout sem buffer adicional do interpretador (redundante
                        // com flush=True no script, mas não custa garantir).
                        Arguments = "-u \"" + GazeScriptPath + "\" --window 60 --interval 2 --skip-frames " + _gazeSkipFrames +
                             (_gazeShowWindow ? " --show-window --frame-quality 80 --frame-max-width 720" : ""),
                        WorkingDirectory = Path.GetDirectoryName(GazeScriptPath),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                    };

                    _gazeProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    _gazeProcess.OutputDataReceived += GazeProcess_OutputDataReceived;
                    _gazeProcess.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data)) Log("GazeMonitor stderr: " + e.Data);
                    };
                    _gazeProcess.Exited += GazeProcess_Exited;

                    _gazeProcess.Start();
                    _gazeProcess.BeginOutputReadLine();
                    _gazeProcess.BeginErrorReadLine();
                    Log("GazeMonitor: processo iniciado (PID " + _gazeProcess.Id + ")" +
                        (isRestart ? " [restart automático, tentativa " + _gazeRestartAttempts + "]" : ""));
                }
                catch (Exception ex)
                {
                    Log("GazeMonitor: erro ao iniciar processo — " + ex.Message);
                    NotifyGazeError("Falha ao iniciar: " + ex.Message);
                    _gazeProcess = null;
                }
            }
        }

        public void StopGazeMonitorProcess()
        {
            lock (_gazeLock)
            {
                // Marca como "parada intencional" ANTES de matar o processo,
                // pra o handler Exited não confundir isso com um crash e
                // tentar reconectar sozinho.
                _gazeShouldBeRunning = false;

                if (_gazeProcess == null) return;
                try
                {
                    if (!_gazeProcess.HasExited) _gazeProcess.Kill();
                }
                catch (Exception ex) { Log("GazeMonitor: erro ao encerrar processo — " + ex.Message); }
                finally
                {
                    _gazeProcess.Dispose();
                    _gazeProcess = null;
                }
            }
        }

        // Abre a janela de debug do vídeo (--show-window). Se o processo já
        // estiver rodando sem a janela, reinicia com a flag; se não estiver
        // rodando, sobe do zero já com ela. A partir de agora a exibição é
        // uma Form WinForms com WebView2 (não mais uma janela OpenCV) — o
        // Python só manda os frames (JPEG/base64) e o status via stdout.
        public void ShowGazeCameraWindowInternal()
        {
            _gazeShowWindow = true;
            bool wasRunning = _gazeProcess != null && !_gazeProcess.HasExited;
            if (wasRunning) StopGazeMonitorProcess();
            StartGazeMonitorProcess();

            if (_gazeCameraForm == null || _gazeCameraForm.IsDisposed)
            {
                _gazeCameraForm = new GazeCameraForm(SendGazeCommand);
                _gazeCameraForm.FormClosed += (s, e) => { _gazeCameraForm = null; HideGazeCameraWindowInternal(); };
            }
            _gazeCameraForm.Show();
            _gazeCameraForm.Activate();
        }

        // Fecha a janela de debug — reinicia o processo sem --show-window,
        // mantendo o monitoramento (headless) rodando normalmente.
        public void HideGazeCameraWindowInternal()
        {
            _gazeShowWindow = false;
            bool wasRunning = _gazeProcess != null && !_gazeProcess.HasExited;
            if (wasRunning)
            {
                StopGazeMonitorProcess();
                StartGazeMonitorProcess();
            }

            if (_gazeCameraForm != null && !_gazeCameraForm.IsDisposed)
            {
                // FormClosed já chamaria HideGazeCameraWindowInternal de novo, então
                // fecha via Hide (não Close) quando chamado por aqui, evitando reentrância.
                _gazeCameraForm.Hide();
            }
        }

        // Manda 1 linha JSON pro stdin do processo Python — é assim que o C#
        // avisa mudanças de slider/checkbox (equalizador, calibração manual,
        // limiar de piscada) pro lado Python em tempo real. Formato esperado
        // pelo gaze_monitor.py: {"cmd":"update_config", <campos>}.
        public void SendGazeCommand(string json)
        {
            try
            {
                lock (_gazeLock)
                {
                    if (_gazeProcess == null || _gazeProcess.HasExited) return;
                    _gazeProcess.StandardInput.WriteLine(json);
                    _gazeProcess.StandardInput.Flush();
                }
            }
            catch (Exception ex)
            {
                Log("SendGazeCommand erro: " + ex.Message);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  ➕ CROSSHAIR EM TELA CHEIA (overlay nativo cobrindo o Windows)
        //  Janela transparente, sem borda, topmost e "click-through" (não
        //  intercepta cliques/mouse) que desenha duas linhas cruzando o
        //  centro do monitor principal. Criada sob demanda e reaproveitada
        //  entre exibições — só é destruída quando o app fecha.
        // ══════════════════════════════════════════════════════════════════
        public void ShowFullscreenCrosshairInternal()
        {
            if (_crosshairOverlayForm == null || _crosshairOverlayForm.IsDisposed)
            {
                _crosshairOverlayForm = new CrosshairOverlayForm();
            }
            _crosshairOverlayForm.Show();
        }

        public void HideFullscreenCrosshairInternal()
        {
            if (_crosshairOverlayForm != null && !_crosshairOverlayForm.IsDisposed)
            {
                _crosshairOverlayForm.Hide();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  🎵 OVERLAY DE BPM — processo Python (bpm_daemon.py)
        //  Mesmo padrão do gaze: processo filho, stdout linha a linha (JSON),
        //  repassado pro BpmOverlayForm via ExecuteScriptAsync.
        // ══════════════════════════════════════════════════════════════════

        public void ToggleBpmOverlayInternal()
        {
            if (_bpmOverlayForm != null && !_bpmOverlayForm.IsDisposed)
            {
                _bpmOverlayForm.Close();
                return;
            }

            if (!File.Exists(BpmPythonExe) || !File.Exists(BpmScriptPath))
            {
                Log("BpmOverlay: python.exe ou bpm_daemon.py não encontrados. Verifique BpmPythonExe/BpmScriptPath em Form1.cs.");
                return;
            }

            StartBpmProcess();

            _bpmOverlayForm = new BpmOverlayForm();
            _bpmOverlayForm.FormClosed += (s, e) =>
            {
                _bpmOverlayForm = null;
                StopBpmProcess();
            };
            _bpmOverlayForm.Show();
            _bpmOverlayForm.Activate();
        }

        private void StartBpmProcess()
        {
            lock (_bpmLock)
            {
                if (_bpmProcess != null && !_bpmProcess.HasExited) return;

                _bpmShouldBeRunning = true;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = BpmPythonExe,
                        Arguments = "-u \"" + BpmScriptPath + "\"",
                        WorkingDirectory = Path.GetDirectoryName(BpmScriptPath),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                    };

                    _bpmProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    _bpmProcess.OutputDataReceived += BpmProcess_OutputDataReceived;
                    _bpmProcess.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data)) Log("BpmDaemon stderr: " + e.Data);
                    };
                    _bpmProcess.Exited += (s, e) =>
                    {
                        Log("BpmDaemon: processo encerrado (código " + SafeExitCode(s as Process) + ")");
                    };

                    _bpmProcess.Start();
                    _bpmProcess.BeginOutputReadLine();
                    _bpmProcess.BeginErrorReadLine();
                    Log("BpmDaemon: processo iniciado (PID " + _bpmProcess.Id + ")");
                }
                catch (Exception ex)
                {
                    Log("BpmDaemon: erro ao iniciar processo — " + ex.Message);
                    _bpmProcess = null;
                }
            }
        }

        private void StopBpmProcess()
        {
            lock (_bpmLock)
            {
                _bpmShouldBeRunning = false;
                if (_bpmProcess == null) return;
                try
                {
                    if (!_bpmProcess.HasExited) _bpmProcess.Kill();
                }
                catch (Exception ex) { Log("BpmDaemon: erro ao encerrar processo — " + ex.Message); }
                finally
                {
                    _bpmProcess.Dispose();
                    _bpmProcess = null;
                }
            }
        }

        private void BpmProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            string line = e.Data.Trim();
            string type = ExtractJsonStringValue(line, "type");

            if (type == "bpm")
            {
                double? value = ExtractJsonNumberValue(line, "value");
                bool stable = ExtractJsonBoolValue(line, "stable");
                RunOnUi(() =>
                {
                    if (_bpmOverlayForm != null && !_bpmOverlayForm.IsDisposed)
                        _bpmOverlayForm.UpdateBpm(value, stable);
                });
            }
            else if (type == "error")
            {
                string message = ExtractJsonStringValue(line, "message") ?? "erro desconhecido";
                Log("BpmDaemon: " + message);
                RunOnUi(() =>
                {
                    if (_bpmOverlayForm != null && !_bpmOverlayForm.IsDisposed)
                        _bpmOverlayForm.ShowError(message);
                });
            }
            else if (type == "ready")
            {
                Log("BpmDaemon: stream de áudio iniciado");
            }
        }

        private void GazeProcess_Exited(object sender, EventArgs e)
        {
            // Ignora eventos "Exited" de processos antigos/obsoletos. Isso
            // acontece quando Stop()+Start() rodam em sequência rápida (ex:
            // abrir/fechar a janela da câmera): o processo antigo é morto,
            // mas o evento Exited dele só chega DEPOIS que o processo NOVO
            // já está rodando e atribuído a _gazeProcess. Sem essa checagem,
            // o evento do processo antigo derrubava e reconectava o
            // processo NOVO (saudável), mesmo sem nada de errado — daí a
            // reconexão "do nada" mesmo com a câmera funcionando.
            lock (_gazeLock)
            {
                if (!ReferenceEquals(sender, _gazeProcess))
                {
                    Log("GazeMonitor: evento Exited de um processo antigo/obsoleto ignorado.");
                    return;
                }
            }

            int exitCode = SafeExitCode(sender as Process);
            Log("GazeMonitor: processo encerrado (código " + exitCode + ")");

            // Se a parada foi intencional (usuário desligou o monitoramento
            // ou pausou o pomodoro), não faz nada — comportamento esperado.
            if (!_gazeShouldBeRunning) return;

            lock (_gazeLock)
            {
                // Confere de novo dentro do lock: pode ter mudado entre o
                // check acima e agora (ex: um Start concorrente já assumiu
                // _gazeProcess enquanto esperávamos o lock).
                if (!ReferenceEquals(sender, _gazeProcess)) return;
                _gazeProcess?.Dispose();
                _gazeProcess = null;
            }

            _gazeRestartAttempts++;
            if (_gazeRestartAttempts > GazeMaxRestartAttempts)
            {
                Log("GazeMonitor: excedeu " + GazeMaxRestartAttempts + " tentativas de reconexão — desistindo.");
                NotifyGazeError("O monitoramento parou de responder e não foi possível reconectar automaticamente (" +
                    GazeMaxRestartAttempts + " tentativas). Desative e reative o monitoramento pra tentar de novo.");
                _gazeShouldBeRunning = false;
                return;
            }

            int delayIndex = Math.Min(_gazeRestartAttempts - 1, GazeRestartDelaysMs.Length - 1);
            int delayMs = GazeRestartDelaysMs[delayIndex];
            Log("GazeMonitor: caiu inesperadamente — tentando reconectar em " + delayMs + "ms " +
                "(tentativa " + _gazeRestartAttempts + "/" + GazeMaxRestartAttempts + ")");
            NotifyGazeReconnecting(_gazeRestartAttempts, GazeMaxRestartAttempts);

            _ = Task.Delay(delayMs).ContinueWith(_ =>
            {
                RunOnUi(() =>
                {
                    if (_isExiting || IsDisposed) return;
                    if (!_gazeShouldBeRunning) return; // usuário desligou enquanto esperava
                    // Verificação final antes de reconectar de fato: se
                    // outro processo já está rodando (ex: o usuário reabriu
                    // manualmente enquanto esperava o backoff), não duplica.
                    if (_gazeProcess != null && !_gazeProcess.HasExited) return;
                    StartGazeMonitorProcess(isRestart: true);
                });
            });
        }

        private static int SafeExitCode(Process p)
        {
            try { return p != null ? p.ExitCode : -1; } catch { return -1; }
        }

        private void GazeProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            string line = e.Data.Trim();

            // Roteamento simples por "type" sem dependência de biblioteca JSON externa
            // (mesmo padrão de ExtractJsonStringValue/Bool já usado no projeto).
            string type = ExtractJsonStringValue(line, "type");

            if (type == "gaze_status")
            {
                double pct = ExtractJsonNumberValue(line, "focused_pct") ?? 0;
                double windowSec = ExtractJsonNumberValue(line, "window_sec") ?? 60;
                bool focused = ExtractJsonBoolValue(line, "current_focused");
                bool faceDetected = ExtractJsonBoolValue(line, "face_detected");
                string direction = ExtractJsonStringValue(line, "direction") ?? "unknown";

                // interval_frame_count: quantos frames entraram na decisão de
                // current_focused deste intervalo (esperado ~10 com os
                // defaults --interval 2 * --sample-hz 5). Só loga quando vem
                // bem abaixo do esperado — sinal de que está rodando mais
                // devagar que 5Hz (ex: máquina sob carga).
                double frameCount = ExtractJsonNumberValue(line, "interval_frame_count") ?? -1;
                if (frameCount >= 0 && frameCount < 7)
                {
                    double focusedCount = ExtractJsonNumberValue(line, "interval_focused_count") ?? -1;
                    Log("GazeMonitor: aviso — só " + (int)frameCount + " frames no intervalo de 2s (esperado ~10, " +
                        (int)focusedCount + " com rosto detectado); pode estar rodando mais devagar que 5Hz.");
                }

                double intervalRatio = ExtractJsonNumberValue(line, "interval_focused_ratio") ?? -1;
                int intervalPct = intervalRatio >= 0 ? (int)Math.Round(intervalRatio * 100) : -1;

                if (isWebBrowserReady)
                {
                    string js = "updateGazeStatus(" + (int)pct + "," + (int)windowSec + "," +
                        (focused ? "true" : "false") + "," + (faceDetected ? "true" : "false") + "," + intervalPct + "," +
                        JsString(direction) + ");";
                    RunOnUi(() => { _ = webBrowser1.ExecuteScriptAsync(js); });
                }

                RunOnUi(() =>
                {
                    if (_gazeCameraForm != null && !_gazeCameraForm.IsDisposed)
                        _gazeCameraForm.UpdateStatus(direction, focused, faceDetected);
                });
            }
            else if (type == "gaze_frame")
            {
                string data = ExtractJsonStringValue(line, "data");
                if (data == null) return;
                RunOnUi(() =>
                {
                    if (_gazeCameraForm != null && !_gazeCameraForm.IsDisposed)
                        _gazeCameraForm.UpdateFrame(data);
                });
            }
            else if (type == "gaze_config")
            {
                // Manda a linha JSON inteira pra Form de câmera — ela já é um
                // objeto JSON válido (mesmos campos do gaze_equalizer_state.json
                // do lado Python), então dá pra passar direto pro JS sem
                // reconstruir campo por campo aqui.
                RunOnUi(() =>
                {
                    if (_gazeCameraForm != null && !_gazeCameraForm.IsDisposed)
                        _gazeCameraForm.InitConfig(line);
                });
            }


            else if (type == "gaze_fps_debug")
            {
                double fpsCaptura = ExtractJsonNumberValue(line, "fps_captura") ?? 0;
                double fpsProc = ExtractJsonNumberValue(line, "fps_processamento") ?? 0;
                double? dlibMs = ExtractJsonNumberValue(line, "ultimo_dlib_ms");
                double? pctRosto = ExtractJsonNumberValue(line, "pct_rosto_detectado");

                string dlibTxt = dlibMs.HasValue ? dlibMs.Value.ToString("0") + "ms" : "--";
                string rostoTxt = pctRosto.HasValue ? pctRosto.Value.ToString("0") + "%" : "--";

                Log("FPS captura: " + fpsCaptura.ToString("0").PadLeft(2) +
                    "  |  FPS processamento (dlib): " + fpsProc.ToString("0").PadLeft(2) +
                    "  |  último gaze.refresh(): " + dlibTxt +
                    "  |  rosto detectado: " + rostoTxt);
            }


            else if (type == "gaze_error")
            {
                string message = ExtractJsonStringValue(line, "message") ?? "erro desconhecido";
                Log("GazeMonitor: " + message);
                NotifyGazeError(message);
            }
        }

        private void NotifyGazeError(string message)
        {
            if (!isWebBrowserReady) return;
            string js = "onGazeMonitorError(" + JsString(message) + ");";
            RunOnUi(() => { _ = webBrowser1.ExecuteScriptAsync(js); });
        }

        private void NotifyGazeReconnecting(int attempt, int maxAttempts)
        {
            if (!isWebBrowserReady) return;
            string js = "onGazeReconnecting(" + attempt + "," + maxAttempts + ");";
            RunOnUi(() => { _ = webBrowser1.ExecuteScriptAsync(js); });
        }

        private void SetupTrayIcon()
        {
            Icon appIcon;
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                appIcon = File.Exists(iconPath)
                    ? new Icon(iconPath)
                    : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                appIcon = SystemIcons.Application;
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add("Abrir Pomodoro Widget", null, (s, e) => ShowWidget());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Fechar", null, (s, e) =>
            {
                _isExiting = true;
                trayIcon.Visible = false;
                Application.Exit();
            });

            trayIcon = new NotifyIcon
            {
                Icon = appIcon,
                Text = "Pomodoro Widget",
                Visible = true,
                ContextMenuStrip = menu
            };
            trayIcon.DoubleClick += (s, e) => ShowWidget();
        }

        private void ShowWidget()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            this.Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_isExiting)
            {
                // Fechar pela UI do widget só esconde — o app continua rodando na bandeja
                e.Cancel = true;
                this.Hide();
                return;
            }

            if (isWebBrowserReady)
                _ = webBrowser1.ExecuteScriptAsync("onAppClosing()");
            StopGazeMonitorProcess();
            StopBpmProcess();
            if (_crosshairOverlayForm != null && !_crosshairOverlayForm.IsDisposed)
                _crosshairOverlayForm.Close();
            Log("Widget fechado");
            base.OnFormClosing(e);
        }

        public void Log(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(appDataPath, "pomodoro.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + msg + Environment.NewLine);
                Debug.WriteLine(msg);
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════
        //  🔗 CONEXÃO AUTOMÁTICA — OAuth Authorization Code + PKCE (loopback)
        //  Mesmo padrão usado pelo VSCode/Claude Code para autenticar via
        //  navegador externo: abre o browser, escuta um callback local em
        //  127.0.0.1, e também aceita colar o código manualmente como fallback.
        // ══════════════════════════════════════════════════════════════════

        private void RunOnUi(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }

        // Escapa uma string C# para uso seguro como literal de string em JS/JSON,
        // sem depender de System.Text.Json (não disponível no .NET Framework < 5).
        // internal (não private) pra GazeCameraForm também poder reutilizar.
        internal static string JsString(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string ExtractJsonStringValue(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static bool ExtractJsonBoolValue(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
            return m.Success && m.Groups[1].Value == "true";
        }

        private static double? ExtractJsonNumberValue(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
            if (!m.Success) return null;
            return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private void NotificarUi(string mensagem, bool erro = false)
        {
            if (!isWebBrowserReady) return;
            string color = erro ? "#fc8181" : "#a5d6a7";
            string js = "var el=document.getElementById('spAutoConnectFeedback'); if(el){ el.textContent="
                + JsString(mensagem) + "; el.style.color='" + color + "'; }";
            RunOnUi(() => { _ = webBrowser1.ExecuteScriptAsync(js); });
        }

        private static int GetFreeTcpPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static string Base64UrlEncode(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Chamado pelo HostObject quando o usuário cola o código manualmente
        // (fallback caso o redirecionamento automático não chegue no app).
        public void SubmitManualCode(string code)
        {
            if (_authCodeTcs != null && !_authCodeTcs.Task.IsCompleted && !string.IsNullOrWhiteSpace(code))
                _authCodeTcs.TrySetResult(code.Trim());
        }

        public async Task ConectarSistemaAsync()
        {
            HttpListener listener = null;
            try
            {
                _authState = Guid.NewGuid().ToString("N");

                byte[] verifierBytes = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(verifierBytes); }
                _authCodeVerifier = Base64UrlEncode(verifierBytes);

                byte[] challengeBytes;
                using (var sha = SHA256.Create())
                {
                    challengeBytes = sha.ComputeHash(Encoding.ASCII.GetBytes(_authCodeVerifier));
                }
                string codeChallenge = Base64UrlEncode(challengeBytes);

                int port = GetFreeTcpPort();
                string redirectUri = $"http://127.0.0.1:{port}/callback/";

                listener = new HttpListener();
                listener.Prefixes.Add(redirectUri);
                listener.Start();

                _authCodeTcs = new TaskCompletionSource<string>();

                string authUrl =
                    $"https://sistemadetarefas.online/desktop-auth/connect.php?redirect_uri={Uri.EscapeDataString(redirectUri)}&state={_authState}&code_challenge={codeChallenge}";

                NotificarUi("Abrindo navegador para login…");

                try { Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    Log("Falha ao abrir navegador: " + ex.Message);
                    NotificarUi("Não abriu o navegador automaticamente. Copie: " + authUrl, erro: true);
                }

                // Caminho automático — escuta o redirecionamento de loopback.
                var currentListener = listener;
                var currentTcs = _authCodeTcs;
                var expectedState = _authState;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ctx = await currentListener.GetContextAsync();
                        var q = ctx.Request.QueryString;

                        // Em vez de mostrar a página de sucesso aqui no 127.0.0.1, redireciona
                        // de volta pro próprio domínio — o usuário só vê o localhost por uma
                        // fração de segundo (igual ao fluxo do Claude Code).
                        ctx.Response.Redirect("https://sistemadetarefas.online/desktop-auth/sucesso.php?app=pomodoro-widget");
                        ctx.Response.OutputStream.Close();

                        if (q["state"] == expectedState && !string.IsNullOrEmpty(q["code"]))
                            currentTcs.TrySetResult(q["code"]);
                    }
                    catch (Exception ex) { Log("Erro no listener de callback: " + ex.Message); }
                });

                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(3));
                var completed = await Task.WhenAny(_authCodeTcs.Task, timeoutTask);

                try { listener.Stop(); } catch { }

                if (completed == timeoutTask)
                {
                    NotificarUi("Tempo esgotado. Tente novamente.", erro: true);
                    return;
                }

                string authCode = await _authCodeTcs.Task;
                NotificarUi("Trocando código pelo token…");

                string token = await TrocarCodigoPorTokenAsync(authCode, _authCodeVerifier, redirectUri);
                if (string.IsNullOrEmpty(token))
                {
                    NotificarUi("Falha ao obter o token. Tente novamente.", erro: true);
                    return;
                }

                RunOnUi(() =>
                {
                    _ = webBrowser1.ExecuteScriptAsync("onAutoTokenReceived(" + JsString(token) + ");");
                });
                Log("Conexão automática concluída com sucesso.");
            }
            catch (Exception ex)
            {
                Log("Erro em ConectarSistemaAsync: " + ex.Message);
                NotificarUi("Erro: " + ex.Message, erro: true);
            }
            finally
            {
                try { listener?.Stop(); } catch { }
            }
        }

        private async Task<string> TrocarCodigoPorTokenAsync(string code, string verifier, string redirectUri)
        {
            try
            {
                using (var http = new HttpClient())
                {
                    string body = "{\"code\":" + JsString(code)
                        + ",\"code_verifier\":" + JsString(verifier)
                        + ",\"redirect_uri\":" + JsString(redirectUri) + "}";

                    HttpResponseMessage resp = await http.PostAsync(
                        "https://sistemadetarefas.online/desktop-auth/exchange.php",
                        new StringContent(body, Encoding.UTF8, "application/json"));

                    string json = await resp.Content.ReadAsStringAsync();

                    if (ExtractJsonBoolValue(json, "success"))
                        return ExtractJsonStringValue(json, "token");

                    Log("exchange_code falhou: " + json);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log("Erro ao trocar código por token: " + ex.Message);
                return null;
            }
        }

        private string BuildHtml() =>
$@"<!DOCTYPE html>
<html>
<head>
<meta charset='UTF-8'>
<style>{BuildCss()}</style>
</head>
<body>
<div class='widget' id='pomodoroWidget'>
  <div class='header' id='pomodoroHeader'>
    <div class='title'>
      <span class='task-title' id='pomodoroTaskTitle'>Pomodoro Widget</span>
      <span class='time-mini' id='pomodoroTimeMini'>60:00</span>
    </div>
    <div class='controls'>
      <button class='btn-burger' id='btnBurger' onclick='togglePanel()' data-tip='Configurações'>&#9776;</button>
      <div class='btn-min-wrap' id='btnMinWrap'>
        <button class='btn-min'   id='pomodoroMinimize' onclick='minimizeButtonClick(event)'>▼</button>
        <span class='dev-badge' id='devDoubleBadge' style='display:none;' data-tip='Contagem pela metade ativa'>½</span>
        <div class='dev-min-menu' id='devMinMenu' style='display:none;'>
          <button onclick='devMinMenuMinimize(event)'>▼ Minimizar</button>
          <button onclick='devMinMenuToggleDouble(event)' id='devMinMenuDoubleBtn'>⧉ Contar pela metade</button>
          <button onclick='devMinMenuOpenAdminPause(event)' id='devMinMenuAdminPauseBtn'>Pausa administrativa</button>
          <button onclick='toggleBpmOverlay(event)' id='bpmToggleBtn'>🎵 BPM</button>
        </div>
      </div>
      <button class='btn-start' id='pomodoroToggle'   onclick='toggleTimer()'>Iniciar</button>
      <button class='btn-close' id='pomodoroClose'    onclick='hostCall(""CloseApp"")'>✕</button>
    </div>
  </div>
  <div class='content' id='pomodoroContent'>
    <div class='timer-row'>
      <span class='coffee-emoji' id='coffeeEmoji' onclick='coffeeConfirm()' data-tip='Hora do café! Clique para confirmar' style='display:none'>☕</span>
      <div class='timer-wrap'>
        <div class='timer' id='pomodoroTimer'>60:00</div>
        <span class='dev-half-badge' id='devHalfBadge'>+0,5s</span>
      </div>
      <div class='hg-wrap' id='hourglassContainer' onclick='onHourglassClick()' data-tip='Clique para sincronizar agora'>
        <svg viewBox='0 0 28 44' width='28' height='44'>
          <line x1='5' y1='2' x2='23' y2='2' stroke='#555' stroke-width='1.5' stroke-linecap='round'/>
          <line x1='5' y1='42' x2='23' y2='42' stroke='#555' stroke-width='1.5' stroke-linecap='round'/>
          <path d='M 7,4 L 7,17 Q 7,22 14,24 Q 21,22 21,17 L 21,4' fill='none' stroke='#3a3a3a' stroke-width='1'/>
          <path d='M 7,40 L 7,27 Q 7,22 14,20 Q 21,22 21,27 L 21,40' fill='none' stroke='#3a3a3a' stroke-width='1'/>
          <clipPath id='hgTopClip'><path d='M 7,4 L 7,17 Q 7,22 14,24 Q 21,22 21,17 L 21,4 Z'/></clipPath>
          <clipPath id='hgBotClip'><path d='M 7,40 L 7,27 Q 7,22 14,20 Q 21,22 21,27 L 21,40 Z'/></clipPath>
          <rect id='hgTopSand' x='7' y='4'   width='14' height='20' clip-path='url(#hgTopClip)' fill='#7b1fa2' opacity='0.7'/>
          <rect id='hgStream'  x='13.4' y='21' width='1.2' height='5' rx='0.6' fill='#7b1fa2' opacity='0'/>
          <rect id='hgBotSand' x='7' y='40'  width='14' height='0'  clip-path='url(#hgBotClip)' fill='#7b1fa2' opacity='0.7'/>
        </svg>
        <div class='sync-pulse' id='syncPulse'></div>
      </div>
      <div id='sessionBadgeWrap' style='display:inline-flex;align-items:center;gap:0;position:relative;'>
        <span class='session-badge' id='pomodoroSessionType' style='display:none;border-radius:20px 0 0 20px;padding:4px 6px 4px 12px;'>Descanso</span>
        <span id='badgeDropdownArrow' role='button' tabindex='0' onclick='toggleBadgeDropdown()' style='display:none;align-items:center;justify-content:center;background:#4caf50;border-radius:0 20px 20px 0;padding:4px 8px 4px 4px;cursor:pointer;line-height:0;'>
          <svg id='badgeArrowSvg' width='10' height='16' viewBox='0 0 12 12' fill='none' xmlns='http://www.w3.org/2000/svg'>
            <polyline points='2,4 6,8 10,4' stroke='#fff' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'/>
          </svg>
        </span>
        <div id='badgeDropdownMenu' style='display:none;position:absolute;top:calc(100% + 6px);left:0;min-width:220px;background:#1e1e2e;border:1px solid rgba(255,255,255,.12);border-radius:8px;box-shadow:0 4px 16px rgba(0,0,0,.5);z-index:9000;overflow:hidden;'>
          <div id='bdMenuMerito' style='display:none;padding:10px 12px;cursor:pointer;font-size:11px;font-weight:700;color:#FFD700;border-bottom:1px solid rgba(255,255,255,.06);' onmouseenter='this.style.background=""rgba(255,215,0,.08)""' onmouseleave='this.style.background=""""' onclick='bdMenuContinuarParaMerito()'>
            ⭐ Estudar mais <span id='bdMeritoMin'></span> min → Mérito
          </div>
          <div style='padding:10px 12px;cursor:pointer;font-size:11px;font-weight:700;color:#a0aec0;' onmouseenter='this.style.background=""rgba(255,255,255,.05)""' onmouseleave='this.style.background=""""' onclick='bdMenuPularDescanso()'>
            ⏭ Pular descanso
          </div>
        </div>
      </div>
      <span class='session-badge admin-pause-badge' id='adminPauseBadge' style='display:none'>Pausa administrativa</span>
    </div>
    <div id='gradeMount'></div>
    <div class='progress'><div class='progress-bar' id='pomodoroProgressBar' style='width:0%'></div></div>
    <div class='stats'>
      <div class='stat'><div class='lbl'>Sessão Atual</div><div class='val' id='currentSession'>00:00:00</div></div>
      <div class='stat' id='pauseStatBlock' style='display:none;position:relative;padding-right:6px;'><div class='lbl' id='pauseStatLabel'>Desvio</div><div class='val' id='pauseElapsed' style='color:#7b1fa2'>00:00</div><div id='desvioBar' style='display:none;position:absolute;right:0;top:3px;bottom:3px;width:3px;background:rgba(255,255,255,0.08);border-radius:2px;overflow:hidden;'><div id='desvioBarFill' style='position:absolute;bottom:0;left:0;width:100%;height:0%;background:#4caf50;border-radius:2px;transition:height .5s,background .5s;'></div></div></div>
      <div class='stat'><div class='lbl'>Total Hoje</div><div class='val' id='totalToday'>00:00:00</div></div>
    </div>
    <div class='task-info' id='taskInfo'>Tarefa atual: Nenhuma</div>
    <div class='gaze-block' id='gazeBlock' style='display:none'>
      <div class='gaze-row'>
        <span class='gaze-label'>👁 Foco ocular</span>
        <span class='gaze-pct' id='gazePct'>--%</span>
      </div>
      <div class='gaze-bar-bg'><div class='gaze-bar-fill' id='gazeBarFill' style='width:0%'></div></div>
      <div class='gaze-sub-row'>
        <div class='gaze-sub' id='gazeSub'>aguardando monitoramento…</div>
        <button id='gazeCameraBtn' class='gaze-camera-btn' onclick='toggleGazeCameraWindow()' title='Abrir janela da câmera (debug)'>
          <svg xmlns='http://www.w3.org/2000/svg' width='15' height='15' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>
            <path d='M23 7l-7 5 7 5V7z'></path>
            <rect x='1' y='5' width='15' height='14' rx='2' ry='2'></rect>
          </svg>
        </button>
      </div>
    </div>
  </div>
</div>

<!-- Painel lateral de configurações -->
<div id='settingsPanel' class='settings-panel'>
  <div class='sp-header'>
    <span>⚙ Configurações</span>
    <button class='sp-close' onclick='togglePanel()'>✕</button>
  </div>
  <div class='sp-body'>
    <div id='updateIntervalsSection'>
      <div class='sp-section'>Intervalos de atualização</div>
      <div class='sp-row'>
        <label class='sp-label' for='cfgSync'>Sync principal <span class='sp-unit'>(seg)</span></label>
        <input class='sp-input' id='cfgSync' type='number' min='5' max='300' step='5' value='15'/>
      </div>
      <div class='sp-row'>
        <label class='sp-label' for='cfgSave'>Salvar estudo <span class='sp-unit'>(seg)</span></label>
        <input class='sp-input' id='cfgSave' type='number' min='10' max='600' step='10' value='30'/>
      </div>
      <div class='sp-row'>
        <label class='sp-label' for='cfgMinMin'>Mínimo p/ salvar <span class='sp-unit'>(min)</span></label>
        <input class='sp-input' id='cfgMinMin' type='number' min='0.1' max='5' step='0.1' value='0.5'/>
      </div>
    </div>
    <div class='sp-section' style='margin-top:14px;'>Temporizador</div>
    <div class='sp-row'>
      <label class='sp-label' for='cfgFoco'>Foco <span class='sp-unit'>(min)</span></label>
      <input class='sp-input' id='cfgFoco' type='number' min='1' max='120' step='1' value='60'/>
    </div>
    <div class='sp-row'>
      <label class='sp-label' for='cfgBreak'>Descanso <span class='sp-unit'>(min)</span></label>
      <input class='sp-input' id='cfgBreak' type='number' min='1' max='60' step='1' value='15'/>
    </div>
    <div class='sp-row'>
      <label class='sp-label' for='cfgDesvio'>Alerta de desvio <span class='sp-unit'>(min)</span></label>
      <input class='sp-input' id='cfgDesvio' type='number' min='1' max='60' step='1' value='10'/>
    </div>
    <button class='sp-save' onclick='saveSettings()'>Salvar</button>
    <div class='sp-feedback' id='spFeedback'></div>
    <div class='sp-section' style='margin-top:14px;'>Sessão</div>
    <button class='sp-reset' onclick='confirmarReset()'>↺ Zerar sessão local</button>
    <div class='sp-feedback' id='spFeedbackReset'></div>
    <div class='sp-section sp-section-flex' style='margin-top:14px;'>
      <span>🔗 Evitar Links Repetidos</span>
      <label class='sp-switch' data-tip='Não reabre o mesmo link de questão errada/anotação se ele já tiver sido aberto nos últimos 2 minutos (útil quando um id de questão colide com o de uma anotação)'>
        <input type='checkbox' id='cfgDedupeLinksEnabled' onchange='saveDedupeLinksEnabled()'/>
        <span class='sp-switch-slider'></span>
      </label>
    </div>
    <div class='sp-feedback' id='spDedupeLinksFeedback'></div>
    <div class='sp-section sp-section-flex' style='margin-top:14px;'>
      <span>🛠️ Modo Desenvolvedor</span>
      <label class='sp-switch' data-tip='Altera o botão ▼ para mostrar um menu com opções extras'>
        <input type='checkbox' id='cfgDevMode' onchange='saveDevMode()'/>
        <span class='sp-switch-slider'></span>
      </label>
    </div>
    <div class='sp-feedback' id='spDevModeFeedback'></div>
    <div class='sp-section sp-section-flex' style='margin-top:14px;'>
      <span>🔒 Bloqueio de Pausa</span>
      <label class='sp-switch' data-tip='Bloquear pausa quando desvio acumulado exceder % do foco'>
        <input type='checkbox' id='cfgLockPause' onchange='saveLockSettings()'/>
        <span class='sp-switch-slider'></span>
      </label>
    </div>
    <div id='lockPauseContent'>
      <div class='sp-row'>
        <label class='sp-label' for='cfgLockPct'>Limite de desvio <span class='sp-unit'>(%)</span></label>
        <input class='sp-input' id='cfgLockPct' type='number' min='1' max='100' step='1' value='10' onchange='saveLockSettings()'/>
      </div>
      <div class='sp-row' style='margin-top:8px;'>
        <label class='sp-label' for='cfgLockPctBlock'>Basear no bloco de 2h <span class='sp-unit'>(120min)</span></label>
        <label class='sp-switch' data-tip='Quando ativo, o % do limite de desvio é calculado sobre 120min (bloco de 2h) em vez do tempo de foco configurado'>
          <input type='checkbox' id='cfgLockPctBlock' onchange='saveLockSettings()'/>
          <span class='sp-switch-slider'></span>
        </label>
      </div>
      <div class='sp-row' style='margin-top:8px;'>
        <label class='sp-label' for='cfgLockAlertEnabled'>🔔 Alertar Limite Ultrapassado</label>
        <label class='sp-switch' data-tip='Mostra um alerta na tela assim que o desvio acumulado ultrapassar o limite e a pausa for bloqueada'>
          <input type='checkbox' id='cfgLockAlertEnabled' onchange='saveLockSettings()'/>
          <span class='sp-switch-slider'></span>
        </label>
      </div>
      <div class='sp-feedback' id='spLockFeedback'></div>
      <div class='sp-row' style='margin-top:8px;' id='cfgLockPwdCurrentRow'>
        <label class='sp-label' for='cfgLockPwdCurrent'>Senha atual</label>
        <input class='sp-input' id='cfgLockPwdCurrent' type='password' autocomplete='off' placeholder='Necessária p/ trocar' style='font-family:monospace;width:130px;text-align:left;'/>
      </div>
      <div class='sp-row' style='margin-top:8px;'>
        <label class='sp-label' for='cfgLockPwdNew'>Nova senha</label>
        <input class='sp-input' id='cfgLockPwdNew' type='password' autocomplete='new-password' placeholder='Nova senha' style='font-family:monospace;width:130px;text-align:left;'/>
      </div>
      <button class='sp-save' style='margin-top:4px;' onclick='saveLockPasswordFromUI()'>🔑 Definir/alterar senha</button>
      <div class='sp-feedback' id='spLockPwdFeedback'></div>
    </div>
    <div class='sp-section sp-section-flex' style='margin-top:14px;'>
      <span>☕ Lembrete de Café</span>
      <label class='sp-switch' data-tip='Ativar/desativar lembrete de café'>
        <input type='checkbox' id='cfgCoffeeEnabled' checked onchange='saveCoffeeEnabled()'/>
        <span class='sp-switch-slider'></span>
      </label>
    </div>
    <div id='coffeeContent'>
      <div class='sp-row'>
        <label class='sp-label' for='cfgCoffee'>Intervalo <span class='sp-unit'>(min)</span></label>
        <input class='sp-input' id='cfgCoffee' type='number' min='1' max='480' step='5' value='120' onchange='saveCoffeeSettings()'/>
      </div>
      <button class='sp-reset' style='margin-top:4px;' onclick='coffeeConfirm()'>☕ Registrar xícara de café</button>
      <div class='sp-feedback' id='spCoffeeFeedback'></div>
      <div class='sp-section' style='margin-top:12px;'>Histórico de xícaras</div>
      <div id='coffeeHistory'><div class='sp-history-empty'>Nenhuma xícara ainda hoje ☕</div></div>
      <button class='sp-reset' style='margin-top:4px;' onclick='coffeeClearHistory()'>🗑 Limpar histórico</button>
    </div>
    <div class='sp-section sp-section-flex' style='margin-top:14px;'>
      <span>👁 Monitoramento de Foco</span>
      <label class='sp-switch' data-tip='Monitora via webcam se você está olhando para o monitor durante o foco'>
        <input type='checkbox' id='cfgGazeEnabled' onchange='saveGazeEnabled()'/>
        <span class='sp-switch-slider'></span>
      </label>
    </div>
    <div class='sp-feedback' id='spGazeFeedback'></div>
    <div class='sp-section sp-section-flex' style='margin-top:14px;'>
      <span>🧪 Testar Monitoramento</span>
      <label class='sp-switch' data-tip='Liga a câmera imediatamente para teste, independente do pomodoro estar rodando — fica salvo entre aberturas do app'>
        <input type='checkbox' id='cfgGazeTestEnabled' onchange='saveGazeTestEnabled()'/>
        <span class='sp-switch-slider'></span>
      </label>
    </div>
    <div class='sp-feedback' id='spGazeTestFeedback'></div>
    <div class='sp-section sp-section-flex' style='margin-top:14px;'>
      <span>🖥️ Crosshair em Tela Cheia</span>
      <label class='sp-switch' data-tip='Mostra a mira cobrindo o monitor inteiro do Windows quando o foco ocular indicar rosto não detectado, olhando p/ os lados ou indefinido, por 2s seguidos'>
        <input type='checkbox' id='cfgCrosshairFullscreenEnabled' onchange='saveCrosshairFullscreenEnabled()'/>
        <span class='sp-switch-slider'></span>
      </label>
    </div>
    <div class='sp-feedback' id='spCrosshairFullscreenFeedback'></div>
    <div class='sp-section' style='margin-top:14px;'>🔑 Autenticação</div>
    <button class='sp-save' onclick='conectarAutomaticamente()'>🔗 Conectar automaticamente</button>
    <div class='sp-feedback' id='spAutoConnectFeedback'></div>
    <div id='manualCodeBox' style='display:none;margin-top:6px;'>
      <input id='cfgManualCode' type='text' placeholder='Cole o código de autorização aqui...'
        style='width:100%;box-sizing:border-box;padding:6px 7px;border:1px solid rgba(123,31,162,.4);border-radius:5px;background:#0d0d1a;color:#e0c8f0;font-size:10px;font-family:monospace;'>
      <button class='sp-save' style='margin-top:6px;' onclick=""hostCall('SubmitManualCode', document.getElementById('cfgManualCode').value)"">Continuar</button>
    </div>
    <div style='margin-top:10px;'>
      <a href='javascript:void(0)' id='manualTokenToggle' onclick='toggleManualTokenBox()'
        style='font-size:10px;color:#9d7bb0;text-decoration:underline;cursor:pointer;'>Prefiro colar o token manualmente</a>
    </div>
    <div id='manualTokenBox' style='display:none;margin-top:8px;'>
      <div style='font-size:10px;color:#7f848e;margin-bottom:8px;line-height:1.4;'>sistemadetarefas.online → Configurações → Gerar token</div>
      <textarea id='cfgApiToken' rows='3'
        style='width:100%;background:#0d0d1a;border:1px solid rgba(123,31,162,.4);border-radius:5px;color:#e0c8f0;font-size:10px;padding:6px 7px;font-family:monospace;resize:none;box-sizing:border-box;'
        placeholder='Cole o token aqui...'></textarea>
      <button class='sp-save' style='margin-top:6px;' onclick='saveApiToken()'>Salvar token</button>
      <div class='sp-feedback' id='spManualTokenFeedback'></div>
    </div>
  </div>
</div>
<div id='settingsOverlay' class='settings-overlay' onclick='togglePanel()'></div>

<!-- Overlay desvio -->
<div id='desvioOverlay' style='display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:9999;'>
  <div style='position:absolute;top:50%;left:50%;width:300px;margin:-110px 0 0 -150px;background:#1a1a2e;border:2px solid #fc8181;border-radius:10px;padding:16px 18px;'>
    <div style='color:#fc8181;font-size:12px;font-weight:700;margin-bottom:4px;'>⚠ MOTIVO DO DESVIO</div>
    <div id='desvioOverlayDur' style='color:#feb2b2;font-size:11px;margin-bottom:10px;'></div>
    <input id='desvioMotivoInput' type='text' placeholder='Ex: reuniao, lanche...' maxlength='200'
      onkeydown='if(event.key===""Enter"")confirmDesvioMotivo();'
      style='width:100%;background:#2d2d4e;border:1px solid #fc8181;border-radius:6px;padding:7px 9px;color:#fff;font-size:12px;font-family:inherit;box-sizing:border-box;'/>
    <button onclick='confirmDesvioMotivo()' style='margin-top:10px;width:100%;background:#c53030;color:#fff;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Confirmar e Retomar</button>
  </div>
</div>

<!-- Overlay fim de foco -->
<div id='fimFocoOverlay' style='display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:9999;'>
  <div style='position:absolute;top:50%;left:50%;width:300px;margin:-100px 0 0 -150px;background:#1a1a2e;border:2px solid #4caf50;border-radius:10px;padding:16px 18px;'>
    <div style='color:#a5d6a7;font-size:13px;font-weight:700;margin-bottom:6px;'>🎯 FOCO CONCLUÍDO!</div>
    <div style='color:#c8e6c9;font-size:11px;margin-bottom:14px;'>Parabéns! Hora de descansar por <b id='fimFocoBreakMin'></b> minutos.</div>
    <div id='btnMeritoExtendWrap' style='display:none;margin-bottom:8px;'>
      <button onclick='continuarParaMerito()' id='btnMeritoExtend' style='width:100%;background:linear-gradient(135deg,#b8860b,#FFD700);color:#1a1a00;border:none;border-radius:6px;padding:9px;font-size:12px;font-weight:800;cursor:pointer;letter-spacing:0.3px;'>⭐ Estudar mais <span id='meritoExtraMin'></span> min → Mérito!</button>
    </div>
    <button onclick='fecharFimFoco()' style='width:100%;background:#388e3c;color:#fff;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Iniciar Descanso</button>
  </div>
</div>

<!-- Overlay fim de descanso -->
<div id='fimDescansoOverlay' style='display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:9999;'>
  <div style='position:absolute;top:50%;left:50%;width:300px;margin:-100px 0 0 -150px;background:#1a1a2e;border:2px solid #7b1fa2;border-radius:10px;padding:16px 18px;'>
    <div style='color:#ce93d8;font-size:13px;font-weight:700;margin-bottom:6px;'>✅ DESCANSO CONCLUÍDO!</div>
    <div style='color:#e0c8f0;font-size:11px;margin-bottom:10px;'>Descansou bem? Hora de voltar ao foco!</div>
    <div id='fimDescansoDesvioWrap' style='display:none;color:#feb2b2;font-size:11px;margin-bottom:10px;'>⚠ Desvio acumulado enquanto aguarda: <b id='fimDescansoDesvioVal'>00:00</b></div>
    <button onclick='fecharFimDescanso()' style='width:100%;background:#7b1fa2;color:#fff;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Iniciar Foco</button>
  </div>
</div>

<!-- Overlay tempo estimado excedido -->
<div id='tempoExcedidoOverlay' style='display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:9999;'>
  <div style='position:absolute;top:50%;left:50%;width:300px;margin:-100px 0 0 -150px;background:#1a1a2e;border:2px solid #f6ad55;border-radius:10px;padding:16px 18px;'>
    <div style='color:#f6ad55;font-size:12px;font-weight:700;margin-bottom:4px;'>⏰ TEMPO ESTIMADO EXCEDIDO</div>
    <div id='tempoExcedidoMsg' style='color:#fbd38d;font-size:11px;margin-bottom:14px;'></div>
    <button onclick='fecharTempoExcedido()' style='width:100%;background:#b7791f;color:#fff;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Continuar</button>
  </div>
</div>

<!-- Overlay limite de desvio ultrapassado (Bloqueio de Pausa) -->
<div id='lockLimiteOverlay' style='display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:9999;'>
  <div style='position:absolute;top:50%;left:50%;width:300px;margin:-100px 0 0 -150px;background:#1a1a2e;border:2px solid #e53e3e;border-radius:10px;padding:16px 18px;'>
    <div style='color:#fc8181;font-size:12px;font-weight:700;margin-bottom:4px;'>🔒 LIMITE DE DESVIO ULTRAPASSADO</div>
    <div id='lockLimiteMsg' style='color:#feb2b2;font-size:11px;margin-bottom:14px;'></div>
    <button onclick='fecharLockLimiteOverlay()' style='width:100%;background:#c53030;color:#fff;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Entendi</button>
  </div>
</div>

<!-- Overlay pausa administrativa -->
<div id='adminPauseOverlay' style='display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:9999;'>
  <div style='position:absolute;top:50%;left:50%;width:300px;margin:-100px 0 0 -150px;background:#1a1a2e;border:2px solid #2196f3;border-radius:10px;padding:16px 18px;'>
    <div style='color:#90caf9;font-size:12px;font-weight:700;margin-bottom:8px;display:flex;align-items:center;justify-content:space-between;'>
      <span>PAUSA ADMINISTRATIVA</span>
      <span style='position:relative;display:inline-flex;'>
        <svg onclick='toggleAdminPauseInfoTip(event)' width='16' height='16' viewBox='0 0 16 16' style='cursor:pointer;flex-shrink:0;'>
          <circle cx='8' cy='8' r='7' fill='none' stroke='#2196f3' stroke-width='1'/>
          <rect x='7.25' y='4' width='1.5' height='5.3' rx='0.75' fill='#2196f3'/>
          <circle cx='8' cy='11.5' r='1' fill='#2196f3'/>
        </svg>
        <div id='adminPauseInfoTip' style='display:none;position:absolute;top:20px;right:0;width:220px;background:#2a2a35;border:1px solid rgba(255,255,255,.12);border-radius:6px;padding:8px 10px;font-size:10.5px;font-weight:400;color:#fff;box-shadow:0 4px 14px rgba(0,0,0,.4);line-height:1.5;z-index:10;text-align:left;'>
          Enquanto ativa, o temporizador continua contando normalmente, mas o tempo <b>não</b> é somado à sessão atual, ao total de hoje, nem contado como desvio. Direito de 120 min por dia (até 60 min contínuos por vez) — só desconta o tempo que você realmente usar.
        </div>
      </span>
    </div>
    <div id='adminPauseSlots' style='margin-bottom:8px;'></div>
    <button onclick='closeAdminPauseOverlay()' style='margin-top:4px;width:100%;background:rgba(255,255,255,.08);color:#a0aec0;border:none;border-radius:6px;padding:7px;font-size:11px;cursor:pointer;'>Fechar</button>
  </div>
</div>

<!-- Overlay senha para desativar o Bloqueio de Pausa -->
<div id='lockPwdOverlay' style='display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:9999;'>
  <div style='position:absolute;top:50%;left:50%;width:280px;margin:-90px 0 0 -140px;background:#1a1a2e;border:2px solid #7b1fa2;border-radius:10px;padding:16px 18px;'>
    <div style='color:#e0c8f0;font-size:12px;font-weight:700;margin-bottom:8px;'>🔒 SENHA PARA DESATIVAR</div>
    <div style='color:#a0aec0;font-size:10.5px;margin-bottom:10px;line-height:1.4;'>Digite a senha definida para desativar o Bloqueio de Pausa.</div>
    <input id='lockPwdInput' type='password' autocomplete='off'
      style='width:100%;box-sizing:border-box;padding:7px 8px;border:1px solid rgba(123,31,162,.4);border-radius:5px;background:#0d0d1a;color:#e0c8f0;font-size:11px;font-family:monospace;'
      onkeydown='if(event.key===""Enter"")confirmLockPwd();'/>
    <div id='lockPwdError' style='color:#e57373;font-size:10px;margin-top:6px;min-height:12px;'></div>
    <div style='display:flex;gap:8px;margin-top:10px;'>
      <button onclick='cancelLockPwd()' style='flex:1;background:rgba(255,255,255,.08);color:#a0aec0;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Cancelar</button>
      <button onclick='confirmLockPwd()' style='flex:1;background:#7b1fa2;color:#fff;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Confirmar</button>
    </div>
  </div>
</div>

<!-- Overlay confirmação reset sessão -->
<div id='resetConfirmOverlay' style='display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:9999;'>
  <div style='position:absolute;top:50%;left:50%;width:300px;margin:-90px 0 0 -150px;background:#1a1a2e;border:2px solid #718096;border-radius:10px;padding:16px 18px;'>
    <div style='color:#e2e8f0;font-size:12px;font-weight:700;margin-bottom:6px;'>↺ ZERAR SESSÃO LOCAL</div>
    <div id='resetConfirmMsg' style='color:#a0aec0;font-size:11px;margin-bottom:14px;'>Isso vai zerar o timer, sessão atual, total de hoje e a grade de horas. Dados já salvos no servidor não são afetados.</div>
    <div style='display:flex;gap:8px;'>
      <button onclick='document.getElementById(""resetConfirmOverlay"").style.display=""none"";resetConfirmRestoreMsg()' style='flex:1;background:rgba(255,255,255,.08);color:#a0aec0;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Cancelar</button>
      <button onclick='resetSessaoConfirmado()' style='flex:1;background:#c53030;color:#fff;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Zerar</button>
    </div>
  </div>
</div>

<!-- Tooltip -->
<div id='customTooltip' style='display:none;position:fixed;z-index:99999;pointer-events:none;max-width:180px;background:#2a1040;border:1px solid #7b1fa2;border-radius:6px;padding:5px 9px;font-size:10px;font-weight:600;color:#e0c8f0;box-shadow:0 4px 16px rgba(0,0,0,.6);line-height:1.4;'></div>

<script>{BuildJs()}</script>
</body></html>";

        private string BuildCss() => @"
*{margin:0;padding:0;box-sizing:border-box;}
body{background:#1E1E1E;color:#fff;font-family:'Segoe UI',sans-serif;overflow:hidden;height:100vh;user-select:none;border-radius:15px;}
.widget{background:#1E1E1E;height:100vh;display:flex;flex-direction:column;border-radius:15px;border:1px solid rgba(255,255,255,.1);box-shadow:0 10px 40px rgba(0,0,0,.5);overflow:hidden;}
.header{background:#2D2D2D;padding:12px 15px;display:flex;justify-content:space-between;align-items:center;border-bottom:1px solid #404040;cursor:move;border-radius:15px 15px 0 0;position:relative;overflow:hidden;}
.header.running::before{content:'';width:calc(100% + 2px);height:calc(100% - 12px);position:absolute;top:6px;left:0;background:#353638;z-index:1;animation:bTB 2s infinite;}
.header.running::after{content:'';width:calc(100% - 12px);height:calc(100% + 4px);position:absolute;top:-2px;left:6px;background:#353638;z-index:1;animation:bLR 2s infinite;}
@keyframes bTB{0%,100%{transform:scaleY(1)}50%{transform:scaleY(0)}}
@keyframes bLR{0%,100%{transform:scaleX(1)}50%{transform:scaleX(0)}}
.header.running.break::before,.header.running.break::after{background:#4caf50;}
.title{font-weight:600;color:#e0e0e0;font-size:14px;display:flex;align-items:center;gap:8px;position:relative;z-index:2;}
.task-title{max-width:120px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
.time-mini{background:#7b1fa2;color:#fff;padding:4px 8px;border-radius:12px;font-size:11px;font-weight:bold;font-family:monospace;}
.time-mini.break{background:#4caf50;}
.time-mini.adminpause{background:#2196f3;}
.controls{display:flex;gap:10px;align-items:center;position:relative;z-index:2;}
.btn-start{background:#7b1fa2;border:none;color:#fff;padding:8px 16px;border-radius:6px;cursor:pointer;font-size:12px;font-weight:600;transition:all .3s;margin-left:8px;}
.btn-start:hover{background:#8e24aa;transform:translateY(-1px);}
.btn-start.running{background:#e53935;}
.btn-min,.btn-close{background:rgba(255,255,255,.1);border:none;color:#abb2bf;width:24px;height:24px;border-radius:4px;cursor:pointer;font-size:12px;font-weight:bold;margin-left:8px;transition:all .2s;}
.btn-min:hover{background:rgba(255,255,255,.2);color:#fff;}
.btn-min-wrap{position:relative;display:inline-block;}
.dev-badge{position:absolute;top:-5px;right:-5px;background:#ff9800;color:#1a1a1a;font-size:8px;font-weight:800;line-height:1;padding:2px 3px;border-radius:6px;pointer-events:none;box-shadow:0 0 0 1px #1e1e1e;}
.dev-min-menu{position:fixed;background:#2a2a35;border:1px solid rgba(255,255,255,.12);border-radius:6px;box-shadow:0 4px 14px rgba(0,0,0,.4);z-index:9999;display:flex;flex-direction:column;min-width:150px;overflow:hidden;}
.dev-min-menu button{background:none;border:none;color:#e0e0e0;text-align:left;padding:7px 10px;font-size:11px;cursor:pointer;white-space:nowrap;}
.dev-min-menu button:hover{background:rgba(255,255,255,.08);}
.dev-min-menu button.active{color:#ff9800;font-weight:700;}
.btn-close{background:rgba(244,67,54,.3);color:#ff6b6b;}
.btn-close:hover{background:rgba(244,67,54,.5);color:#fff;}
.content{padding:20px;flex:1;display:flex;flex-direction:column;gap:15px;}
.timer-row{display:flex;align-items:center;justify-content:center;gap:12px;margin:10px 0;}
.timer{font-size:48px;font-weight:bold;font-family:monospace;color:#7b1fa2;}
.timer.adminpause{color:#2196f3;}
.timer-wrap{position:relative;display:inline-block;}
.dev-half-badge{position:absolute;top:-2px;right:-38px;background:#ff9800;color:#1a1a1a;font-size:10px;font-weight:800;padding:2px 5px;border-radius:8px;opacity:0;pointer-events:none;white-space:nowrap;}
.dev-half-badge.pulse{animation:devHalfPulse 0.9s ease-out;}
@keyframes devHalfPulse{
  0%   { opacity:1; transform:translateY(0);   }
  70%  { opacity:1; transform:translateY(-6px);}
  100% { opacity:0; transform:translateY(-10px);}
}
.timer.break{color:#4caf50;}
.hg-wrap{position:relative;cursor:pointer;opacity:.5;transition:opacity .3s,transform .2s;padding:4px;}
.hg-wrap:hover{opacity:1;transform:scale(1.1);}
.sync-pulse{position:absolute;top:50%;left:50%;width:0;height:0;border-radius:50%;background:rgba(123,31,162,.3);transform:translate(-50%,-50%);pointer-events:none;opacity:0;}
.sync-pulse.active{animation:pulse .6s ease-out forwards;}
@keyframes pulse{0%{width:0;height:0;opacity:.6}100%{width:60px;height:60px;opacity:0}}
.session-badge{padding:4px 12px;border-radius:20px;background:#7b1fa2;color:#fff;font-size:12px;font-weight:bold;}
.session-badge.break{background:#4caf50;}
.session-badge.break.merit{background:linear-gradient(135deg,#388e3c,#b8860b);border:1px solid #FFD700;}
.session-badge.admin-pause-badge{background:#2196f3;}
#badgeDropdownArrow.break{background:#4caf50;}
#badgeDropdownArrow.break.merit{background:linear-gradient(135deg,#388e3c,#b8860b);}
#badgeDropdownMenu{animation:fadeDropdown .15s ease;}
@keyframes fadeDropdown{from{opacity:0;transform:translateY(-4px)}to{opacity:1;transform:translateY(0)}}
.progress{height:6px;background:#404040;border-radius:3px;margin:15px 0;overflow:hidden;}
.progress-bar{height:100%;background:#7b1fa2;border-radius:3px;transition:width 1s linear;}
.progress-bar.break{background:#4caf50;}
.progress-bar.adminpause{background:#2196f3;}
.stats{display:flex;justify-content:space-between;}
.stat{text-align:center;}
.lbl{font-size:11px;color:#7f848e;margin-bottom:2px;}
.val{font-size:14px;font-weight:bold;color:#7b1fa2;}
.task-info{text-align:center;font-size:10px;color:#e0e0e0;background:rgba(123,31,162,.1);padding:8px;border-radius:8px;border:1px solid rgba(123,31,162,.3);}
.gaze-block{margin-top:8px;background:rgba(255,255,255,.03);border:1px solid rgba(255,255,255,.08);border-radius:8px;padding:8px 10px;}
.gaze-row{display:flex;justify-content:space-between;align-items:center;font-size:11px;color:#b0b8c1;}
.gaze-pct{font-weight:bold;color:#7b1fa2;font-size:13px;}
.gaze-bar-bg{height:5px;background:#404040;border-radius:3px;margin:6px 0 4px;overflow:hidden;}
.gaze-bar-fill{height:100%;background:#7b1fa2;border-radius:3px;transition:width .6s ease,background .6s ease;width:0%;}
.gaze-sub-row{display:flex;align-items:center;justify-content:center;gap:6px;}
.gaze-sub{font-size:10px;color:#7f848e;text-align:center;}
.gaze-camera-btn{background:none;border:none;color:#7f848e;cursor:pointer;padding:2px 4px;border-radius:4px;display:flex;align-items:center;transition:color .2s,background .2s;flex-shrink:0;}
.gaze-camera-btn:hover{color:#e0e0e0;background:rgba(255,255,255,.08);}
.gaze-camera-btn.active{color:#7b1fa2;}
#gradeMount{width:100%;overflow:hidden;margin-top:2px;}
.btn-burger{background:none;border:none;color:rgba(255,255,255,.35);font-size:15px;cursor:pointer;padding:2px 4px;border-radius:4px;transition:color .2s,background .2s;line-height:1;margin-right:2px;}
.btn-burger:hover{color:rgba(255,255,255,.8);background:rgba(255,255,255,.08);}
.settings-panel{position:fixed;top:0;right:-280px;width:270px;height:100%;background:#1a1a2e;border-left:1px solid rgba(123,31,162,.5);z-index:8000;transition:right .28s cubic-bezier(.4,0,.2,1);display:flex;flex-direction:column;box-shadow:-4px 0 24px rgba(0,0,0,.5);}
.settings-panel.open{right:0;}
.settings-overlay{position:fixed;inset:0;background:rgba(0,0,0,.0);z-index:7999;display:none;cursor:pointer;}
.settings-overlay.open{display:block;}
.sp-header{display:flex;justify-content:space-between;align-items:center;padding:13px 14px 11px;border-bottom:1px solid rgba(123,31,162,.4);color:#ce93d8;font-size:12px;font-weight:700;letter-spacing:.5px;}
.sp-close{background:none;border:none;color:#7f848e;font-size:14px;cursor:pointer;padding:0 2px;transition:color .2s;}
.sp-close:hover{color:#ff6b6b;}
.sp-body{padding:14px 14px 40px;overflow-y:auto;flex:1;}
.sp-section{font-size:9px;font-weight:700;letter-spacing:1px;color:#7b1fa2;text-transform:uppercase;margin-bottom:10px;border-bottom:1px solid rgba(123,31,162,.25);padding-bottom:4px;}
.sp-section-flex{display:flex;align-items:center;justify-content:space-between;border-bottom:1px solid rgba(123,31,162,.25);}
.sp-switch{position:relative;display:inline-block;width:30px;height:16px;flex-shrink:0;cursor:pointer;}
.sp-switch input{opacity:0;width:0;height:0;}
.sp-switch-slider{position:absolute;inset:0;background:#3a3a3a;border-radius:16px;transition:.2s;}
.sp-switch-slider::before{content:'';position:absolute;height:12px;width:12px;left:2px;top:2px;background:#b0b8c1;border-radius:50%;transition:.2s;}
.sp-switch input:checked + .sp-switch-slider{background:#7b1fa2;}
.sp-switch input:checked + .sp-switch-slider::before{transform:translateX(14px);background:#fff;}
.sp-row{display:flex;align-items:center;justify-content:space-between;margin-bottom:10px;gap:8px;}
.sp-label{font-size:11px;color:#b0b8c1;flex:1;line-height:1.3;}
.sp-unit{color:#555;font-size:9px;}
.sp-input{width:64px;background:#0d0d1a;border:1px solid rgba(123,31,162,.4);border-radius:5px;color:#e0c8f0;font-size:12px;padding:4px 7px;text-align:right;font-family:monospace;transition:border .2s;}
.sp-input:focus{outline:none;border-color:#7b1fa2;}
.sp-save{width:100%;margin-top:16px;padding:8px;background:#7b1fa2;border:none;border-radius:6px;color:#fff;font-size:12px;font-weight:700;cursor:pointer;transition:background .2s;}
.sp-save:hover{background:#8e24aa;}
.sp-reset{width:100%;margin-top:8px;padding:7px;background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.15);border-radius:6px;color:#b0b8c1;font-size:11px;font-weight:600;cursor:pointer;transition:background .2s;}
.sp-reset:hover{background:rgba(255,80,80,.15);color:#ff6b6b;border-color:rgba(255,80,80,.3);}
.sp-feedback{font-size:10px;color:#a5d6a7;text-align:center;margin-top:8px;min-height:14px;}
/* ── Café Timer ── */
.coffee-emoji{font-size:22px;cursor:pointer;line-height:1;user-select:none;transition:transform .2s,filter .2s;filter:grayscale(60%) opacity(.55);}
.coffee-emoji.ready{filter:none;animation:coffeeWiggle .6s ease-in-out infinite alternate;}
.coffee-emoji:hover{transform:scale(1.2);}
@keyframes coffeeWiggle{0%{transform:rotate(-8deg) scale(1.1)}100%{transform:rotate(8deg) scale(1.15)}}
/* histórico no painel */
.sp-history-item{display:flex;justify-content:space-between;align-items:center;padding:4px 0;border-bottom:1px solid rgba(123,31,162,.15);font-size:10px;color:#b0b8c1;}
.sp-history-item:last-child{border-bottom:none;}
.sp-history-time{color:#7b1fa2;font-weight:700;font-family:monospace;}
.sp-history-empty{font-size:10px;color:#555;text-align:center;padding:8px 0;}
.sp-history-remove{color:#666;cursor:pointer;font-size:11px;font-weight:700;padding:0 2px;flex-shrink:0;transition:color .15s;}
.sp-history-remove:hover{color:#fc8181;}
/* ── Anti-pause lock ── */
.sp-check-row{display:flex;align-items:center;gap:8px;margin-bottom:10px;}
.sp-checkbox{width:15px;height:15px;accent-color:#7b1fa2;cursor:pointer;flex-shrink:0;}
.sp-check-label{font-size:11px;color:#b0b8c1;cursor:pointer;line-height:1.3;}
.btn-start.locked{background:#555;cursor:not-allowed;opacity:.6;}
.btn-start.locked:hover{background:#555;transform:none;}";

        private string BuildJs() => @"
var API = 'https://sistemadetarefas.online';

// Retorna headers de autenticação Bearer.
// Sem token salvo, não autentica — chamadas vão falhar com 401 até o
// usuário conectar a conta (fluxo OAuth+PKCE em Configurações).
function authHeaders() {
    var token = localStorage.getItem('api_bearer_token') || '';
    if (!token) {
        console.warn('[AUTH] Nenhum token configurado — conecte sua conta em Configurações.');
        return { 'Content-Type': 'application/json' };
    }
    return { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token };
}

function saveApiToken() {
    var input = document.getElementById('cfgApiToken');
    var fb    = document.getElementById('spManualTokenFeedback');
    if (!input) return;
    var token = input.value.trim();
    if (!token) {
        fb.textContent = 'Token não pode ser vazio.';
        fb.style.color = '#fc8181';
        return;
    }
    localStorage.setItem('api_bearer_token', token);
    fb.textContent = '✅ Token salvo com sucesso!';
    fb.style.color = '#a5d6a7';
    setTimeout(function() { fb.textContent = ''; }, 3000);
    console.log('[AUTH] Token Bearer salvo no localStorage');
}

function toggleManualTokenBox() {
    var box = document.getElementById('manualTokenBox');
    var link = document.getElementById('manualTokenToggle');
    if (!box) return;
    var open = box.style.display !== 'none';
    box.style.display = open ? 'none' : 'block';
    if (link) link.textContent = open ? 'Prefiro colar o token manualmente' : 'Ocultar token manual';
}

// Dispara o fluxo de conexão automática (loopback + PKCE) no C#.
// Mostra o campo de código manual desde já, como fallback visível
// caso o redirecionamento automático não chegue no app.
function conectarAutomaticamente() {
    var box = document.getElementById('manualCodeBox');
    if (box) box.style.display = 'block';
    var fb = document.getElementById('spAutoConnectFeedback');
    if (fb) {
        fb.textContent = 'Abrindo navegador… se não detectar automaticamente, cole o código acima.';
        fb.style.color = '#b0b8c1';
    }
    hostCall('ConectarSistema');
}

// Chamado pelo C# (Form1.ConectarSistemaAsync) quando o token final chega.
function onAutoTokenReceived(token) {
    localStorage.setItem('api_bearer_token', token);
    var fb = document.getElementById('spAutoConnectFeedback');
    if (fb) { fb.textContent = '✅ Conectado automaticamente!'; fb.style.color = '#a5d6a7'; }
    var box = document.getElementById('manualCodeBox');
    if (box) box.style.display = 'none';
    var input = document.getElementById('cfgApiToken');
    if (input) input.placeholder = '••••••••' + token.slice(-6) + ' (conectado automaticamente)';
    console.log('[AUTH] Token recebido via conexão automática e salvo.');
}

function hostCall(method) {
    var args = Array.prototype.slice.call(arguments, 1);
    try { var h = window.chrome.webview.hostObjects.sync.external; h[method].apply(h, args); }
    catch(e) { console.error('[hostCall] ' + method + ':', e); }
}

var isRunning             = false;
var currentTime           = 3600; // será sobrescrito pelo loadState ou cfg
var isBreakTime           = false;
var currentSessionSeconds = 0;
var totalTodaySeconds     = 0;
var tickInterval          = null;

var devModeEnabled        = false;
var devDoubleCountActive  = false;

var ADMIN_PAUSE_DURATION  = 3600; // 60 min em segundos, fixo por pausa
var ADMIN_PAUSE_MAX_USES  = 2;
var ADMIN_PAUSE_2_MIN_FOCUS_SECONDS = 180 * 60; // Pausa 2 (índice 1) só libera após 180min (3h) de foco no dia
var DEV_PAUSE_SLOT_INDEX  = 2; // 'Pausa Dev' — não entra no array adminPauseSlots, é ilimitada (só aparece com Modo Desenvolvedor ativo)
var DEV_PAUSE_MIN_FOCUS_SECONDS = 360 * 60; // Pausa Dev só libera após 360min (6h) de foco no dia
var adminPauseActive      = false;
var adminPauseActiveSlot  = -1; // índice da pausa em uso (0 ou 1), -1 se nenhuma
var adminPauseSecondsLeft = 0;
var adminPauseSlots       = (function() { var a=[]; for (var i=0;i<2;i++) a.push(3600); return a; })(); // saldo de cada pausa (segundos)

// 'Pausa 1' / 'Pausa 2' / 'Pausa Dev' — usado em logs e nos textos do menu/overlay.
function pauseSlotLabel(slotIndex) {
    return slotIndex === DEV_PAUSE_SLOT_INDEX ? 'Dev' : String(slotIndex + 1);
}

function loadDevMode() {
    try {
        devModeEnabled       = localStorage.getItem('devModeEnabled') === '1';
        devDoubleCountActive = localStorage.getItem('devDoubleCount') === '1';
    } catch(e) {}
    var chk = document.getElementById('cfgDevMode'); if (chk) chk.checked = devModeEnabled;
    setSectionVisible('updateIntervalsSection', devModeEnabled);
    updateDevBadge();
}

function saveDevMode() {
    var chk = document.getElementById('cfgDevMode');
    devModeEnabled = chk ? chk.checked : false;
    try { localStorage.setItem('devModeEnabled', devModeEnabled ? '1' : '0'); } catch(e) {}
    // Ao desligar o modo dev, desativa também a contagem pela metade
    if (!devModeEnabled) {
        devDoubleCountActive = false;
        try { localStorage.setItem('devDoubleCount', '0'); } catch(e) {}
        closeDevMinMenu();
    }
    setSectionVisible('updateIntervalsSection', devModeEnabled);
    updateDevBadge();
    var fb = document.getElementById('spDevModeFeedback');
    if (fb) { fb.textContent = devModeEnabled ? 'Modo desenvolvedor ativado.' : 'Modo desenvolvedor desativado.'; setTimeout(function(){ fb.textContent=''; }, 2000); }
    console.log('[DEV] Modo desenvolvedor ' + (devModeEnabled ? 'ativado' : 'desativado'));
}

function updateDevBadge() {
    var badge = document.getElementById('devDoubleBadge');
    if (badge) badge.style.display = (devModeEnabled && devDoubleCountActive) ? '' : 'none';
    var btn = document.getElementById('devMinMenuDoubleBtn');
    if (btn) {
        btn.classList.toggle('active', devDoubleCountActive);
        btn.textContent = (devDoubleCountActive ? '✓ ' : '⧉ ') + 'Contar pela metade';
    }
}

function minimizeButtonClick(ev) {
    if (!devModeEnabled) { hostCall('MinimizeWidget'); return; }
    ev.stopPropagation();
    var content = document.getElementById('pomodoroContent');
    var isMinimized = content && content.style.display === 'none';
    if (isMinimized) {
        // Só restaura a janela (mecanismo já existente), sem abrir o menu
        hostCall('MinimizeWidget');
    } else {
        toggleDevMinMenu();
    }
}

function toggleDevMinMenu() {
    var menu = document.getElementById('devMinMenu');
    var open = menu && menu.style.display !== 'none';
    if (open) closeDevMinMenu();
    else openDevMinMenu();
}

function openDevMinMenu() {
    var menu = document.getElementById('devMinMenu');
    var wrap = document.getElementById('btnMinWrap');
    if (!menu || !wrap) return;
    var r = wrap.getBoundingClientRect();
    menu.style.top   = (r.bottom + 4) + 'px';
    menu.style.right = (window.innerWidth - r.right) + 'px';
    menu.style.left  = 'auto';
    menu.style.display = 'flex';
}

function closeDevMinMenu() {
    var menu = document.getElementById('devMinMenu');
    if (menu) menu.style.display = 'none';
}

function devMinMenuMinimize(ev) {
    ev.stopPropagation();
    closeDevMinMenu();
    hostCall('MinimizeWidget');
}

function devMinMenuToggleDouble(ev) {
    ev.stopPropagation();
    devDoubleCountActive = !devDoubleCountActive;
    try { localStorage.setItem('devDoubleCount', devDoubleCountActive ? '1' : '0'); } catch(e) {}
    updateDevBadge();
    closeDevMinMenu();
    console.log('[DEV] Contagem pela metade ' + (devDoubleCountActive ? 'ativada' : 'desativada'));
}

function updateAdminPauseMenuButton() {
    var btn = document.getElementById('devMinMenuAdminPauseBtn');
    if (!btn) return;
    if (adminPauseActive) {
        btn.textContent = 'Pausa administrativa (ativa — Pausa ' + pauseSlotLabel(adminPauseActiveSlot) + ')';
        btn.style.color = '#64b5f6';
    } else {
        var disponiveis = adminPauseSlots.filter(function(s, idx) {
            if (s <= 0) return false;
            if (idx === 1 && totalTodaySeconds < ADMIN_PAUSE_2_MIN_FOCUS_SECONDS) return false; // Pausa 2 ainda bloqueada
            return true;
        }).length;
        btn.textContent = 'Pausa administrativa (' + disponiveis + '/' + ADMIN_PAUSE_MAX_USES + ' disponíveis)';
        btn.style.color = disponiveis > 0 ? '' : '#666';
    }
}

function devMinMenuOpenAdminPause(ev) {
    ev.stopPropagation();
    closeDevMinMenu();
    renderAdminPauseOverlay();
    document.getElementById('adminPauseOverlay').style.display = 'block';
}

function closeAdminPauseOverlay() {
    document.getElementById('adminPauseOverlay').style.display = 'none';
    var tip = document.getElementById('adminPauseInfoTip'); if (tip) tip.style.display = 'none';
}

function toggleAdminPauseInfoTip(ev) {
    ev.stopPropagation();
    var tip = document.getElementById('adminPauseInfoTip');
    if (!tip) return;
    tip.style.display = (tip.style.display === 'none') ? 'block' : 'none';
}

document.addEventListener('click', function(ev) {
    var tip = document.getElementById('adminPauseInfoTip');
    if (tip && tip.style.display !== 'none' && !tip.parentNode.contains(ev.target)) tip.style.display = 'none';
});

function fmtMinSec(totalSeconds) {
    var m = Math.floor(totalSeconds / 60), s = Math.floor(totalSeconds % 60);
    return m + ':' + (s < 10 ? '0' : '') + s;
}

function renderAdminPauseOverlay() {
    var mount = document.getElementById('adminPauseSlots');
    if (!mount) return;
    var html = '';
    for (var i = 0; i < ADMIN_PAUSE_MAX_USES; i++) {
        var remaining = adminPauseSlots[i];
        var isThisActive = adminPauseActive && adminPauseActiveSlot === i;
        var label, btnLabel, btnBg, btnDisabled;
        if (isThisActive) {
            label = 'Pausa ' + (i + 1) + ' — ativa, encerra em ' + fmtMinSec(remaining);
            btnLabel = 'Encerrar agora'; btnBg = '#c53030'; btnDisabled = false;
        } else if (adminPauseActive) {
            // outra pausa está ativa no momento — trava esta
            label = 'Pausa ' + (i + 1) + ' — ' + fmtMinSec(remaining) + ' restantes';
            btnLabel = 'Indisponível agora'; btnBg = '#4a5568'; btnDisabled = true;
        } else if (i === 1 && totalTodaySeconds < ADMIN_PAUSE_2_MIN_FOCUS_SECONDS) {
            // Pausa 2 fica bloqueada até acumular 180min (3h) de foco no dia
            var faltamMin = Math.ceil((ADMIN_PAUSE_2_MIN_FOCUS_SECONDS - totalTodaySeconds) / 60);
            label = 'Pausa 2 — libera com 3h de foco no dia (faltam ' + faltamMin + 'min)';
            btnLabel = 'Bloqueada'; btnBg = '#4a5568'; btnDisabled = true;
        } else if (remaining <= 0) {
            label = 'Pausa ' + (i + 1) + ' — esgotada hoje';
            btnLabel = 'Esgotada'; btnBg = '#4a5568'; btnDisabled = true;
        } else {
            label = 'Pausa ' + (i + 1) + ' — ' + fmtMinSec(remaining) + ' disponíveis';
            btnLabel = 'Iniciar (60 min)'; btnBg = '#1976d2'; btnDisabled = false;
        }
        html += '<div style=""display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:8px;"">' +
                    '<span style=""color:#e3f2fd;font-size:10.5px;flex:1;"">' + label + '</span>' +
                    '<button onclick=""adminPauseSlotClick(' + i + ')"" ' + (btnDisabled ? 'disabled' : '') + ' style=""background:' + btnBg + ';color:#fff;border:none;border-radius:6px;padding:6px 9px;font-size:10.5px;font-weight:700;cursor:' + (btnDisabled ? 'default' : 'pointer') + ';white-space:nowrap;"">' + btnLabel + '</button>' +
                '</div>';
    }

    // Pausa Dev — só aparece com Modo Desenvolvedor ativo. Ilimitada (não
    // consome saldo nenhum, nunca esgota), mas só libera após 6h de estudo
    // no dia — é uma ferramenta de teste, não uma pausa de graça.
    if (devModeEnabled) {
        var isDevActive = adminPauseActive && adminPauseActiveSlot === DEV_PAUSE_SLOT_INDEX;
        var devLabel, devBtnLabel, devBtnBg, devBtnDisabled;
        if (isDevActive) {
            devLabel = 'Pausa Dev — ativa, encerra em ' + fmtMinSec(adminPauseSecondsLeft);
            devBtnLabel = 'Encerrar agora'; devBtnBg = '#c53030'; devBtnDisabled = false;
        } else if (adminPauseActive) {
            devLabel = 'Pausa Dev — indisponível agora'; devBtnLabel = 'Indisponível agora'; devBtnBg = '#4a5568'; devBtnDisabled = true;
        } else if (totalTodaySeconds < DEV_PAUSE_MIN_FOCUS_SECONDS) {
            var devFaltamMin = Math.ceil((DEV_PAUSE_MIN_FOCUS_SECONDS - totalTodaySeconds) / 60);
            devLabel = 'Pausa Dev — libera com 6h de estudo no dia (faltam ' + devFaltamMin + 'min)';
            devBtnLabel = 'Bloqueada'; devBtnBg = '#4a5568'; devBtnDisabled = true;
        } else {
            devLabel = 'Pausa Dev — ilimitada (60 min cada)';
            devBtnLabel = 'Iniciar (60 min)'; devBtnBg = '#805ad5'; devBtnDisabled = false;
        }
        html += '<div style=""display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:8px;"">' +
                    '<span style=""color:#e3f2fd;font-size:10.5px;flex:1;"">' + devLabel + '</span>' +
                    '<button onclick=""adminPauseSlotClick(' + DEV_PAUSE_SLOT_INDEX + ')"" ' + (devBtnDisabled ? 'disabled' : '') + ' style=""background:' + devBtnBg + ';color:#fff;border:none;border-radius:6px;padding:6px 9px;font-size:10.5px;font-weight:700;cursor:' + (devBtnDisabled ? 'default' : 'pointer') + ';white-space:nowrap;"">' + devBtnLabel + '</button>' +
                '</div>';
    }

    mount.innerHTML = html;
}

function adminPauseSlotClick(i) {
    if (adminPauseActive && adminPauseActiveSlot === i) {
        stopTimer(); // encerra a pausa administrativa E pausa o pomodoro de verdade (mesmo caminho do botão Pausar)
    } else if (!adminPauseActive) {
        startAdminPause(i);
    }
    renderAdminPauseOverlay();
}

function startAdminPause(slotIndex) {
    if (adminPauseActive) return;
    var isDevSlot = slotIndex === DEV_PAUSE_SLOT_INDEX;
    if (!isDevSlot) {
        if (slotIndex < 0 || slotIndex >= ADMIN_PAUSE_MAX_USES) return;
        if (adminPauseSlots[slotIndex] <= 0) return;
        if (slotIndex === 1 && totalTodaySeconds < ADMIN_PAUSE_2_MIN_FOCUS_SECONDS) {
            console.log('[ADMIN_PAUSE] Pausa 2 bloqueada — só libera com 3h de foco no dia (atual: ' + Math.floor(totalTodaySeconds / 60) + 'min)');
            return;
        }
    } else if (!devModeEnabled) {
        return; // segurança extra — Pausa Dev exige Modo Desenvolvedor ativo mesmo se chamada fora do botão
    } else if (totalTodaySeconds < DEV_PAUSE_MIN_FOCUS_SECONDS) {
        console.log('[ADMIN_PAUSE] Pausa Dev bloqueada — só libera com 6h de estudo no dia (atual: ' + Math.floor(totalTodaySeconds / 60) + 'min)');
        return;
    }
    // Se havia um desvio em andamento (widget pausado), descarta sem contabilizar
    if (pauseStartTs !== null) {
        if (pauseDisplayInterval) { clearInterval(pauseDisplayInterval); pauseDisplayInterval = null; }
        var block = document.getElementById('pauseStatBlock'); if (block) block.style.display = 'none';
        pauseStartTs = null; // descartado — não soma a totalDesvioSeconds
    }
    // Descarrega qualquer tempo real já acumulado ANTES de blindar — evita que aquele
    // saldo seja salvo depois, já com a pausa administrativa ativa (pareceria ""vazamento"")
    saveTimeAcc();
    adminPauseActive      = true;
    adminPauseActiveSlot  = slotIndex;
    // Cada pausa é fixa em até 60 min. Pausa Dev sempre usa a duração cheia (é
    // ilimitada, não tem saldo pra consultar); as demais usam o que resta no slot
    // — só debita o que for realmente usado (a cada tick).
    adminPauseSecondsLeft = isDevSlot ? ADMIN_PAUSE_DURATION : adminPauseSlots[slotIndex];
    adminPauseSave();
    var badge = document.getElementById('adminPauseBadge'); if (badge) badge.style.display = '';
    var hdr   = document.getElementById('pomodoroHeader');   if (hdr)   hdr.classList.add('adminpause');
    var grade = document.getElementById('gradeMount'); if (grade) grade.style.display = 'none';
    updateHgMode(isBreakTime);
    if (!isRunning) _startTick(); // garante que o pomodoroTimer continue contando normalmente
    updateDisplay(); // atualiza timer/badge/barra na hora, sem esperar o próximo tick (até 1s de atraso)
    updateLockButton(); // bloqueia o botão Pausar na hora também
    updateAdminPauseMenuButton();
    console.log('[ADMIN_PAUSE] Pausa ' + pauseSlotLabel(slotIndex) + ' iniciada — ' + Math.floor(adminPauseSecondsLeft / 60) + ' min disponíveis nesse slot');
}

function endAdminPause(reason) {
    if (!adminPauseActive) return;
    var slotIndex = adminPauseActiveSlot;
    adminPauseActive      = false;
    adminPauseActiveSlot  = -1;
    adminPauseSecondsLeft = 0;
    adminPauseSave();
    var badge = document.getElementById('adminPauseBadge'); if (badge) badge.style.display = 'none';
    var hdr   = document.getElementById('pomodoroHeader');   if (hdr)   hdr.classList.remove('adminpause');
    var grade = document.getElementById('gradeMount'); if (grade) grade.style.display = '';
    updateHgMode(isBreakTime);
    var motivo = reason === true ? 'automaticamente (60 min)' : 'manualmente';
    console.log('[ADMIN_PAUSE] Pausa ' + pauseSlotLabel(slotIndex) + ' encerrada ' + motivo);
    updateAdminPauseMenuButton();
    // Ao encerrar por timeout (automático), mantém o foco pausado em vez
    // de retomá-lo. O stopTimer() não chama endAdminPause() de volta aqui
    // porque adminPauseActive já é false — não há recursão infinita.
    if (reason === true) stopTimer();
}

function adminPauseLoad() {
    var defaults = [];
    for (var i = 0; i < ADMIN_PAUSE_MAX_USES; i++) defaults.push(ADMIN_PAUSE_DURATION);
    try {
        var s = JSON.parse(localStorage.getItem('adminPauseState') || 'null');
        if (s && s.date === dateToday() && Array.isArray(s.slots) && s.slots.length === ADMIN_PAUSE_MAX_USES) {
            adminPauseSlots = s.slots;
        } else {
            adminPauseSlots = defaults; // novo dia — saldo renovado
        }
    } catch(e) { adminPauseSlots = defaults; }
    updateAdminPauseMenuButton();
}

function adminPauseSave() {
    try { localStorage.setItem('adminPauseState', JSON.stringify({ date: dateToday(), slots: adminPauseSlots })); } catch(e) {}
}

document.addEventListener('click', function(ev) {
    var wrap = document.getElementById('btnMinWrap');
    if (wrap && !wrap.contains(ev.target)) closeDevMinMenu();
});

var studyingTaskId        = null;
var studyingTaskName      = 'Nenhuma tarefa';
var studyingUserId        = null;
var studyingProjectId     = null;
var studyingProjectName   = '';
var studyingTempoEstimado = null;
var horaInicioDia         = null; // hora de inicio do dia util do usuario (0-23), vinda do servidor

var accumulatedSeconds    = 0;
var lastSavedSessionSec   = 0;

var pauseStartTs          = null;
var pauseDisplayInterval  = null;
var pendingDesvio         = null;
var totalDesvioSeconds    = 0; // acumulado de desvios da sessão
var _desvioBlockRef       = Math.floor(new Date().getHours() / 2) * 2; // bloco de 2h vigente para fins de reset do desvio
var _currentBlock              = Math.floor(new Date().getHours() / 2) * 2; // bloco de 2h vigente
var _meritBreakTriggeredBlock  = -1; // bloco para o qual o merit break já foi disparado
var _meritoBreakInterruption   = false; // true quando o merito-break interrompeu um foco em andamento
var _meritoBreakSavedFocoSec   = 0;    // currentTime salvo antes de entrar no merito-break

var processedSessions     = {};
var processedQErradas     = {};
var processedAnotacoes    = {};

var tempoExcedidoNotificado = false;
var alarmAudio = null;

function getAlarm() {
    if (!alarmAudio) { alarmAudio = new Audio('alert.mp3'); alarmAudio.loop = false; }
    return alarmAudio;
}

document.addEventListener('DOMContentLoaded', function() {
    var hdr = document.getElementById('pomodoroHeader');
    if (hdr) {
        hdr.addEventListener('mousedown', function(ev) {
            if (ev.target && ev.target.tagName.toLowerCase() === 'button') return;
            hostCall('StartDrag');
        });
    }
    document.querySelectorAll('button').forEach(function(b) {
        b.addEventListener('mousedown', function(e) { e.stopPropagation(); });
    });

    loadState();
    updateDisplay();
    initHourglass();
    gradeLoad();
    gradeRefresh();
    initTooltip();

    loadSettings();
    loadDevMode();
    adminPauseLoad();
    coffeeLoad();
    loadGazeSettings();
    loadCrosshairSettings();
    loadDedupeLinksSettings();

    // Primeira chamada imediata — não espera o intervalo de 15s para ter horaInicioDia
    syncCheck();

    // Se o app abriu com o timer parado (não estava rodando nem em descanso),
    // começa a contar desvio imediatamente — sem precisar clicar em Parar.
    if (!isRunning && !isBreakTime) {
        pauseStartTs = Date.now();
        startPauseDisplay();
        console.log('[DESVIO] Contador iniciado automaticamente na abertura do app');
    }

    setInterval(function() {
        // Verifica mérito a cada segundo, independente do estado do timer
        checkAndTriggerMeritBreak();

        if (!isRunning || isBreakTime || !studyingTaskId || adminPauseActive) return;
        // Se lastSavedSessionSec > currentSessionSeconds, houve reset — realinha
        if (lastSavedSessionSec > currentSessionSeconds) lastSavedSessionSec = currentSessionSeconds;
        var diff = currentSessionSeconds - lastSavedSessionSec;
        if (diff > 0) { accumulatedSeconds += diff; lastSavedSessionSec = currentSessionSeconds; }

        // Detecta virada de bloco de 2h durante o foco
        var nowBlock = Math.floor(new Date().getHours() / 2) * 2;
        if (nowBlock !== _currentBlock) {
            var prevBlock    = _currentBlock;
            _currentBlock    = nowBlock;
            _meritBreakTriggeredBlock = -1; // reseta flag para o novo bloco
            // Verifica se o bloco anterior atingiu o mérito
            var prevBlockMin = (gradeHoras[prevBlock] || 0) + (gradeHoras[prevBlock + 1] || 0) + accumulatedSeconds / 60;
            if (prevBlockMin >= 79.995) {
                checkAndTriggerMeritBreak();
            }
        }
    }, 1000);

    console.log('[INIT] Widget pronto — 10 requests via fetch()');
});

async function syncCheck() {
    try {
        var resp = await fetch(API + '/pomodoro_sync.php', { headers: authHeaders() });
        var data = await resp.json();
        if (!data.success || !data.data) return;
        var d = data.data;

        // Atualiza hora de início do dia útil sempre que o sync retornar o valor
        if (d.horaInicioDia !== null && d.horaInicioDia !== undefined) {
            horaInicioDia = d.horaInicioDia;
            checkAutoResetOverlay();
            console.log('[SYNC] horaInicioDia:', horaInicioDia);
        }

        if (d.taskIdResponderQErradas && shouldProcess(processedQErradas,  d.taskIdResponderQErradas))
            await fetchQuestoesErradas();

        if (d.taskIdAbrirAnotacoes   && shouldProcess(processedAnotacoes, d.taskIdAbrirAnotacoes))
            await fetchAnotacoes();

        if (d.sessionId              && shouldProcess(processedSessions,  d.sessionId))
            await fetchSession(d.sessionId);

        if (d.source === 'website')
            updateStudyingTask(d);
    } catch(e) { console.error('[SYNC] Erro:', e); }
}

async function fetchQuestoesErradas() {
    try {
        var resp = await fetch(API + '/pomodoro_sync.php?action=get_urls_questoes_erradas', { headers: authHeaders() });
        var data = await resp.json();
        if (data.success && data.urls) {
            var abertos = 0;
            data.urls.forEach(function(u) { if (openTrackedLink(u.url, 'Q_ERRADAS')) abertos++; });
            console.log('[Q_ERRADAS] ' + abertos + '/' + data.urls.length + ' URLs abertas');
        }
        await postAction({ action: 'clear_task_responder_q_erradas' });
    } catch(e) { console.error('[Q_ERRADAS] Erro:', e); }
}

async function fetchAnotacoes() {
    try {
        var resp = await fetch(API + '/pomodoro_sync.php?action=get_anotacoes_da_task', { headers: authHeaders() });
        var data = await resp.json();
        if (data.success && data.urls) {
            var abertos = 0;
            data.urls.forEach(function(u) { if (openTrackedLink(u.url, 'ANOTACOES')) abertos++; });
            console.log('[ANOTACOES] ' + abertos + '/' + data.urls.length + ' URLs abertas');
        }
        await postAction({ action: 'clear_task_abrir_anotacoes' });
    } catch(e) { console.error('[ANOTACOES] Erro:', e); }
}

async function fetchSession(sessionId) {
    try {
        var resp = await fetch(API + '/save_session.php?action=get_session&id=' + sessionId, {
            headers: authHeaders()
        });
        var data = await resp.json();
        if (data.success && data.urls) {
            data.urls.forEach(function(u) { hostCall('OpenLink', u.url); });
            console.log('[SESSION] ' + data.urls.length + ' URLs abertas');
        }
        await postAction({ action: 'clear_session_id' });
    } catch(e) { console.error('[SESSION] Erro:', e); }
}

async function saveEstudoRegistro(minutos) {
    if (!studyingTaskId || minutos <= 0) return;
    try {
        var resp = await fetch(API + '/pomodoro_sync.php', {
            method: 'POST',
            headers: authHeaders(),
            body: JSON.stringify({
                action:                  'save_estudo_registro',
                minutos_estudados:       parseFloat(minutos.toFixed(2)),
                task_name:               studyingTaskName,
                tarefa_id:               studyingTaskId,
                project_name:            studyingProjectName,
                data_estudo:             dateToday(),
                data_criacao_computador: dateNow()
            })
        });
        var data = await resp.json();
        if (data.success) {
            gradeAddMinutos(new Date().getHours(), minutos);
            console.log('[SAVE_ESTUDO] ' + minutos.toFixed(2) + ' min — ID:' + data.insert_id);
        } else { console.error('[SAVE_ESTUDO]', data.message); }
    } catch(e) { console.error('[SAVE_ESTUDO] fetch erro:', e); }
}

async function savePausa(duracaoMinutos, tipo, motivo) {
    if (!studyingTaskId) return;
    var fim = new Date();
    var ini = new Date(fim.getTime() - duracaoMinutos * 60000);
    try {
        var resp = await fetch(API + '/pomodoro_sync.php', {
            method: 'POST',
            headers: authHeaders(),
            body: JSON.stringify({
                action:          'save_pausa',
                tarefa_id:       studyingTaskId,
                task_name:       studyingTaskName,
                project_name:    studyingProjectName,
                data_estudo:     dateToday(),
                inicio_pausa:    fmtDateTime(ini),
                fim_pausa:       fmtDateTime(fim),
                duracao_minutos: parseFloat(duracaoMinutos.toFixed(2)),
                tipo:            tipo,
                motivo:          motivo || ''
            })
        });
        var data = await resp.json();
        if (data.success) console.log('[PAUSA] ' + tipo + ' ' + duracaoMinutos.toFixed(1) + 'min — ID:' + data.insert_id);
        else              console.error('[PAUSA]', data.message);
    } catch(e) { console.error('[PAUSA] fetch erro:', e); }
}

async function postAction(payload) {
    try {
        await fetch(API + '/pomodoro_sync.php', {
            method: 'POST',
            headers: authHeaders(),
            body: JSON.stringify(payload)
        });
    } catch(e) { console.error('[POST_ACTION]', e); }
}

async function updateTarefaSegundos(segundos) {
    if (!studyingTaskId || segundos <= 0) return;
    try {
        var resp = await fetch(API + '/tarefas.php', {
            method: 'POST',
            headers: authHeaders(),
            body: JSON.stringify({
                action:    'update_tarefa_segundos',
                id:        studyingTaskId,
                segundos:  segundos,
                timestamp: Date.now().toString()
            })
        });
        var data = await resp.json();
        console.log('[UPDATE_SEG] +' + segundos + 's tarefa ' + studyingTaskId + ' ok=' + data.success);
    } catch(e) { console.error('[UPDATE_SEG] fetch erro:', e); }
}

async function saveTimeAcc() {
    if (!studyingTaskId || accumulatedSeconds <= 0 || adminPauseActive) return;
    var minutos   = accumulatedSeconds / 60;
    if (minutos < cfg.minMinutes) return;
    var secToSave      = accumulatedSeconds;
    accumulatedSeconds = 0;
    console.log('[ACC] Salvando ' + minutos.toFixed(2) + ' min (' + secToSave + 's)');
    await Promise.all([
        saveEstudoRegistro(minutos),
        updateTarefaSegundos(secToSave)
    ]);
}

async function onAppClosing() {
    if (pauseStartTs !== null) {
        var dur = (Date.now() - pauseStartTs) / 60000;
        if (dur >= 1) await savePausa(dur, dur >= 30 ? 'desvio' : 'pausa', '');
    }
    await saveTimeAcc();
    console.log('[CLOSE] Cleanup concluído');
}

function updateStudyingTask(d) {
    if (!d.studyingTaskId) {
        if (studyingTaskId) { saveTimeAcc(); studyingTaskId = null; setTaskTitle('Pomodoro Widget'); }
        return;
    }

    // Sempre atualiza tempo_estimado independente de ser a mesma tarefa
    studyingTempoEstimado = d.tempo_estimado || null;

    if (d.studyingTaskId !== studyingTaskId) {
        if (studyingTaskId && accumulatedSeconds > 0) saveTimeAcc();
        studyingTaskId        = d.studyingTaskId;
        studyingTaskName      = d.taskName    || 'Tarefa';
        studyingUserId        = d.userId      || null;
        studyingProjectId     = d.projectId   || null;
        studyingProjectName   = d.projectName || '';
        accumulatedSeconds    = 0;
        lastSavedSessionSec   = currentSessionSeconds;
        currentSessionSeconds = 0;
        tempoExcedidoNotificado = false;
        console.log('[TASK] ' + studyingTaskName + ' (ID:' + studyingTaskId + ') tempo_estimado:' + studyingTempoEstimado);
    }

    setTaskTitle(studyingTaskName);
    updateDisplay();
}

function setTaskTitle(name) {
    var t = document.getElementById('pomodoroTaskTitle');
    var i = document.getElementById('taskInfo');
    if (t) t.textContent = name;
    if (i) {
        var tempo = studyingTempoEstimado && studyingTempoEstimado > 0
            ? ' [' + studyingTempoEstimado + ' min]'
            : '';
        i.textContent = 'Tarefa atual: ' + name + tempo;
    }
}

function toggleTimer() {
    if (isRunning && (isPauseLocked() || adminPauseActive)) return; // bloqueado
    isRunning ? stopTimer() : startTimer(); saveState();
}

// Retorna true se o bloco de 2h atual ja atingiu a condicao de estrela (>=80 min).
// Quando verdadeiro, pausas dentro desse bloco nao devem gerar desvio — o merito
// ja foi conquistado e qualquer pausa adicional e legitima ate o proximo bloco.
// IMPORTANTE: usa o valor exato (com tolerância de 0.005min só pra imprecisão de
// ponto flutuante) em vez de Math.round — arredondar pra inteiro antes de comparar
// deixava qualquer valor a partir de 79.5min contar como 80, concedendo o mérito
// com até 30s de foco a menos do que o real.
function currentBlockHasStar() {
    var blockStart = Math.floor(new Date().getHours() / 2) * 2;
    var m = (gradeHoras[blockStart] || 0) + (gradeHoras[blockStart + 1] || 0);
    return m >= 79.995;
}

function startTimer() {
    if (pauseStartTs !== null) {
        var dur = stopPauseDisplay();
        if (currentBlockHasStar()) {
            // Bloco ja tem estrela — a pausa nao conta como desvio de forma alguma
            pendingDesvio = null;
            console.log('[DESVIO] Ignorado: bloco atual ja atingiu a estrela (>=80 min)');
        } else if (dur >= 1) {
            pendingDesvio = { dur: dur };
            if (dur >= cfg.desvioMinutes) { showDesvioOverlay(dur); return; }
            sendPendingDesvio('');
        } else { pendingDesvio = null; }
    }
    _startTick();
}

function _startTick() {
    isRunning = true;
    devHalfCarry = 0;
    var btn = document.getElementById('pomodoroToggle');
    var hdr = document.getElementById('pomodoroHeader');
    if (btn) { btn.textContent = 'Pausar'; btn.className = 'btn-start running'; }
    if (hdr) { hdr.classList.add('running'); if (isBreakTime) hdr.classList.add('break'); }
    if (tickInterval) clearInterval(tickInterval);
    tickInterval = setInterval(tick, 1000);
    if (gazeEnabled && !isBreakTime) hostCall('StartGazeMonitor');
    else if (!gazeTestActive) gazeHide();
}

function stopTimer() {
    if (adminPauseActive) endAdminPause(false); // pausar manualmente encerra a pausa administrativa
    isRunning = false;
    var btn = document.getElementById('pomodoroToggle');
    var hdr = document.getElementById('pomodoroHeader');
    if (btn) { btn.textContent = 'Iniciar'; btn.className = 'btn-start'; }
    if (hdr) hdr.classList.remove('running', 'break');
    if (tickInterval) { clearInterval(tickInterval); tickInterval = null; }
    if (!isBreakTime) { pauseStartTs = Date.now(); startPauseDisplay(); }
    else { pauseStartTs = null; var b = document.getElementById('pauseStatBlock'); if (b) b.style.display = 'none'; }
    if (!gazeTestActive) hostCall('StopGazeMonitor');
    updateDisplay(); // garante que timer/badge/barra voltem ao estado real (sem cor/valor da pausa administrativa)
    saveState();
}

var devHalfCarry = 0; // acumula frações de segundo quando a contagem pela metade está ativa

// Zera totalDesvioSeconds apenas quando o relógio cruza para um novo bloco de 2h.
// Chamada tanto durante o foco (tick) quanto durante a pausa (startPauseDisplay),
// pra garantir o reset no início/fim do bloco independentemente do estado atual.
function checkDesvioBlockReset() {
    var currentBlock = Math.floor(new Date().getHours() / 2) * 2;
    if (currentBlock !== _desvioBlockRef) {
        _desvioBlockRef    = currentBlock;
        totalDesvioSeconds = 0;
        saveState();
        console.log('[DESVIO] Bloco mudou — acumulado zerado para o novo bloco (' + currentBlock + 'h)');
    }
}

function tick() {
    var advance = (devModeEnabled && devDoubleCountActive) ? 0.5 : 1;
    devHalfCarry += advance;
    if (devHalfCarry >= 1) {
        devHalfCarry -= 1;
        if (!adminPauseActive) {
            if (currentTime > 0) {
                currentTime--;
                if (!isBreakTime) {
                    currentSessionSeconds++;
                    totalTodaySeconds++;
                    checkTempoExcedido();
                }
            } else { handleTimerComplete(); }
        }
    }
    if (devModeEnabled && devDoubleCountActive) pulseHalfBadge();
    if (adminPauseActive) {
        adminPauseSecondsLeft--;
        if (adminPauseActiveSlot >= 0 && adminPauseActiveSlot !== DEV_PAUSE_SLOT_INDEX) adminPauseSlots[adminPauseActiveSlot] = Math.max(0, adminPauseSlots[adminPauseActiveSlot] - 1); // só debita o que é realmente usado (Pausa Dev é ilimitada, nunca debita)
        adminPauseSave();
        if (document.getElementById('adminPauseOverlay').style.display !== 'none') renderAdminPauseOverlay();
        if (adminPauseSecondsLeft <= 0) endAdminPause(true);
    }
    updateDisplay();
    updateLockButton();
    checkDesvioBlockReset();
}

function pulseHalfBadge() {
    var el = document.getElementById('devHalfBadge');
    if (!el) return;
    el.classList.remove('pulse');
    void el.offsetWidth; // força reflow pra poder re-disparar a animação
    el.classList.add('pulse');
}

function handleTimerComplete() {
    stopTimer();
    if (!isBreakTime) {
        // Foco acabou → zera desvio acumulado, exceto quando o limite está atrelado
        // ao bloco de 2h (nesse caso só zera na virada de bloco, via checkDesvioBlockReset)
        if (!cfg.lockPctBlock) totalDesvioSeconds = 0;
        _lastLockState     = false;
        console.log('[LOCK] Ciclo concluído — desvio ' + (cfg.lockPctBlock ? 'mantido (atrelado ao bloco)' : 'zerado') + ', botão liberado');
        // prepara break mas mostra overlay antes de iniciar
        // Se o mérito do bloco foi atingido, reduz o break ao tempo restante do bloco
        var now2         = new Date();
        var bkStart      = Math.floor(now2.getHours() / 2) * 2;
        var minsLeft2    = (bkStart + 2) * 60 - (now2.getHours() * 60 + now2.getMinutes());
        // Inclui accumulatedSeconds (ainda não salvos no gradeHoras) para não perder
        // o mérito quando o flush assíncrono ainda não aconteceu
        var blockMinLive = (gradeHoras[bkStart] || 0) + (gradeHoras[bkStart + 1] || 0) + accumulatedSeconds / 60;
        var hasMerito    = blockMinLive >= 79.995; // idem currentBlockHasStar() — sem arredondar pra inteiro
        console.log('[MERITO-CHECK] gradeH=' + (gradeHoras[bkStart]||0).toFixed(1) + ' gradeH1=' + (gradeHoras[bkStart+1]||0).toFixed(1) + ' accSec=' + accumulatedSeconds.toFixed(1) + ' blockMinLive=' + blockMinLive.toFixed(2) + ' hasMerito=' + hasMerito + ' minsLeft=' + minsLeft2.toFixed(1));
        var breakMin     = hasMerito
                           ? Math.max(1, Math.floor(minsLeft2))
                           : cfg.breakMinutes;
        isBreakTime = true; currentTime = breakMin * 60;
        // stopTimer() acima disparou o desvio pois isBreakTime ainda era false;
        // cancela agora que sabemos que é descanso — break nunca conta como desvio.
        if (pauseDisplayInterval) { clearInterval(pauseDisplayInterval); pauseDisplayInterval = null; }
        pauseStartTs = null;
        var psb = document.getElementById('pauseStatBlock'); if (psb) psb.style.display = 'none';
        var badge = document.getElementById('pomodoroSessionType');
        if (badge) {
            badge.textContent = hasMerito ? '⭐ Descanso' : 'Descanso';
            badge.className   = 'session-badge break' + (hasMerito ? ' merit' : '');
            badge.style.display = '';
        }
        // Mostra a seta ao lado do badge de descanso e atualiza a opção de mérito
        var arrow = document.getElementById('badgeDropdownArrow');
        if (arrow) { arrow.className = 'break' + (hasMerito ? ' merit' : ''); arrow.style.display = 'inline-flex'; }
        updateBadgeDropdownMerito();
        document.getElementById('pomodoroHeader').classList.add('break');
        savePausa(breakMin, 'pausa', 'descanso_pomodoro');
        updateHgMode(true); updateDisplay(); saveState();
        // Overlay + alarme
        var bEl = document.getElementById('fimFocoBreakMin');
        if (bEl) bEl.textContent = breakMin + (hasMerito && breakMin < cfg.breakMinutes ? ' (⭐ reduzido)' : '');
        // Botão de extensão de mérito: aparece se o bloco ainda não tem estrela
        // e o déficit é pequeno o suficiente para valer a pena (≤ 20 min)
        var meritoWrap = document.getElementById('btnMeritoExtendWrap');
        if (meritoWrap) {
            var blockStart   = Math.floor(new Date().getHours() / 2) * 2;
            var blockMin     = (gradeHoras[blockStart] || 0) + (gradeHoras[blockStart + 1] || 0);
            var deficit      = Math.ceil(80 - blockMin);
            var minsLeft     = (blockStart + 2) * 60 - (new Date().getHours() * 60 + new Date().getMinutes());
            var showMerito   = !currentBlockHasStar() && deficit > 0 && deficit <= 20 && minsLeft >= deficit;
            meritoWrap.style.display = showMerito ? '' : 'none';
            if (showMerito) {
                var sp = document.getElementById('meritoExtraMin');
                if (sp) sp.textContent = deficit;
                console.log('[MERITO] Botão exibido: deficit=' + deficit + 'min, minsLeft=' + minsLeft + 'min');
            }
        }
        document.getElementById('fimFocoOverlay').style.display = 'block';
        try { var a = getAlarm(); a.currentTime = 0; a.play(); } catch(e) {}
    } else {
        // Descanso acabou → prepara foco mas mostra overlay antes de iniciar
        isBreakTime = false;
        if (_meritoBreakInterruption && _meritoBreakSavedFocoSec > 0) {
            currentTime = _meritoBreakSavedFocoSec;
            console.log('[MERITO-BREAK] Foco restaurado para ' + (_meritoBreakSavedFocoSec / 60).toFixed(1) + 'min (timerRestante)');
        } else {
            currentTime = cfg.focoMinutes * 60;
        }
        _meritoBreakInterruption = false; _meritoBreakSavedFocoSec = 0;
        var badge = document.getElementById('pomodoroSessionType');
        if (badge) badge.style.display = 'none';
        var arrow = document.getElementById('badgeDropdownArrow');
        if (arrow) { arrow.style.display = 'none'; }
        hideBadgeDropdown();
        document.getElementById('pomodoroHeader').classList.remove('break');
        updateHgMode(false); updateDisplay(); saveState();
        // A partir daqui, o tempo que o usuário demorar para clicar em 'Iniciar Foco'
        // conta como desvio automaticamente — reaproveita o mesmo mecanismo de
        // pausa/desvio usado durante o foco (startTimer() fecha isso ao clicar).
        pauseStartTs = Date.now();
        startPauseDisplay();
        // Overlay + alarme
        document.getElementById('fimDescansoOverlay').style.display = 'block';
        try { var a = getAlarm(); a.currentTime = 0; a.play(); } catch(e) {}
    }
}

// Verifica se o bloco atual atingiu o mérito e, se sim, dispara o break automaticamente.
// Chamado tanto na virada de bloco quanto ao atualizar o grade (gradeAddMinutos).
// O flag _meritBreakTriggeredBlock evita duplo disparo para o mesmo bloco.
function checkAndTriggerMeritBreak() {
    if (isBreakTime) return;
    var blockStart = Math.floor(new Date().getHours() / 2) * 2;
    if (_meritBreakTriggeredBlock === blockStart) return; // já disparou para este bloco
    if (!currentBlockHasStar()) return;                   // mérito ainda não atingido
    _meritBreakTriggeredBlock = blockStart;
    var now      = new Date();
    var minsLeft = (blockStart + 2) * 60 - (now.getHours() * 60 + now.getMinutes());
    var breakMin = Math.max(1, Math.floor(minsLeft));
    if (isRunning) stopTimer();
    if (!cfg.lockPctBlock) totalDesvioSeconds = 0;
    _lastLockState = false;
    // Salva o tempo restante de foco para restaurar após o merito-break
    _meritoBreakInterruption = true;
    _meritoBreakSavedFocoSec = currentTime;
    console.log('[MERITO-BREAK] timerRestante salvo: ' + (currentTime / 60).toFixed(1) + 'min');
    isBreakTime = true; currentTime = breakMin * 60;
    if (pauseDisplayInterval) { clearInterval(pauseDisplayInterval); pauseDisplayInterval = null; }
    pauseStartTs = null;
    var psb = document.getElementById('pauseStatBlock'); if (psb) psb.style.display = 'none';
    var badge = document.getElementById('pomodoroSessionType');
    if (badge) { badge.textContent = '⭐ Descanso'; badge.className = 'session-badge break merit'; badge.style.display = ''; }
    var arrow = document.getElementById('badgeDropdownArrow');
    if (arrow) { arrow.className = 'break merit'; arrow.style.display = 'inline-flex'; }
    updateBadgeDropdownMerito();
    document.getElementById('pomodoroHeader').classList.add('break');
    savePausa(breakMin, 'pausa', 'descanso_pomodoro');
    updateHgMode(true); updateDisplay(); saveState();
    var bEl = document.getElementById('fimFocoBreakMin');
    if (bEl) bEl.textContent = breakMin + ' (⭐ mérito atingido)';
    var meritoWrap = document.getElementById('btnMeritoExtendWrap');
    if (meritoWrap) meritoWrap.style.display = 'none';
    document.getElementById('fimFocoOverlay').style.display = 'block';
    try { var a = getAlarm(); a.currentTime = 0; a.play(); } catch(e) {}
    console.log('[MERITO-BREAK] ⭐ bloco=' + blockStart + 'h break=' + breakMin + 'min');
}

function fecharFimFoco() {
    document.getElementById('fimFocoOverlay').style.display = 'none';
    document.getElementById('btnMeritoExtendWrap').style.display = 'none';
    try { if (alarmAudio) { alarmAudio.pause(); alarmAudio.currentTime = 0; } } catch(e) {}
    _startTick();
}

// Reverte o break e continua o foco pelo tempo exato para atingir o mérito do bloco.
function continuarParaMerito() {
    document.getElementById('fimFocoOverlay').style.display = 'none';
    document.getElementById('btnMeritoExtendWrap').style.display = 'none';
    try { if (alarmAudio) { alarmAudio.pause(); alarmAudio.currentTime = 0; } } catch(e) {}
    // Reverte estado para foco
    isBreakTime = false;
    var badge = document.getElementById('pomodoroSessionType');
    if (badge) badge.style.display = 'none';
    var arrow = document.getElementById('badgeDropdownArrow');
    if (arrow) arrow.style.display = 'none';
    hideBadgeDropdown();
    document.getElementById('pomodoroHeader').classList.remove('break');
    // Calcula exatamente quantos segundos faltam para os 80 min do bloco
    var blockStart  = Math.floor(new Date().getHours() / 2) * 2;
    var blockMin    = (gradeHoras[blockStart] || 0) + (gradeHoras[blockStart + 1] || 0);
    var deficit     = Math.max(1, Math.ceil(80 - blockMin));
    currentTime     = deficit * 60;
    updateHgMode(false);
    updateDisplay();
    saveState();
    _startTick();
    console.log('[MERITO] Foco estendido em ' + deficit + 'min para atingir o mérito do bloco ' + blockStart + 'h');
}

// ── Dropdown do badge de descanso ──────────────────────────────────────────
function updateBadgeDropdownMerito() {
    var blockStart = Math.floor(new Date().getHours() / 2) * 2;
    var blockMin   = (gradeHoras[blockStart] || 0) + (gradeHoras[blockStart + 1] || 0);
    var deficit    = Math.ceil(80 - blockMin);
    var minsLeft   = (blockStart + 2) * 60 - (new Date().getHours() * 60 + new Date().getMinutes());
    var show       = !currentBlockHasStar() && deficit > 0 && deficit <= 20 && minsLeft >= deficit;
    var row        = document.getElementById('bdMenuMerito');
    if (row) row.style.display = show ? '' : 'none';
    if (show) { var sp = document.getElementById('bdMeritoMin'); if (sp) sp.textContent = deficit; }
}

function toggleBadgeDropdown() {
    var menu = document.getElementById('badgeDropdownMenu');
    if (!menu) return;
    if (menu.style.display === 'none' || menu.style.display === '') {
        updateBadgeDropdownMerito();
        menu.style.display = 'block';
        // Fecha ao clicar fora
        setTimeout(function() {
            document.addEventListener('click', hideBadgeDropdownOutside, { once: true });
        }, 0);
    } else {
        hideBadgeDropdown();
    }
}

function hideBadgeDropdown() {
    var menu = document.getElementById('badgeDropdownMenu');
    if (menu) menu.style.display = 'none';
}

function hideBadgeDropdownOutside(e) {
    var wrap = document.getElementById('sessionBadgeWrap');
    if (wrap && !wrap.contains(e.target)) hideBadgeDropdown();
}

function bdMenuContinuarParaMerito() {
    hideBadgeDropdown();
    continuarParaMerito();
}

function bdMenuPularDescanso() {
    hideBadgeDropdown();
    // Encerra o break e inicia o próximo foco como se o descanso tivesse terminado
    isBreakTime = false;
    if (_meritoBreakInterruption && _meritoBreakSavedFocoSec > 0) {
        currentTime = _meritoBreakSavedFocoSec;
        console.log('[MERITO-BREAK] Foco restaurado para ' + (_meritoBreakSavedFocoSec / 60).toFixed(1) + 'min (timerRestante) [pular descanso]');
    } else {
        currentTime = cfg.focoMinutes * 60;
    }
    _meritoBreakInterruption = false; _meritoBreakSavedFocoSec = 0;
    var badge = document.getElementById('pomodoroSessionType'); if (badge) badge.style.display = 'none';
    var arrow = document.getElementById('badgeDropdownArrow');  if (arrow) arrow.style.display = 'none';
    document.getElementById('pomodoroHeader').classList.remove('break');
    updateHgMode(false); updateDisplay(); saveState(); _startTick();
    console.log('[BADGE-MENU] Descanso pulado manualmente');
}
// ───────────────────────────────────────────────────────────────────────────

function fecharFimDescanso() {
    document.getElementById('fimDescansoOverlay').style.display = 'none';
    var fdWrap = document.getElementById('fimDescansoDesvioWrap');
    if (fdWrap) fdWrap.style.display = 'none';
    try { if (alarmAudio) { alarmAudio.pause(); alarmAudio.currentTime = 0; } } catch(e) {}
    // Fecha o desvio acumulado enquanto o overlay esteve aberto (mesmo fluxo de
    // qualquer outra pausa) e então inicia o foco. Se o desvio for grande o
    // suficiente, startTimer() abre o desvioOverlay pedindo o motivo antes de
    // iniciar — o foco só começa de fato depois da confirmação nesse caso.
    startTimer();
}


function checkTempoExcedido() {
    if (!studyingTempoEstimado || tempoExcedidoNotificado || isBreakTime) return;
    var minutosAtual = currentSessionSeconds / 60;
    if (minutosAtual >= studyingTempoEstimado) {
        tempoExcedidoNotificado = true;
        showTempoExcedido(minutosAtual);
    }
}

function showTempoExcedido(minutosAtual) {
    var o = document.getElementById('tempoExcedidoOverlay');
    var m = document.getElementById('tempoExcedidoMsg');
    if (!o) return;
    if (m) m.textContent = 'Você estudou ' + Math.round(minutosAtual) + ' min na tarefa [' + studyingTaskName + '] (estimado: ' + studyingTempoEstimado + ' min).';
    o.style.display = 'block';
    try { var a = getAlarm(); a.currentTime = 0; a.play(); } catch(e) { console.warn('[ALARM]', e); }
}

function fecharTempoExcedido() {
    var o = document.getElementById('tempoExcedidoOverlay');
    if (o) o.style.display = 'none';
    try { if (alarmAudio) { alarmAudio.pause(); alarmAudio.currentTime = 0; } } catch(e) {}
}

function startPauseDisplay() {
    var block = document.getElementById('pauseStatBlock');
    if (block) block.style.display = '';
    if (pauseDisplayInterval) clearInterval(pauseDisplayInterval);
    pauseDisplayInterval = setInterval(function() {
        if (!pauseStartTs) return;
        // Detecta virada de bloco (a cada 2h) usando a referência compartilhada
        var beforeBlock = _desvioBlockRef;
        checkDesvioBlockReset();
        if (_desvioBlockRef !== beforeBlock) {
            pauseStartTs = Date.now(); // reinicia a contagem visual a partir do novo bloco
        }
        var e = Math.floor((Date.now() - pauseStartTs) / 1000);
        var m = Math.floor(e / 60), s = e % 60;
        var str = (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
        var el = document.getElementById('pauseElapsed');
        var lb = document.getElementById('pauseStatLabel');
        // Se o overlay de fim de descanso estiver aberto, espelha o desvio nele também
        var fdOverlay = document.getElementById('fimDescansoOverlay');
        if (fdOverlay && fdOverlay.style.display !== 'none') {
            var fdWrap = document.getElementById('fimDescansoDesvioWrap');
            var fdVal  = document.getElementById('fimDescansoDesvioVal');
            if (fdWrap) fdWrap.style.display = '';
            if (fdVal)  fdVal.textContent = str;
        }
        if (!el) return;
        el.textContent = str;
        if      (m >= 30) { el.style.color = '#c53030'; if (lb) { lb.textContent = '⚠ Desvio'; lb.style.color = '#c53030'; } }
        else if (m >= 10) { el.style.color = '#e53e3e'; if (lb) { lb.textContent = '⚠ Desvio'; lb.style.color = '#e53e3e'; } }
        else              { el.style.color = '#7b1fa2'; if (lb) { lb.textContent = 'Desvio'; lb.style.color = ''; } }
        // Mantém o gradeMount atualizado durante a pausa (tick nao roda,
        // entao gradeRefresh nunca seria chamada e o bloco atual ficaria desatualizado)
        gradeRefresh();
        // Barra vertical de progresso do limite de desvio
        var bar = document.getElementById('desvioBar');
        var fill = document.getElementById('desvioBarFill');
        if (bar && fill) {
            if (cfg.lockPause && !isBreakTime && !currentBlockHasStar()) {
                bar.style.display = 'block';
                var pausaAtualSec = pauseStartTs !== null ? (Date.now() - pauseStartTs) / 1000 : 0;
                var desvioTotalMin = (totalDesvioSeconds + pausaAtualSec) / 60;
                var limiteMin = getLockLimiteBaseMinutes() * (cfg.lockPct / 100);
                var pct = limiteMin > 0 ? Math.min(100, (desvioTotalMin / limiteMin) * 100) : 0;
                fill.style.height = pct + '%';
                fill.style.background = pct >= 100 ? '#e53935' : pct >= 70 ? '#ffb300' : '#4caf50';
            } else {
                bar.style.display = 'none';
            }
        }
    }, 1000);
}

function stopPauseDisplay() {
    if (pauseDisplayInterval) { clearInterval(pauseDisplayInterval); pauseDisplayInterval = null; }
    var block = document.getElementById('pauseStatBlock');
    if (block) block.style.display = 'none';
    if (!pauseStartTs) return 0;
    var dur = (Date.now() - pauseStartTs) / 60000;
    // So acumula desvio se o bloco atual ainda nao conquistou a estrela
    if (!currentBlockHasStar()) totalDesvioSeconds += (Date.now() - pauseStartTs) / 1000;
    pauseStartTs = null;
    return dur;
}

function showDesvioOverlay(dur) {
    var o = document.getElementById('desvioOverlay');
    var d = document.getElementById('desvioOverlayDur');
    var i = document.getElementById('desvioMotivoInput');
    if (!o) return;
    if (d) d.textContent = Math.round(dur) + ' min de desvio registrado';
    if (i) i.value = '';
    o.style.display = 'block';
    setTimeout(function() { if (i) i.focus(); }, 100);
}

function hideDesvioOverlay() {
    var o = document.getElementById('desvioOverlay');
    if (o) o.style.display = 'none';
}

function sendPendingDesvio(motivo) {
    if (!pendingDesvio) return;
    savePausa(pendingDesvio.dur, 'desvio', motivo);
    pendingDesvio = null;
}

function confirmDesvioMotivo() {
    var i = document.getElementById('desvioMotivoInput');
    hideDesvioOverlay();
    sendPendingDesvio(i ? i.value.trim() : '');
    _startTick();
}

function updateDisplay() {
    var str;
    if (adminPauseActive) {
        var am = Math.floor(adminPauseSecondsLeft / 60), as = adminPauseSecondsLeft % 60;
        str = am + ':' + (as < 10 ? '0' : '') + as;
    } else {
        var m = Math.floor(currentTime / 60), s = currentTime % 60;
        str = m + ':' + (s < 10 ? '0' : '') + s;
    }
    var timer = document.getElementById('pomodoroTimer');
    var mini  = document.getElementById('pomodoroTimeMini');
    var bar   = document.getElementById('pomodoroProgressBar');
    if (timer) { timer.textContent = str; timer.className = 'timer' + (adminPauseActive ? ' adminpause' : (isBreakTime ? ' break' : '')); }
    if (mini)  { mini.textContent  = str; mini.className  = 'time-mini' + (adminPauseActive ? ' adminpause' : (isBreakTime ? ' break' : '')); }
    var total = isBreakTime ? cfg.breakMinutes * 60 : cfg.focoMinutes * 60;
    if (adminPauseActive) {
        if (bar) { bar.style.width = (((ADMIN_PAUSE_DURATION - adminPauseSecondsLeft) / ADMIN_PAUSE_DURATION) * 100) + '%'; bar.className = 'progress-bar adminpause'; }
    } else {
        if (bar) { bar.style.width = (((total - currentTime) / total) * 100) + '%'; bar.className = 'progress-bar' + (isBreakTime ? ' break' : ''); }
    }
    var cs = document.getElementById('currentSession');
    var tt = document.getElementById('totalToday');
    if (cs) cs.textContent = fmtSec(currentSessionSeconds);
    if (tt) tt.textContent = fmtSec(totalTodaySeconds);
}

function fmtSec(s) {
    var h = Math.floor(s/3600), m = Math.floor((s%3600)/60), ss = s % 60;
    return (h<10?'0':'')+h+':'+(m<10?'0':'')+m+':'+(ss<10?'0':'')+ss;
}

function saveState() {
    try { localStorage.setItem('pState', JSON.stringify({ currentTime: currentTime, isBreakTime: isBreakTime, isRunning: isRunning, currentSessionSeconds: currentSessionSeconds, totalTodaySeconds: totalTodaySeconds, lastSavedSessionSec: lastSavedSessionSec, totalDesvioSeconds: totalDesvioSeconds, desvioBlockRef: _desvioBlockRef })); }
    catch(e) {}
}
function loadState() {
    try {
        var s = JSON.parse(localStorage.getItem('pState') || 'null');
        if (!s) return;
        currentTime           = s.currentTime           || cfg.focoMinutes * 60 || 3600;
        isBreakTime           = s.isBreakTime           || false;
        currentSessionSeconds = s.currentSessionSeconds || 0;
        totalTodaySeconds     = s.totalTodaySeconds     || 0;
        lastSavedSessionSec   = s.lastSavedSessionSec   || s.currentSessionSeconds || 0;
        // O desvio salvo só é restaurado se ainda pertence ao bloco de 2h vigente —
        // do contrário (app ficou fechado e o bloco virou nesse meio-tempo) começa zerado.
        var blockNow = Math.floor(new Date().getHours() / 2) * 2;
        if (s.desvioBlockRef === blockNow) {
            totalDesvioSeconds = s.totalDesvioSeconds || 0;
        } else {
            totalDesvioSeconds = 0;
        }
        _desvioBlockRef = blockNow;
        updateHgMode(isBreakTime);
        // Restaura visibilidade do badge e da seta conforme o estado salvo
        var badge = document.getElementById('pomodoroSessionType');
        var arrow = document.getElementById('badgeDropdownArrow');
        if (isBreakTime) {
            if (badge) { badge.textContent = 'Descanso'; badge.className = 'session-badge break'; badge.style.display = ''; }
            if (arrow) { arrow.className = 'break'; arrow.style.display = 'inline-flex'; }
            updateBadgeDropdownMerito();
        } else {
            if (badge) badge.style.display = 'none';
            if (arrow) arrow.style.display = 'none';
        }
        if (s.isRunning) _startTick();
    } catch(e) {}
}

var hgTop, hgBot, hgStr, hgCont, hgTs, hgFlip = false;

function initHourglass() {
    hgTop = document.getElementById('hgTopSand');
    hgBot = document.getElementById('hgBotSand');
    hgStr = document.getElementById('hgStream');
    hgCont = document.getElementById('hourglassContainer');
    if (hgTop) { hgDraw(0); setInterval(hgLoop, 50); }
}
function hgDraw(p) {
    if (!hgTop) return; p = Math.max(0, Math.min(1, p));
    var tH = 20*(1-p); hgTop.setAttribute('y',(4+20-tH).toFixed(1)); hgTop.setAttribute('height',Math.max(0,tH).toFixed(1));
    var bH = 20*p;     hgBot.setAttribute('y',(40-bH).toFixed(1));   hgBot.setAttribute('height',Math.max(0,bH).toFixed(1));
    hgStr.setAttribute('opacity',(p>0.01&&p<0.99)?'0.7':'0');
}
function hgLoop() {
    if (hgFlip) return;
    if (!hgTs) hgTs = Date.now();
    var p = Math.min(1, (Date.now()-hgTs)/15000);
    hgDraw(p);
    if (p >= 1) { hgFlip=true; hgDraw(1); hgStr.setAttribute('opacity','0'); setTimeout(function(){hgDraw(0);hgTs=null;hgFlip=false;},300); }
}
function onHourglassClick() {
    var pulse = document.getElementById('syncPulse');
    if (pulse) { pulse.className='sync-pulse'; void pulse.offsetWidth; pulse.className='sync-pulse active'; }
    if (hgCont) { hgCont.style.opacity='1'; setTimeout(function(){hgCont.style.opacity='';},1000); }
    hgDraw(0); hgTs=null; hgFlip=false;
    syncCheck();
    console.log('[SYNC] Forçado via ampulheta');
}
function updateHgMode(brk) {
    var c = adminPauseActive ? '#2196f3' : (brk ? '#4caf50' : '#7b1fa2');
    if (hgTop) hgTop.setAttribute('fill',c);
    if (hgBot) hgBot.setAttribute('fill',c);
    if (hgStr) hgStr.setAttribute('fill',c);
}

var gradeHoras = new Array(24).fill(0);

function gradeSave() {
    try { localStorage.setItem('gradeHoras', JSON.stringify({ data: dateToday(), horas: gradeHoras })); } catch(e) {}
}
function gradeLoad() {
    try {
        var s = JSON.parse(localStorage.getItem('gradeHoras') || 'null');
        if (s && s.data === dateToday()) gradeHoras = s.horas;
        else { gradeHoras = new Array(24).fill(0); gradeSave(); } // novo dia — zera
    } catch(e) {}
}

function gradeAddMinutos(h, m) { if (h>=0&&h<24&&m>0) { gradeHoras[h]+=m; gradeRefresh(); gradeSave(); checkAndTriggerMeritBreak(); } }
function gradeRefresh() {
    var mount = document.getElementById('gradeMount'); if (!mount) return;
    var hoje = new Date(), dias=['Dom','Seg','Ter','Qua','Qui','Sex','Sab'];
    var nowMin = hoje.getHours()*60+hoje.getMinutes();
    var html = '<table style=""width:100%;border-collapse:separate;border-spacing:2px;table-layout:fixed;""><tr>';
    for (var h=0;h<24;h+=2) {
        var m=(gradeHoras[h]||0)+(gradeHoras[h+1]||0);
        var cor=m===0?'#252525':m<=15?'#3d1454':m<=30?'#5c1a7a':m<=59?'#7b1fa2':m<=79?'#8e24aa':'#ab47bc';
        var cur=(h<=hoje.getHours()&&hoje.getHours()<h+2);
        var brd='none';
        if (cur) {
            var remainingInBlock = (h+2)*60 - nowMin;
            var efficiency = cfg.focoMinutes / (cfg.focoMinutes + cfg.breakMinutes);
            var additionalPot;
            var modoCalculo;
            var _dbgExtra = '';
            if (isRunning && !isBreakTime && (currentTime / 60) >= remainingInBlock) {
                // Focus cobre o bloco inteiro — sem desconto
                additionalPot = remainingInBlock;
                modoCalculo   = 'timer-cobre-bloco';
            } else if (isRunning && !isBreakTime) {
                // Focus termina antes do bloco:
                //   1. O timer atual contribui exatamente timerMin de foco
                //   2. Após o timer vem um break; se ainda couber tempo, aplica eficiência
                var timerMin       = currentTime / 60;
                var afterFocus     = remainingInBlock - timerMin;
                var timeAfterBreak = afterFocus - cfg.breakMinutes;
                var extraPot       = timeAfterBreak > 0 ? timeAfterBreak * efficiency : 0;
                additionalPot      = timerMin + extraPot;
                modoCalculo        = 'focus-parcial';
                _dbgExtra          = ' | aposTimer=' + afterFocus.toFixed(1) + 'min | aposBreak=' + timeAfterBreak.toFixed(1) + 'min | extraPot=' + extraPot.toFixed(1) + 'min';
            } else if (isRunning && isBreakTime) {
                // Em descanso: o break consome tempo do bloco antes de voltar ao foco
                var afterBreak = Math.max(0, remainingInBlock - currentTime / 60);
                additionalPot  = afterBreak * efficiency;
                modoCalculo    = 'em-descanso';
                _dbgExtra      = ' | aposBreak=' + afterBreak.toFixed(1) + 'min';
            } else {
                // Timer parado — usa a mesma lógica de focus-parcial com currentTime disponível,
                // para evitar que parado seja otimista demais ao ignorar o custo do próximo break
                var timerMinP = currentTime / 60;
                if (timerMinP >= remainingInBlock) {
                    additionalPot = remainingInBlock;
                    modoCalculo   = 'parado-cobre-bloco';
                } else {
                    var afterFocusP = remainingInBlock - timerMinP;
    var timeAfterBreakP = afterFocusP - cfg.breakMinutes;
    var extraPotP = timeAfterBreakP > 0 ? timeAfterBreakP * efficiency : 0;
    additionalPot       = timerMinP + extraPotP;
                    modoCalculo         = 'parado';
                    _dbgExtra           = ' | aposTimer=' + afterFocusP.toFixed(1) + 'min | aposBreak=' + timeAfterBreakP.toFixed(1) + 'min | extraPot=' + extraPotP.toFixed(1) + 'min';
                }
            }
            var pot = m + additionalPot;
var potArred = Math.round(pot * 10) / 10; // evita divergência entre o valor exibido (arredondado) e a comparação
brd = potArred >= 80 ? '2px solid #FFD700' : '2px solid #ce93d8';
console.log('[GRADE] bloco=' + h + 'h' +
    ' | acumulado=' + m.toFixed(1) + 'min' +
    ' | restanteBloco=' + remainingInBlock.toFixed(1) + 'min' +
    ' | modo=' + modoCalculo +
    ' | emBreak=' + isBreakTime +
    ' | timerRestante=' + (currentTime / 60).toFixed(1) + 'min' +
    ' | eficiencia=' + efficiency.toFixed(2) +
    ' (foco=' + cfg.focoMinutes + 'min break=' + cfg.breakMinutes + 'min)' +
    _dbgExtra +
    ' | potAdic=' + additionalPot.toFixed(1) + 'min' +
    ' | pot=' + potArred.toFixed(1) + 'min' +
    ' | atingivel=' + (potArred >= 80));
        }
        var lbl = m > 0 ? Math.round(m) + '' : '';
var cellContent = lbl;
if (m >= 79.995) // consistente com currentBlockHasStar() — sem arredondar pra inteiro
{
    cellContent = '<div style=""position:relative;width:100%;height:26px;display:flex;align-items:center;justify-content:center;""><svg viewBox=""0 0 24 24"" style=""position:absolute;top:0;left:0;width:100%;height:100%;""><polygon points=""12,2 15.09,8.26 22,9.27 17,14.14 18.18,21.02 12,17.77 5.82,21.02 7,14.14 2,9.27 8.91,8.26"" fill=""#FFD700"" stroke=""#cc9900"" stroke-width=""0.8""/></svg><span style=""position:relative;z-index:1;color:#3d2000;font-size:7px;font-weight:900;letter-spacing:-0.5px;line-height:1;"">' + lbl + '</span></div>';
}
var tip = dias[hoje.getDay()] + ' ' + (h < 10 ? '0' : '') + h + ':00 [' + lbl + 'min]';
html += '<td style=""background:' + cor + ';border:' + brd + ';border-radius:3px;height:26px;text-align:center;vertical-align:middle;font-size:8px;font-weight:700;color:' + (m >= 60 ? '#e0e0e0' : '#999') + ';padding:0;"" data-tip=""' + tip + '"">' + cellContent + '</td>';
    }
    html += '</tr></table>'; mount.innerHTML = html;
}

function initTooltip()
{
    var tip = null, delay = null, mx = 0, my = 0;
    function gT() { if (!tip) tip = document.getElementById('customTooltip'); return tip; }
    function sT(t) { var e = gT(); if (!e) return; e.innerHTML = t; pT(e); e.style.display = 'block'; }
    function pT(e) { var l = mx + 4; if (mx > 255) l = mx - e.offsetWidth - 4; e.style.left = l + 'px'; e.style.top = (my + 22) + 'px'; }
    function hT() { var e = gT(); if (e) e.style.display = 'none'; if (delay) { clearTimeout(delay); delay = null; } }
    function fT(el) { while (el && el.nodeType === 1) { var t = el.getAttribute && el.getAttribute('data-tip'); if (t) return t; el = el.parentNode; } return null; }
    document.body.addEventListener('mousemove', function(e){ mx = e.clientX; my = e.clientY; var el = gT(); if (el && el.style.display !== 'none') pT(el); });
    document.body.addEventListener('mouseover', function(e){ var t = fT(e.target); if (!t) { hT(); return; } if (delay) clearTimeout(delay); delay = setTimeout(function(){ sT(t); },0); });
    document.body.addEventListener('mouseout', function(){ hT(); });
}

var cfg = {
    syncInterval:  15,
    saveInterval: 30,
    minMinutes: 0.5,
    focoMinutes: 60,
    breakMinutes: 15,
    desvioMinutes: 10,
    lockPause: false,
    lockPct: 10,
    lockPctBlock: false,
    lockAlertEnabled: false
};

var _syncTimer = null;
var _saveTimer = null;

function loadSettings()
{
    try
    {
        var s = JSON.parse(localStorage.getItem('pwCfg') || 'null');
        if (s)
        {
            cfg.syncInterval = s.syncInterval || cfg.syncInterval;
            cfg.saveInterval = s.saveInterval || cfg.saveInterval;
            cfg.minMinutes = s.minMinutes || cfg.minMinutes;
            cfg.focoMinutes = s.focoMinutes || cfg.focoMinutes;
            cfg.breakMinutes = s.breakMinutes || cfg.breakMinutes;
            cfg.desvioMinutes = s.desvioMinutes || cfg.desvioMinutes;
            cfg.lockPause = s.lockPause != null ? s.lockPause : cfg.lockPause;
            cfg.lockPct = s.lockPct || cfg.lockPct;
            cfg.lockPctBlock = s.lockPctBlock != null ? s.lockPctBlock : cfg.lockPctBlock;
            cfg.lockAlertEnabled = s.lockAlertEnabled != null ? s.lockAlertEnabled : cfg.lockAlertEnabled;
        }
    }
    catch (e) { }
    applySettings();
    syncInputsFromCfg();
}

function applySettings()
{
    if (_syncTimer) clearInterval(_syncTimer);
    if (_saveTimer) clearInterval(_saveTimer);
    _syncTimer = setInterval(syncCheck, cfg.syncInterval * 1000);
    _saveTimer = setInterval(saveTimeAcc, cfg.saveInterval * 1000);
    console.log('[CFG] sync=' + cfg.syncInterval + 's save=' + cfg.saveInterval + 's minMin=' + cfg.minMinutes + ' foco=' + cfg.focoMinutes + 'min break=' + cfg.breakMinutes + 'min');
}

function setSectionVisible(id, visible)
{
    var el = document.getElementById(id);
    if (el) el.style.display = visible ? '' : 'none';
}

function syncInputsFromCfg()
{
    // Carrega token salvo no campo da sidebar
    var tokenInput = document.getElementById('cfgApiToken');
    if (tokenInput)
    {
        var saved = localStorage.getItem('api_bearer_token') || '';
        tokenInput.value = saved ? '••••••••' + saved.slice(-6) : '';
        tokenInput.placeholder = saved ? 'Token já configurado — cole para atualizar' : 'Cole o token aqui...';
    }
    var f = function(id, val) { var el = document.getElementById(id); if (el) el.value = val; }
    ;
    f('cfgSync', cfg.syncInterval);
    f('cfgSave', cfg.saveInterval);
    f('cfgMinMin', cfg.minMinutes);
    f('cfgFoco', cfg.focoMinutes);
    f('cfgBreak', cfg.breakMinutes);
    f('cfgDesvio', cfg.desvioMinutes);
    var chk = document.getElementById('cfgLockPause'); if (chk) chk.checked = cfg.lockPause;
    f('cfgLockPct', cfg.lockPct);
    var chkBlock = document.getElementById('cfgLockPctBlock'); if (chkBlock) chkBlock.checked = cfg.lockPctBlock;
    var chkAlert = document.getElementById('cfgLockAlertEnabled'); if (chkAlert) chkAlert.checked = cfg.lockAlertEnabled;
    setSectionVisible('lockPauseContent', cfg.lockPause);
    refreshLockPwdFieldsVisibility();
    updateLockButton();
    }

    function saveSettings()
    {
        var g = function(id) { return parseFloat(document.getElementById(id).value); }
        ;
        cfg.syncInterval = Math.max(5, Math.min(300, g('cfgSync') || 15));
        cfg.saveInterval = Math.max(10, Math.min(600, g('cfgSave') || 30));
        cfg.minMinutes = Math.max(0.1, Math.min(5, g('cfgMinMin') || 0.5));
        cfg.focoMinutes = Math.max(1, Math.min(120, g('cfgFoco') || 60));
        cfg.breakMinutes = Math.max(1, Math.min(60, g('cfgBreak') || 15));
        cfg.desvioMinutes = Math.max(1, Math.min(60, g('cfgDesvio') || 10));
        try { localStorage.setItem('pwCfg', JSON.stringify(cfg)); } catch (e) { }
        applySettings();
        syncInputsFromCfg();
        var fb = document.getElementById('spFeedback');
        if (fb) { fb.textContent = 'Salvo!'; setTimeout(function(){ fb.textContent = ''; }, 2000); }
        console.log('[CFG] Configuracoes salvas');
    }

    function saveLockSettings()
    {
        var chk = document.getElementById('cfgLockPause');
        var pct = document.getElementById('cfgLockPct');
        var pctBlock = document.getElementById('cfgLockPctBlock');
        var alertChk = document.getElementById('cfgLockAlertEnabled');
        // Tentando desligar um bloqueio que já estava ativo → exige senha antes de aplicar.
        if (cfg.lockPause === true && chk && chk.checked === false)
        {
            chk.checked = true; // mantém marcado visualmente até a senha ser confirmada
            showLockPwdOverlay();
            return;
        }
        cfg.lockPause = chk ? chk.checked : false;
cfg.lockPct = Math.max(1, Math.min(100, parseFloat(pct && pct.value) || 10));
if (pct) pct.value = cfg.lockPct;
cfg.lockPctBlock = pctBlock ? pctBlock.checked : false;
cfg.lockAlertEnabled = alertChk ? alertChk.checked : false;
try { localStorage.setItem('pwCfg', JSON.stringify(cfg)); } catch (e) { }
updateLockButton();
setSectionVisible('lockPauseContent', cfg.lockPause);
var fb = document.getElementById('spLockFeedback');
if (fb) { fb.textContent = cfg.lockPause ? 'Bloqueio ativo!' : 'Bloqueio desativado.'; setTimeout(function(){ fb.textContent = ''; }, 2000); }
}

// ---- Senha do Bloqueio de Pausa -----------------------------------------
// A senha nunca é guardada em texto puro: só o hash SHA-256(salt + senha) vai
// pro localStorage. Isso significa que (a) ler o valor salvo não revela a
// senha, e (b) colar o próprio hash salvo no campo de senha NÃO funciona,
// porque o hash digitado seria hasheado de novo antes da comparação — só a
// senha original reproduz o hash esperado.
function _bufToHex(buf) {
    return Array.prototype.map.call(new Uint8Array(buf), function(b) { return b.toString(16).padStart(2, '0'); }).join('');
}

function _randomSaltHex() {
    var arr = new Uint8Array(16);
    crypto.getRandomValues(arr);
    return _bufToHex(arr.buffer);
}

async function _sha256Hex(str) {
    var enc = new TextEncoder().encode(str);
    var digest = await crypto.subtle.digest('SHA-256', enc);
    return _bufToHex(digest);
}

async function setLockPassword(pwd) {
    if (!pwd) return false;
    var salt = _randomSaltHex();
    var hash = await _sha256Hex(salt + ':' + pwd);
    try { localStorage.setItem('lockPwdHash', JSON.stringify({ salt: salt, hash: hash })); } catch (e) { return false; }
    return true;
}

async function checkLockPassword(pwd) {
    var stored;
    try { stored = JSON.parse(localStorage.getItem('lockPwdHash') || 'null'); } catch (e) { stored = null; }
    if (!stored || !stored.salt || !stored.hash) return false; // nenhuma senha definida ainda
    var hash = await _sha256Hex(stored.salt + ':' + (pwd || ''));
    return hash === stored.hash;
}

function _lockPwdAlreadySet() {
    try { return !!JSON.parse(localStorage.getItem('lockPwdHash') || 'null'); } catch (e) { return false; }
}

// Mostra/esconde o campo 'Senha atual' — só é exigido quando já existe uma senha salva.
function refreshLockPwdFieldsVisibility() {
    var row = document.getElementById('cfgLockPwdCurrentRow');
    if (row) row.style.display = _lockPwdAlreadySet() ? '' : 'none';
}

async function saveLockPasswordFromUI() {
    var curInp = document.getElementById('cfgLockPwdCurrent');
    var newInp = document.getElementById('cfgLockPwdNew');
    var fb = document.getElementById('spLockPwdFeedback');
    var newPwd = newInp ? newInp.value : '';
    if (!newPwd || newPwd.length < 4) {
        if (fb) { fb.textContent = 'Use ao menos 4 caracteres.'; setTimeout(function() { fb.textContent = ''; }, 2500); }
        return;
    }
    // Já existe senha? Então trocar exige confirmar a atual primeiro — sem isso,
    // qualquer pessoa poderia simplesmente sobrescrever a senha por uma nova e
    // desativar o bloqueio livremente, o que anularia a proteção.
    if (_lockPwdAlreadySet()) {
        var okCur = await checkLockPassword(curInp ? curInp.value : '');
        if (!okCur) {
            if (fb) { fb.textContent = 'Senha atual incorreta.'; setTimeout(function() { fb.textContent = ''; }, 2500); }
            if (curInp) { curInp.value = ''; curInp.focus(); }
            return;
        }
    }
    var ok = await setLockPassword(newPwd);
    if (curInp) curInp.value = '';
    if (newInp) newInp.value = '';
    refreshLockPwdFieldsVisibility();
    if (fb) { fb.textContent = ok ? 'Senha salva!' : 'Erro ao salvar.'; setTimeout(function() { fb.textContent = ''; }, 2000); }
}

function showLockPwdOverlay() {
    var ov = document.getElementById('lockPwdOverlay');
    var inp = document.getElementById('lockPwdInput');
    var err = document.getElementById('lockPwdError');
    if (err) err.textContent = '';
    if (inp) inp.value = '';
    if (ov) ov.style.display = 'block';
    if (inp) inp.focus();
}

function cancelLockPwd() {
    var ov = document.getElementById('lockPwdOverlay');
    if (ov) ov.style.display = 'none';
}

async function confirmLockPwd() {
    var inp = document.getElementById('lockPwdInput');
    var err = document.getElementById('lockPwdError');
    var stored = null;
    try { stored = JSON.parse(localStorage.getItem('lockPwdHash') || 'null'); } catch (e) { }
    if (!stored) {
        if (err) err.textContent = 'Nenhuma senha definida ainda.';
        return;
    }
    var ok = await checkLockPassword(inp ? inp.value : '');
    if (!ok) {
        if (err) err.textContent = 'Senha incorreta.';
        if (inp) { inp.value = ''; inp.focus(); }
        return;
    }
    cancelLockPwd();
    var chk = document.getElementById('cfgLockPause');
    if (chk) chk.checked = false;
    cfg.lockPause = false;
    try { localStorage.setItem('pwCfg', JSON.stringify(cfg)); } catch (e) { }
    updateLockButton();
    setSectionVisible('lockPauseContent', false);
    fecharLockLimiteOverlay();
    _lastDesvioLockState = false;
    var fb = document.getElementById('spLockFeedback');
    if (fb) { fb.textContent = 'Bloqueio desativado.'; setTimeout(function() { fb.textContent = ''; }, 2000); }
}

// Base minutos do limite de desvio: bloco fixo de 2h (120min) quando o toggle
// 'Basear no bloco de 2h' está ativo, senão o tempo de foco configurado.
function getLockLimiteBaseMinutes()
{
    return cfg.lockPctBlock ? 120 : cfg.focoMinutes;
}

function isPauseLocked()
{
    if (!cfg.lockPause || isBreakTime || currentBlockHasStar()) return false;
    var pausaAtualSec = pauseStartTs !== null ? (Date.now() - pauseStartTs) / 1000 : 0;
    var desvioTotalMin = (totalDesvioSeconds + pausaAtualSec) / 60;
    var limiteMin = getLockLimiteBaseMinutes() * (cfg.lockPct / 100);
    return desvioTotalMin >= limiteMin;
}

var _lastLockState = false;
var _lastDesvioLockState = false; // rastreia só o bloqueio por % (sem pausa administrativa), pra disparar o alerta
function updateLockButton()
{
    var btn = document.getElementById('pomodoroToggle');
    if (!btn || !isRunning) return;
    var pausaAtualSec = pauseStartTs !== null ? (Date.now() - pauseStartTs) / 1000 : 0;
    var desvioTotalMin = (totalDesvioSeconds + pausaAtualSec) / 60;
    var limiteMin = getLockLimiteBaseMinutes() * (cfg.lockPct / 100);
    var desvioLocked = cfg.lockPause && !isBreakTime && !currentBlockHasStar() && desvioTotalMin >= limiteMin;
    var locked = desvioLocked || adminPauseActive;

    if (locked !== _lastLockState)
    {
        _lastLockState = locked;
        console.log('[LOCK] Estado mudou → ' + (locked ? 'BLOQUEADO' : 'liberado'));
    }

    // Alerta visual de 'limite de desvio ultrapassado' — dispara só na
    // transição liberado -> bloqueado do limite por %, nunca por causa da
    // pausa administrativa (que é um bloqueio de outra natureza).
    if (desvioLocked !== _lastDesvioLockState)
    {
        _lastDesvioLockState = desvioLocked;
        if (desvioLocked && cfg.lockAlertEnabled) showLockLimiteOverlay();
    }

    if (locked)
    {
        btn.classList.add('locked');
        btn.setAttribute('data-tip', adminPauseActive ? 'Pausar bloqueado: pausa administrativa ativa' : 'Pausa bloqueada: desvio acumulado atingiu ' + cfg.lockPct + '% do ' + (cfg.lockPctBlock ? 'bloco de 2h' : 'foco'));
    }
    else
    {
        btn.classList.remove('locked');
        btn.removeAttribute('data-tip');
    }
}

// Mostra o overlay de alerta assim que o desvio acumulado ultrapassa o
// limite configurado (%). Fica na tela até o usuário clicar 'Entendi' —
// não some sozinho, pra garantir que a pessoa realmente viu o aviso.
function showLockLimiteOverlay()
{
    var msg = document.getElementById('lockLimiteMsg');
    if (msg) msg.textContent = 'Desvio acumulado atingiu ' + cfg.lockPct + '% do ' + (cfg.lockPctBlock ? 'bloco de 2h (120min)' : 'tempo de foco') + '. A pausa está bloqueada até o desvio ser zerado.';
    var ov = document.getElementById('lockLimiteOverlay');
    if (ov) ov.style.display = 'block';
    console.log('[LOCK] Alerta de limite ultrapassado exibido');
}

function fecharLockLimiteOverlay()
{
    var ov = document.getElementById('lockLimiteOverlay');
    if (ov) ov.style.display = 'none';
}

var _resetConfirmDefaultMsg = 'Isso vai zerar o timer, sessão atual, total de hoje e a grade de horas. Dados já salvos no servidor não são afetados.';

function resetConfirmRestoreMsg()
{
    var el = document.getElementById('resetConfirmMsg');
    if (el) el.textContent = _resetConfirmDefaultMsg;
}

function confirmarReset()
{
    var o = document.getElementById('resetConfirmOverlay');
    if (o) o.style.display = 'block';
}

// Exibe o overlay automaticamente ao abrir o app se a hora atual >= horaInicioDia
// e ainda não foi exibido hoje. Chamado a partir do syncCheck() na primeira resposta
// que contiver horaInicioDia.
function checkAutoResetOverlay()
{
    if (horaInicioDia === null) return;
    var currentHour = new Date().getHours();
    if (currentHour < horaInicioDia) return; // ainda antes do horário de início do dia
    var today = dateToday();
    if (localStorage.getItem('lastAutoResetPrompt') === today) return; // já foi exibido hoje
    localStorage.setItem('lastAutoResetPrompt', today);
    // Personaliza o texto para o contexto automático
    var msg = document.getElementById('resetConfirmMsg');
    if (msg) msg.textContent = 'Novo dia de estudos detectado (início às ' + horaInicioDia + 'h). Deseja zerar a sessão local para começar do zero?';
    var o = document.getElementById('resetConfirmOverlay');
    if (o) o.style.display = 'block';
    console.log('[AUTO-RESET] Overlay exibido automaticamente — hora_inicio_dia=' + horaInicioDia + 'h');
}

function resetSessaoConfirmado()
{
    var o = document.getElementById('resetConfirmOverlay');
    if (o) o.style.display = 'none';
    resetSessao();
}

function resetSessao()
{
    if (isRunning) stopTimer();
    currentSessionSeconds = 0;
    totalTodaySeconds = 0;
    lastSavedSessionSec = 0;
    currentTime = cfg.focoMinutes * 60;
    isBreakTime = false;
    accumulatedSeconds = 0;
    lastSavedSessionSec = 0;
    pauseStartTs = null;
    tempoExcedidoNotificado = false;
    totalDesvioSeconds = 0;
    _desvioBlockRef = Math.floor(new Date().getHours() / 2) * 2;
    _lastLockState = false;
    gradeHoras = new Array(24).fill(0);
    _meritBreakTriggeredBlock = -1;
    gradeSave();
    stopPauseDisplay();
    updateHgMode(false);
    updateDisplay();
    gradeRefresh();
    saveState();

    // Zera saldo das pausas administrativas (restaura slots completos)
    var pauseDefaults = [];
    for (var i = 0; i < ADMIN_PAUSE_MAX_USES; i++) pauseDefaults.push(ADMIN_PAUSE_DURATION);
    adminPauseSlots = pauseDefaults;
    adminPauseSave();
    updateAdminPauseMenuButton();
    console.log('[RESET] Pausas administrativas restauradas para o padrão');

    // Zera histórico de xícaras de café
    coffeeHistory = [];
    coffeeSave();
    coffeeRenderHistory();
    console.log('[RESET] Histórico de xícaras zerado');

    var fb = document.getElementById('spFeedbackReset');
    if (fb) { fb.textContent = 'Sessão zerada!'; setTimeout(function(){ fb.textContent = ''; }, 2000); }
    console.log('[RESET] Sessão local zerada');
}

function togglePanel()
{
    var panel = document.getElementById('settingsPanel');
    var overlay = document.getElementById('settingsOverlay');
    if (!panel) return;
    var opening = !panel.classList.contains('open');
    panel.classList.toggle('open');
    overlay.classList.toggle('open');
    if (opening) syncInputsFromCfg();
}

function shouldProcess(dict, id)
{
    var COOLDOWN = 5 * 60 * 1000;
    if (dict[id] && Date.now() - dict[id] < COOLDOWN) return false;
    dict[id] = Date.now(); return true;
}

/* ═══════════════════════════════════════
   🔗 EVITAR LINKS REPETIDOS (questões/anotações)
   ═══════════════════════════════════════
   Alguns links de questões erradas e de anotações acabam se repetindo —
   inclusive casos em que o id de uma questão colide com o id de uma
   anotação — fazendo o mesmo link abrir mais de uma vez. Quando o toggle
   está ativo, guardamos um histórico (persistido em localStorage, então
   sobrevive a fechar/abrir o app) de links já abertos, mas só pelos
   próximos DEDUPE_LINKS_TTL_MS — passado esse tempo o link ""expira"" do
   histórico e pode ser aberto de novo normalmente. */
var dedupeLinksEnabled = false;
        var openedLinksHistory = { }; // url -> timestamp de quando foi aberto
        var DEDUPE_LINKS_TTL_MS = 2 * 60 * 1000; // considera ""repetido"" só dentro desses 2min

        function loadDedupeLinksSettings()
        {
            try { dedupeLinksEnabled = localStorage.getItem('dedupeLinksEnabled') === '1'; } catch (e) { }
            var chk = document.getElementById('cfgDedupeLinksEnabled');
            if (chk) chk.checked = dedupeLinksEnabled;

            try
            {
                var raw = localStorage.getItem('openedLinksHistory');
                openedLinksHistory = raw ? JSON.parse(raw) : { }
                ;
            }
            catch (e) { openedLinksHistory = { }; }
            pruneAndPersistOpenedLinksHistory(); // remove de cara qualquer entrada já expirada (ex: app ficou fechado > 2min)
            }

            function saveDedupeLinksEnabled()
            {
                var chk = document.getElementById('cfgDedupeLinksEnabled');
                dedupeLinksEnabled = chk ? chk.checked : false;
            try { localStorage.setItem('dedupeLinksEnabled', dedupeLinksEnabled ? '1' : '0'); } catch (e) { }
            var fb = document.getElementById('spDedupeLinksFeedback');
            if (fb) { fb.textContent = dedupeLinksEnabled ? 'Ativado.' : 'Desativado.'; setTimeout(function(){ fb.textContent = ''; }, 2000); }
            console.log('[LINKS] evitar repetidos ' + (dedupeLinksEnabled ? 'ativado' : 'desativado'));
        }

        // Remove do histórico qualquer link com mais de DEDUPE_LINKS_TTL_MS e
        // persiste o resultado em localStorage.
        function pruneAndPersistOpenedLinksHistory()
        {
            var now = Date.now();
            var pruned = { };
            Object.keys(openedLinksHistory).forEach(function(u) {
                if (now - openedLinksHistory[u] < DEDUPE_LINKS_TTL_MS) pruned[u] = openedLinksHistory[u];
            });
            openedLinksHistory = pruned;
            try { localStorage.setItem('openedLinksHistory', JSON.stringify(openedLinksHistory)); } catch (e) { }
        }

        // Abre um link de questão errada/anotação. Se 'Evitar Links Repetidos'
        // estiver ativo e esse MESMO link já tiver sido aberto nos últimos 2min
        // (não importa se veio da lista de questões ou de anotações), pula em vez
        // de reabrir. Retorna true se de fato abriu, false se pulou.
        function openTrackedLink(url, tag)
        {
            if (!url) return false;

            var now = Date.now();
            var lastOpened = openedLinksHistory[url];
            var isRecentDuplicate = dedupeLinksEnabled && lastOpened && (now - lastOpened) < DEDUPE_LINKS_TTL_MS;

            if (isRecentDuplicate)
            {
                console.log('[' + tag + '] pulado (link repetido nos últimos 2min): ' + url);
                return false;
            }

            hostCall('OpenLink', url);

            if (dedupeLinksEnabled)
            {
                openedLinksHistory[url] = now;
                pruneAndPersistOpenedLinksHistory();
            }
            return true;
        }
        function dateToday() { var d = new Date(); return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()); }
        function dateNow() { return dateToday() + ' ' + new Date().toTimeString().slice(0, 8); }
        function fmtDateTime(d) { return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' ' + d.toTimeString().slice(0, 8); }
        function pad(n) { return n < 10 ? '0' + n : '' + n; }

        /* ═══════════════════════════════════════
           ☕  COFFEE TIMER
           ═══════════════════════════════════════ */
        var coffeeInterval = 120; // minutos (padrão 2h)
        var coffeeSecondsLeft = 0;
        var coffeeTimer = null;
        var coffeeHistory = []; // [{time: 'HH:MM', ts: Date.now()}]
        var coffeeEnabled = true;

        function coffeeLoad()
        {
            try
            {
                var s = JSON.parse(localStorage.getItem('coffeeState') || 'null');
                var d = dateToday();
                if (s)
                {
                    coffeeInterval = s.interval || 120;
                    coffeeEnabled = s.enabled != null ? s.enabled : true;
                    var el = document.getElementById('cfgCoffee'); if (el) el.value = coffeeInterval;
                    var chk = document.getElementById('cfgCoffeeEnabled'); if (chk) chk.checked = coffeeEnabled;
                    // restaura histórico só se for hoje
                    if (s.date === d) coffeeHistory = s.history || [];
                    else coffeeHistory = [];
                    // restaura segundos restantes (pode ter mudado de dia)
                    if (s.date === d && s.secondsLeft > 0) coffeeSecondsLeft = s.secondsLeft;
                    else coffeeSecondsLeft = coffeeInterval * 60;
                    } else
                    {
                        coffeeSecondsLeft = coffeeInterval * 60;
                    }
                } catch (e) { coffeeSecondsLeft = coffeeInterval * 60; }
            setSectionVisible('coffeeContent', coffeeEnabled);
            coffeeRenderHistory();
            coffeeUpdateEmoji();
            coffeeStartTick();
        }

        function saveCoffeeEnabled()
        {
            var chk = document.getElementById('cfgCoffeeEnabled');
            coffeeEnabled = chk ? chk.checked : true;
            coffeeSave();
            setSectionVisible('coffeeContent', coffeeEnabled);
            coffeeUpdateEmoji();
            var fb = document.getElementById('spCoffeeFeedback');
            if (fb) { fb.textContent = coffeeEnabled ? 'Lembrete de café ativado.' : 'Lembrete de café desativado.'; setTimeout(function(){ fb.textContent = ''; }, 2000); }
            console.log('[COFFEE] Lembrete ' + (coffeeEnabled ? 'ativado' : 'desativado'));
        }

        function coffeeStartTick()
        {
            if (coffeeTimer) clearInterval(coffeeTimer);
            coffeeTimer = setInterval(coffeeTick, 1000);
        }

        function coffeeSave()
        {
            try
            {
                localStorage.setItem('coffeeState', JSON.stringify({
                date: dateToday(), interval: coffeeInterval,
            secondsLeft: coffeeSecondsLeft, history: coffeeHistory, enabled: coffeeEnabled
                }));
            }
            catch (e) { }
        }

        function coffeeTick()
        {
            if (!coffeeEnabled) return;
            if (coffeeSecondsLeft > 0)
            {
                coffeeSecondsLeft--;
                coffeeSave();
            }
            else
            {
                // Hora do café! toca alarme e ativa o emoji
                coffeeAlert();
            }
            coffeeUpdateEmoji();
        }

        function coffeeAlert()
        {
            try { var a = getAlarm(); a.currentTime = 0; a.play(); } catch (e) { }
            // Pisca o emoji
            var em = document.getElementById('coffeeEmoji');
            if (em) em.classList.add('ready');
        }

        function coffeeUpdateEmoji()
        {
            var em = document.getElementById('coffeeEmoji');
            if (!em) return;
            if (!coffeeEnabled)
            {
                em.style.display = 'none';
                em.classList.remove('ready');
                return;
            }
            if (coffeeSecondsLeft <= 0)
            {
                em.style.display = '';
                em.classList.add('ready');
            }
            else
            {
                em.style.display = 'none';
                em.classList.remove('ready');
            }
        }

        function coffeeConfirm()
        {
            // Para o alarme se estiver tocando
            try { if (alarmAudio) { alarmAudio.pause(); alarmAudio.currentTime = 0; } } catch (e) { }
            // Registra no histórico local
            var now = new Date();
            var timeStr = pad(now.getHours()) + ':' + pad(now.getMinutes());
            coffeeHistory.unshift({ time: timeStr, ts: Date.now() });
            if (coffeeHistory.length > 20) coffeeHistory.pop(); // máx 20 entradas
                                                                // Zera o timer
            coffeeSecondsLeft = coffeeInterval * 60;
            var em = document.getElementById('coffeeEmoji');
            if (em) em.classList.remove('ready');
            coffeeSave();
            coffeeRenderHistory();
            coffeeUpdateEmoji();
            // Feedback visual
            if (em)
            {
                em.style.transform = 'scale(1.5)';
                setTimeout(function(){ em.style.transform = ''; }, 400);
            }
        }

        function coffeeClearHistory()
        {
            coffeeHistory = [];
            coffeeSave();
            coffeeRenderHistory();
            var fb = document.getElementById('spCoffeeFeedback');
            if (fb) { fb.textContent = 'Histórico limpo!'; setTimeout(function(){ fb.textContent = ''; }, 2000); }
            console.log('[COFFEE] Histórico de xícaras limpo');
        }

        function coffeeRenderHistory()
        {
            var container = document.getElementById('coffeeHistory');
            if (!container) return;
            if (!coffeeHistory.length)
            {
                container.innerHTML = '<div class=""sp-history-empty"">Nenhuma xícara ainda hoje ☕</div>';
                return;
            }
            var html = '';
            coffeeHistory.forEach(function(item, i) {
                var label = i === 0 ? '(última)' : '';
                html += '<div class=""sp-history-item""><span><span class=""sp-history-time"">' + item.time + '</span> xícara ' + (coffeeHistory.length - i) + ' ' + label + '</span><span class=""sp-history-remove"" onclick=""coffeeRemoveEntry(' + i + ')"" title=""Remover registro"">✕</span></div>';
            });
            container.innerHTML = html;
        }

        function coffeeRemoveEntry(index)
        {
            if (index < 0 || index >= coffeeHistory.length) return;
            coffeeHistory.splice(index, 1);
            coffeeSave();
            coffeeRenderHistory();
            var fb = document.getElementById('spCoffeeFeedback');
            if (fb) { fb.textContent = 'Registro removido.'; setTimeout(function(){ fb.textContent = ''; }, 2000); }
            console.log('[COFFEE] Registro de xícara removido, índice ' + index);
        }

        function saveCoffeeSettings()
        {
            var el = document.getElementById('cfgCoffee');
            if (!el) return;
            var val = parseInt(el.value) || 120;
            coffeeInterval = Math.max(1, Math.min(480, val));
            el.value = coffeeInterval;
            coffeeSecondsLeft = coffeeInterval * 60;
            coffeeSave();
            coffeeUpdateEmoji();
            var fb = document.getElementById('spCoffeeFeedback');
            if (fb) { fb.textContent = 'Salvo! Timer reiniciado.'; setTimeout(function(){ fb.textContent = ''; }, 2000); }
        }

        /* ═══════════════════════════════════════
           👁  MONITORAMENTO DE FOCO OCULAR
           ═══════════════════════════════════════ */
        var gazeEnabled = false; // opt-in: desligado por padrão (usa webcam)
        var gazeTestActive = false; // modo de teste manual: ignora estado do pomodoro

        function loadGazeSettings()
        {
            try { gazeEnabled = localStorage.getItem('gazeEnabled') === '1'; } catch (e) { }
            var chk = document.getElementById('cfgGazeEnabled');
            if (chk) chk.checked = gazeEnabled;
            // Se o timer já estiver rodando (ex: reload de estado) e o monitor
            // estiver habilitado, garante que o processo seja iniciado.
            if (gazeEnabled && isRunning && !isBreakTime) hostCall('StartGazeMonitor');

            // Teste manual (id cfgGazeTestEnabled) — persistido, pra não precisar
            // reativar toda vez que o app abre. Se estava ligado na última sessão,
            // já sobe a câmera de novo aqui.
            try { gazeTestActive = localStorage.getItem('gazeTestActive') === '1'; } catch (e) { }
            var chkTest = document.getElementById('cfgGazeTestEnabled');
            if (chkTest) chkTest.checked = gazeTestActive;
            if (gazeTestActive) hostCall('StartGazeMonitor');
            }

            function saveGazeEnabled()
            {
                var chk = document.getElementById('cfgGazeEnabled');
                gazeEnabled = chk ? chk.checked : false;
            try { localStorage.setItem('gazeEnabled', gazeEnabled ? '1' : '0'); } catch (e) { }
            var fb = document.getElementById('spGazeFeedback');
            if (gazeEnabled)
            {
                if (fb) { fb.textContent = 'Ativado! Câmera liga durante o foco.'; setTimeout(function(){ fb.textContent = ''; }, 2500); }
                if (isRunning && !isBreakTime) hostCall('StartGazeMonitor');
            }
            else
            {
                if (fb) { fb.textContent = 'Desativado.'; setTimeout(function(){ fb.textContent = ''; }, 2000); }
                // Não derruba o processo se um teste manual estiver em andamento —
                // o usuário pode estar testando de propósito com o toggle desligado.
                if (!gazeTestActive) { hostCall('StopGazeMonitor'); gazeHide(); }
            }
            console.log('[GAZE] Monitoramento ' + (gazeEnabled ? 'ativado' : 'desativado'));
            }

            // Liga/desliga o monitoramento manualmente, independente do pomodoro estar
            // rodando ou não — só para testes rápidos (ex: checar calibração, ver se a
            // webcam abre certo) sem precisar iniciar um ciclo de foco de verdade.
            // Persistido em localStorage — fica ligado entre aberturas do app até você
            // desligar de propósito.
            function saveGazeTestEnabled()
            {
                var chk = document.getElementById('cfgGazeTestEnabled');
                gazeTestActive = chk ? chk.checked : false;
            try { localStorage.setItem('gazeTestActive', gazeTestActive ? '1' : '0'); } catch (e) { }
            var fb = document.getElementById('spGazeTestFeedback');
            if (gazeTestActive)
            {
                hostCall('StartGazeMonitor');
                if (fb) { fb.textContent = 'Teste ativo — câmera ligada mesmo sem pomodoro rodando.'; }
                console.log('[GAZE] Teste manual iniciado (independente do pomodoro)');
            }
            else
            {
                // Só derruba de fato o processo se o pomodoro também não estiver
                // precisando dele nesse momento (ex: você ativou o teste enquanto
                // o foco de verdade já estava rodando com o monitoramento ligado).
                var pomodoroPrecisa = gazeEnabled && isRunning && !isBreakTime;
                if (!pomodoroPrecisa) { hostCall('StopGazeMonitor'); gazeHide(); }
                if (fb) { fb.textContent = ''; }
                console.log('[GAZE] Teste manual encerrado');
            }
        }

        function gazeHide()
        {
            var b = document.getElementById('gazeBlock');
            if (b) b.style.display = 'none';
            // Sem monitoramento ativo não há status pra avaliar — some com o crosshair.
            crosshairReset();
        }

        // Abre/fecha a janela de debug da câmera (gaze_monitor.py --show-window).
        // Independente do pomodoro/teste estarem ligados — se o monitoramento não
        // estiver rodando ainda, o C# inicia o processo já com a janela visível.
        // Fecha reiniciando o processo sem a flag --show-window (o monitoramento
        // em si, se ainda for necessário, continua headless normalmente).
        var gazeCameraWindowOpen = false;

        function toggleBpmOverlay(ev)
        {
            if (ev) ev.stopPropagation();
            closeDevMinMenu();
            hostCall('ToggleBpmOverlay');
        }

        function toggleGazeCameraWindow()
        {
            gazeCameraWindowOpen = !gazeCameraWindowOpen;
            var btn = document.getElementById('gazeCameraBtn');
            if (gazeCameraWindowOpen)
            {
                hostCall('ShowGazeCameraWindow');
                if (btn) { btn.classList.add('active'); btn.title = 'Fechar janela da câmera'; }
                console.log('[GAZE] Janela de debug da câmera aberta');
            }
            else
            {
                hostCall('HideGazeCameraWindow');
                if (btn) { btn.classList.remove('active'); btn.title = 'Abrir janela da câmera (debug)'; }
                console.log('[GAZE] Janela de debug da câmera fechada');
            }
        }

        // Chamado pelo C# (Form1) a cada atualização vinda do processo Python.
        // pct: 0-100 (média crua dos últimos windowSec), windowSec: janela em
        // segundos, focused/faceDetected: bool, intervalPct: 0-100 (fração real
        // de frames com rosto detectado NO INTERVALO ATUAL de 2s — é o número
        // comparado contra o limiar de 20% pra decidir current_focused; -1 se
        // indisponível).


        function updateGazeStatus(pct, windowSec, focused, faceDetected, intervalPct, direction)
        {
            var block = document.getElementById('gazeBlock');
            var pctEl = document.getElementById('gazePct');
            var fill = document.getElementById('gazeBarFill');
            var sub = document.getElementById('gazeSub');
            if (!block || !pctEl || !fill || !sub) return;

            block.style.display = 'block';
            pctEl.textContent = pct + '%';
            fill.style.width = pct + '%';
            fill.style.background = pct >= 70 ? '#4caf50' : (pct >= 40 ? '#ffb300' : '#e53935');

            var janela = windowSec >= 60 ? Math.round(windowSec / 60) + ' min' : windowSec + 's';

            // Mesmo critério de rótulo do GazeCameraForm.updateCameraStatus — baseado
            // direto em ""direction"" (o resultado do ÚLTIMO frame processado pelo
            // dlib), não numa agregação do intervalo. Mais rápido de refletir
            // mudança porque não espera acumular vários frames pra decidir o texto.
            var statusLabel;
            if (!faceDetected) statusLabel = 'Rosto não detectado';
            else if (direction === 'blinking') statusLabel = 'Piscando';
            else if (direction === 'right') statusLabel = 'Olhando direita';
            else if (direction === 'left') statusLabel = 'Olhando esquerda';
            else if (direction === 'center') statusLabel = 'Olhando centro';
            else statusLabel = 'Indefinido';

            // Avalia o crosshair de atenção com base no status 'cru' (sem o sufixo
            // '- Foco: SIM/NÃO'), que é exatamente o texto usado no gatilho.
            crosshairEvaluate(statusLabel);

            statusLabel += ' - Foco: ' + (focused ? 'SIM' : 'NÃO');

            sub.textContent = statusLabel + ' · últimos ' + janela;
            console.debug('[GAZE] current_focused=' + focused + ' direction=' + direction +
                ' interval_pct=' + intervalPct + '% face_detected(último frame)=' + faceDetected);
        }
        /* ═══════════════════════════════════════
           🖥️ CROSSHAIR EM TELA CHEIA (Windows)
           ═══════════════════════════════════════
           Mostra duas linhas (vertical + horizontal) cruzando o centro do monitor
           (via janela nativa do Windows, não dentro do widget) quando o status do
           foco ocular indica atenção desviada — 'Rosto não detectado', 'Olhando
           direita', 'Olhando esquerda' ou 'Indefinido' — mas só depois de o status
           permanecer NESSE GRUPO por 2s seguidos (evita 'piscar' a mira por trocas
           rápidas entre esses status, ex: direita -> indefinido -> direita).
           Assim que o status sair do grupo (ex: 'Olhando centro', 'Piscando' ou
           foco perdido é reconquistado), o crosshair some imediatamente, sem
           debounce de saída. */
        var crosshairEnabled = false;
        var crosshairTriggerStatuses = ['Rosto não detectado', 'Olhando direita', 'Olhando esquerda', 'Indefinido'];
        var crosshairTimer = null;   // handle do setTimeout de 2s pendente
        var crosshairInBadSet = false; // true enquanto o status atual está no grupo acima

        function loadCrosshairSettings()
        {
            try { crosshairEnabled = localStorage.getItem('crosshairFullscreenEnabled') === '1'; } catch (e) { }
            var chk = document.getElementById('cfgCrosshairFullscreenEnabled');
            if (chk) chk.checked = crosshairEnabled;
            }

            function saveCrosshairFullscreenEnabled()
            {
                var chk = document.getElementById('cfgCrosshairFullscreenEnabled');
                crosshairEnabled = chk ? chk.checked : false;
            try { localStorage.setItem('crosshairFullscreenEnabled', crosshairEnabled ? '1' : '0'); } catch (e) { }
            var fb = document.getElementById('spCrosshairFullscreenFeedback');
            if (fb) { fb.textContent = crosshairEnabled ? 'Ativado.' : 'Desativado.'; setTimeout(function(){ fb.textContent = ''; }, 2000); }
            if (!crosshairEnabled) crosshairReset();
            console.log('[CROSSHAIR] ' + (crosshairEnabled ? 'ativado' : 'desativado'));
        }

        function crosshairShow()
        {
            hostCall('ShowFullscreenCrosshair');
        }

        function crosshairHide()
        {
            hostCall('HideFullscreenCrosshair');
        }

        // Zera qualquer contagem/timer pendente e esconde a mira imediatamente.
        function crosshairReset()
        {
            if (crosshairTimer) { clearTimeout(crosshairTimer); crosshairTimer = null; }
            crosshairInBadSet = false;
            crosshairHide();
        }

        // Chamado a cada atualização de status vindo do gaze monitor (updateGazeStatus)
        // com o statusLabel 'cru' (sem o sufixo '- Foco: ...').
        function crosshairEvaluate(rawStatus)
        {
            if (!crosshairEnabled) return;

            var isTrigger = crosshairTriggerStatuses.indexOf(rawStatus) !== -1;

            if (!isTrigger)
            {
                // Saiu do grupo de status que ativam o crosshair — some na hora,
                // cancela qualquer contagem de 2s que estivesse rolando.
                crosshairReset();
                return;
            }

            if (crosshairInBadSet)
            {
                // Já estava no grupo (seja com o timer rodando, seja já visível) —
                // nada a fazer, deixa a contagem/estado em andamento.
                return;
            }

            // Entrou agora no grupo de status 'ruins' — inicia a contagem de 2s.
            crosshairInBadSet = true;
            if (crosshairTimer) clearTimeout(crosshairTimer);
            crosshairTimer = setTimeout(function() {
                crosshairTimer = null;
                if (crosshairInBadSet) crosshairShow();
            }, 2000);
        }

        // Chamado pelo C# se o processo Python falhar ao iniciar (ex: Python/venv
        // não encontrado), para dar feedback visual em vez de falhar silenciosamente.
        function onGazeMonitorError(message)
        {
            var sub = document.getElementById('gazeSub');
            var block = document.getElementById('gazeBlock');
            if (block) block.style.display = 'block';
            if (sub) sub.textContent = 'Erro: ' + message;
            console.error('[GAZE] ' + message);
        }

        // Chamado pelo C# quando o processo Python caiu sozinho (crash) e uma
        // tentativa automática de reconexão está em andamento — evita que a UI
        // fique ""congelada"" mostrando o último status como se ainda fosse válido.
        function onGazeReconnecting(attempt, maxAttempts)
        {
            var sub = document.getElementById('gazeSub');
            var block = document.getElementById('gazeBlock');
            if (block) block.style.display = 'block';
            if (sub) sub.textContent = 'Reconectando... (tentativa ' + attempt + '/' + maxAttempts + ')';
            console.warn('[GAZE] Reconectando, tentativa ' + attempt + '/' + maxAttempts);
        }
";
    }
}

namespace PomodoroWidget
{
    // ══════════════════════════════════════════════════════════════════
    //  ➕ CrosshairOverlayForm — janela nativa transparente cobrindo toda
    //  a área virtual do Windows (todos os monitores), usada pelo toggle
    //  "Crosshair em Tela Cheia". Desenha duas linhas (vertical + horizontal)
    //  cruzando o centro do monitor PRIMÁRIO. É "click-through" (WS_EX_TRANSPARENT)
    //  — nunca intercepta cliques/mouse do usuário — e não aparece na barra
    //  de tarefas nem no Alt+Tab (WS_EX_TOOLWINDOW).
    // ══════════════════════════════════════════════════════════════════
    public class CrosshairOverlayForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private static readonly Color KeyColor = Color.FromArgb(1, 1, 1); // cor "chroma key" quase preta, improvável de colidir com o desenho
        private readonly Pen _pen = new Pen(Color.FromArgb(220, 255, 60, 60), 1f);

        public CrosshairOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = KeyColor;
            TransparencyKey = KeyColor;
            Cursor = Cursors.Default;

            // Cobre a área virtual inteira (todos os monitores conectados),
            // já que SystemInformation.VirtualScreen já considera monitores
            // posicionados à esquerda/acima do primário (coordenadas negativas).
            Bounds = SystemInformation.VirtualScreen;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        // Evita que a janela "roube" o foco/ative ao ser exibida (Show()),
        // já que é só um overlay visual por cima de tudo — o usuário deve
        // continuar interagindo normalmente com o que estiver por baixo.
        protected override bool ShowWithoutActivation => true;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // Centro do monitor PRIMÁRIO, convertido para coordenadas relativas
            // a esta janela (que começa em SystemInformation.VirtualScreen.Location,
            // podendo ser negativa se houver monitor à esquerda/acima do primário).
            var primary = Screen.PrimaryScreen.Bounds;
            int cx = (primary.Left - Left) + primary.Width / 2;
            int cy = (primary.Top - Top) + primary.Height / 2;

            e.Graphics.DrawLine(_pen, cx, 0, cx, Height);
            e.Graphics.DrawLine(_pen, 0, cy, Width, cy);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _pen.Dispose();
            base.Dispose(disposing);
        }
    }
}