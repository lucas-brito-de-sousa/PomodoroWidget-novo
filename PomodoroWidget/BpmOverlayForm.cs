using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PomodoroWidget
{
    // ── Overlay de BPM (WinForms + WebView2) ─────────────────────────────
    // Janela pequena, sem borda, sempre no topo, arrastável — igual um
    // overlay de FPS. Mostra só o número de BPM vindo do bpm_daemon.py
    // (processo Python gerenciado pelo Form1, mesmo padrão do gaze).
    public class BpmOverlayForm : Form
    {
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        private WebView2 webView;
        private bool _webViewReady = false;

        public BpmOverlayForm()
        {
            Text = "BPM";
            Width = 160;
            Height = 90;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(
                Screen.PrimaryScreen.WorkingArea.Width - Width - 20,
                20);
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(20, 20, 20);
            DoubleBuffered = true;

            webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(webView);

            Load += async (s, e) =>
            {
                SetRoundRegion(16);
                try { await SetupWebView(); }
                catch (Exception ex) { MessageBox.Show("Falha ao iniciar o overlay de BPM: " + ex.Message); }
            };
            Resize += (s, e) => SetRoundRegion(16);
        }

        private void SetRoundRegion(int r)
        {
            IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r);
            SetWindowRgn(Handle, rgn, true);
            DeleteObject(rgn);
        }

        private async Task SetupWebView()
        {
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PomodoroWidget");
            string userDataFolder = Path.Combine(baseDir, "WebView2Data_Bpm");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await webView.EnsureCoreWebView2Async(env);

            var s = webView.CoreWebView2.Settings;
            s.AreDevToolsEnabled = true;
            s.IsScriptEnabled = true;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDefaultScriptDialogsEnabled = true;
            s.IsStatusBarEnabled = false;
            s.AreHostObjectsAllowed = true;

            webView.CoreWebView2.AddHostObjectToScript("bpmOverlay", new BpmOverlayHostObject(this));
            webView.NavigationCompleted += (o, e) => { _webViewReady = true; };

            string tempDir = Path.Combine(Path.GetTempPath(), "PomodoroWidget_Bpm");
            Directory.CreateDirectory(tempDir);
            string htmlFile = Path.Combine(tempDir, "bpm.html");
            File.WriteAllText(htmlFile, BuildBpmHtml(), Encoding.UTF8);

            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "bpmoverlay.local",
                tempDir,
                CoreWebView2HostResourceAccessKind.Allow);

            webView.CoreWebView2.Navigate("https://bpmoverlay.local/bpm.html");
        }

        // ── Chamado pelo Form1 quando chega uma nova leitura do bpm_daemon ──
        public void UpdateBpm(double? value, bool stable)
        {
            if (!_webViewReady) return;
            string valueJs = value.HasValue
                ? value.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            string js = "updateBpm(" + valueJs + "," + (stable ? "true" : "false") + ");";
            _ = webView.ExecuteScriptAsync(js);
        }

        public void ShowError(string message)
        {
            if (!_webViewReady) return;
            string js = "showBpmError(" + Form1.JsString(message) + ");";
            _ = webView.ExecuteScriptAsync(js);
        }

        private string BuildBpmHtml()
        {
            return @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<style>
  html, body { margin:0; padding:0; background:transparent; overflow:hidden; }
  body {
    font-family: 'Segoe UI', sans-serif; color:#e0e0e0;
    display:flex; flex-direction:column; align-items:center; justify-content:center;
    height:100vh; -webkit-user-select:none; user-select:none; cursor:move;
  }
  #bpmValue { font-size:34px; font-weight:700; line-height:1; color:#4caf50; transition:color .2s; }
  #bpmValue.waiting { color:#7f848e; }
  #bpmLabel { font-size:10px; letter-spacing:1px; color:#7f848e; text-transform:uppercase; margin-top:2px; }
  #closeBtn {
    position:absolute; top:4px; right:6px; font-size:12px; color:#7f848e;
    cursor:pointer; -webkit-user-select:none; user-select:none;
  }
  #closeBtn:hover { color:#e53935; }
</style>
</head>
<body onmousedown='onBodyMouseDown(event)'>
  <div id='closeBtn' onclick='onClose()' title='Fechar overlay'>✕</div>
  <div id='bpmValue' class='waiting'>--.--</div>
  <div id='bpmLabel'>BPM</div>

<script>
function onBodyMouseDown(e) {
    // Ignora clique no botão de fechar; qualquer outro ponto arrasta a janela.
    if (e.target && e.target.id === 'closeBtn') return;
    hostCall('StartDrag');
}

function onClose() {
    hostCall('CloseOverlay');
}

function updateBpm(value, stable) {
    var el = document.getElementById('bpmValue');
    if (!el) return;
    if (value === null) {
        el.textContent = '--.--';
        el.classList.add('waiting');
        return;
    }
    el.textContent = value.toFixed(2);
    el.classList.toggle('waiting', !stable);
}

function showBpmError(message) {
    var el = document.getElementById('bpmValue');
    var lbl = document.getElementById('bpmLabel');
    if (el) { el.textContent = '--.--'; el.classList.add('waiting'); }
    if (lbl) lbl.textContent = 'ERRO';
    console.error('[BPM] ' + message);
}

function hostCall(method) {
    var args = Array.prototype.slice.call(arguments, 1);
    try { var h = window.chrome.webview.hostObjects.sync.bpmOverlay; h[method].apply(h, args); }
    catch (e) { console.error('[hostCall bpmOverlay] ' + method + ':', e); }
}
</script>
</body>
</html>";
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.AutoDual)]
        public class BpmOverlayHostObject
        {
            private readonly BpmOverlayForm f;
            public BpmOverlayHostObject(BpmOverlayForm form) { f = form; }

            // Mesmo truque de arraste sem borda usado no Form1 principal:
            // solta a captura do mouse e manda o Windows tratar como se
            // tivesse clicado na barra de título (mesmo sem barra visível).
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
                        catch { }
                    }));
                }
                catch { }
            }

            public void CloseOverlay() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.Close(); }
                catch { }
            }));
        }
    }
}