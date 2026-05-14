using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

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

        public Form1()
        {
            InitializeComponent();
            appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PomodoroWidget");
            Directory.CreateDirectory(appDataPath);
            SetupForm();
            SetupWebBrowser();
            Log("Widget iniciado — WebView2 + fetch()");
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

        private async void SetupWebBrowser()
        {
            await webBrowser1.EnsureCoreWebView2Async(null);

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

            public void StartDrag() => f.Invoke(new Action(() =>
            {
                try
                {
                    if (!f.IsHandleCreated || f.IsDisposed) return;
                    ReleaseCapture();
                    SendMessage(f.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                }
                catch (Exception ex) { f.Log("StartDrag erro: " + ex.Message); }
            }));

            public void MinimizeWidget() => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.DoMinimize(); }
                catch (Exception ex) { f.Log("MinimizeWidget erro: " + ex.Message); }
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (isWebBrowserReady)
                _ = webBrowser1.ExecuteScriptAsync("onAppClosing()");
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
      <button class='btn-min'   id='pomodoroMinimize' onclick='hostCall(""MinimizeWidget"")'>▼</button>
      <button class='btn-start' id='pomodoroToggle'   onclick='toggleTimer()'>Iniciar</button>
      <button class='btn-close' id='pomodoroClose'    onclick='hostCall(""CloseApp"")'>✕</button>
    </div>
  </div>
  <div class='content' id='pomodoroContent'>
    <div class='timer-row'>
      <div class='timer' id='pomodoroTimer'>60:00</div>
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
      <span class='session-badge' id='pomodoroSessionType' style='display:none'>Descanso</span>
    </div>
    <div id='gradeMount'></div>
    <div class='progress'><div class='progress-bar' id='pomodoroProgressBar' style='width:0%'></div></div>
    <div class='stats'>
      <div class='stat'><div class='lbl'>Sessão Atual</div><div class='val' id='currentSession'>00:00:00</div></div>
      <div class='stat' id='pauseStatBlock' style='display:none'><div class='lbl' id='pauseStatLabel'>Desvio</div><div class='val' id='pauseElapsed' style='color:#7b1fa2'>00:00</div></div>
      <div class='stat'><div class='lbl'>Total Hoje</div><div class='val' id='totalToday'>00:00:00</div></div>
    </div>
    <div class='task-info' id='taskInfo'>Tarefa atual: Nenhuma</div>
  </div>
</div>

<!-- Painel lateral de configurações -->
<div id='settingsPanel' class='settings-panel'>
  <div class='sp-header'>
    <span>⚙ Configurações</span>
    <button class='sp-close' onclick='togglePanel()'>✕</button>
  </div>
  <div class='sp-body'>
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
    <div class='sp-section' style='margin-top:14px;'>Temporizador</div>
    <div class='sp-row'>
      <label class='sp-label' for='cfgFoco'>Foco <span class='sp-unit'>(min)</span></label>
      <input class='sp-input' id='cfgFoco' type='number' min='1' max='120' step='1' value='60'/>
    </div>
    <div class='sp-row'>
      <label class='sp-label' for='cfgBreak'>Descanso <span class='sp-unit'>(min)</span></label>
      <input class='sp-input' id='cfgBreak' type='number' min='1' max='60' step='1' value='15'/>
    </div>
    <button class='sp-save' onclick='saveSettings()'>Salvar</button>
    <div class='sp-feedback' id='spFeedback'></div>
    <div class='sp-section' style='margin-top:14px;'>Sessão</div>
    <button class='sp-reset' onclick='confirmarReset()'>↺ Zerar sessão local</button>
    <div class='sp-feedback' id='spFeedbackReset'></div>
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
    <button onclick='fecharFimFoco()' style='width:100%;background:#388e3c;color:#fff;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Iniciar Descanso</button>
  </div>
</div>

<!-- Overlay fim de descanso -->
<div id='fimDescansoOverlay' style='display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:9999;'>
  <div style='position:absolute;top:50%;left:50%;width:300px;margin:-100px 0 0 -150px;background:#1a1a2e;border:2px solid #7b1fa2;border-radius:10px;padding:16px 18px;'>
    <div style='color:#ce93d8;font-size:13px;font-weight:700;margin-bottom:6px;'>✅ DESCANSO CONCLUÍDO!</div>
    <div style='color:#e0c8f0;font-size:11px;margin-bottom:14px;'>Descansou bem? Hora de voltar ao foco!</div>
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

<!-- Overlay confirmação reset sessão -->
<div id='resetConfirmOverlay' style='display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:9999;'>
  <div style='position:absolute;top:50%;left:50%;width:300px;margin:-90px 0 0 -150px;background:#1a1a2e;border:2px solid #718096;border-radius:10px;padding:16px 18px;'>
    <div style='color:#e2e8f0;font-size:12px;font-weight:700;margin-bottom:6px;'>↺ ZERAR SESSÃO LOCAL</div>
    <div style='color:#a0aec0;font-size:11px;margin-bottom:14px;'>Isso vai zerar o timer, sessão atual, total de hoje e a grade de horas. Dados já salvos no servidor não são afetados.</div>
    <div style='display:flex;gap:8px;'>
      <button onclick='document.getElementById(""resetConfirmOverlay"").style.display=""none""' style='flex:1;background:rgba(255,255,255,.08);color:#a0aec0;border:none;border-radius:6px;padding:8px;font-size:12px;font-weight:700;cursor:pointer;'>Cancelar</button>
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
.controls{display:flex;gap:10px;align-items:center;position:relative;z-index:2;}
.btn-start{background:#7b1fa2;border:none;color:#fff;padding:8px 16px;border-radius:6px;cursor:pointer;font-size:12px;font-weight:600;transition:all .3s;margin-left:8px;}
.btn-start:hover{background:#8e24aa;transform:translateY(-1px);}
.btn-start.running{background:#e53935;}
.btn-min,.btn-close{background:rgba(255,255,255,.1);border:none;color:#abb2bf;width:24px;height:24px;border-radius:4px;cursor:pointer;font-size:12px;font-weight:bold;margin-left:8px;transition:all .2s;}
.btn-min:hover{background:rgba(255,255,255,.2);color:#fff;}
.btn-close{background:rgba(244,67,54,.3);color:#ff6b6b;}
.btn-close:hover{background:rgba(244,67,54,.5);color:#fff;}
.content{padding:20px;flex:1;display:flex;flex-direction:column;gap:15px;}
.timer-row{display:flex;align-items:center;justify-content:center;gap:12px;margin:10px 0;}
.timer{font-size:48px;font-weight:bold;font-family:monospace;color:#7b1fa2;}
.timer.break{color:#4caf50;}
.hg-wrap{position:relative;cursor:pointer;opacity:.5;transition:opacity .3s,transform .2s;padding:4px;}
.hg-wrap:hover{opacity:1;transform:scale(1.1);}
.sync-pulse{position:absolute;top:50%;left:50%;width:0;height:0;border-radius:50%;background:rgba(123,31,162,.3);transform:translate(-50%,-50%);pointer-events:none;opacity:0;}
.sync-pulse.active{animation:pulse .6s ease-out forwards;}
@keyframes pulse{0%{width:0;height:0;opacity:.6}100%{width:60px;height:60px;opacity:0}}
.session-badge{padding:4px 12px;border-radius:20px;background:#7b1fa2;color:#fff;font-size:12px;font-weight:bold;}
.session-badge.break{background:#4caf50;}
.progress{height:6px;background:#404040;border-radius:3px;margin:15px 0;overflow:hidden;}
.progress-bar{height:100%;background:#7b1fa2;border-radius:3px;transition:width 1s linear;}
.progress-bar.break{background:#4caf50;}
.stats{display:flex;justify-content:space-between;}
.stat{text-align:center;}
.lbl{font-size:11px;color:#7f848e;margin-bottom:2px;}
.val{font-size:14px;font-weight:bold;color:#7b1fa2;}
.task-info{text-align:center;font-size:10px;color:#e0e0e0;background:rgba(123,31,162,.1);padding:8px;border-radius:8px;border:1px solid rgba(123,31,162,.3);}
#gradeMount{width:100%;overflow:hidden;margin-top:2px;}
.btn-burger{background:none;border:none;color:rgba(255,255,255,.35);font-size:15px;cursor:pointer;padding:2px 4px;border-radius:4px;transition:color .2s,background .2s;line-height:1;margin-right:2px;}
.btn-burger:hover{color:rgba(255,255,255,.8);background:rgba(255,255,255,.08);}
.settings-panel{position:fixed;top:0;right:-260px;width:250px;height:100%;background:#1a1a2e;border-left:1px solid rgba(123,31,162,.5);z-index:8000;transition:right .28s cubic-bezier(.4,0,.2,1);display:flex;flex-direction:column;box-shadow:-4px 0 24px rgba(0,0,0,.5);}
.settings-panel.open{right:0;}
.settings-overlay{position:fixed;inset:0;background:rgba(0,0,0,.0);z-index:7999;display:none;cursor:pointer;}
.settings-overlay.open{display:block;}
.sp-header{display:flex;justify-content:space-between;align-items:center;padding:13px 14px 11px;border-bottom:1px solid rgba(123,31,162,.4);color:#ce93d8;font-size:12px;font-weight:700;letter-spacing:.5px;}
.sp-close{background:none;border:none;color:#7f848e;font-size:14px;cursor:pointer;padding:0 2px;transition:color .2s;}
.sp-close:hover{color:#ff6b6b;}
.sp-body{padding:14px 14px 10px;overflow-y:auto;flex:1;}
.sp-section{font-size:9px;font-weight:700;letter-spacing:1px;color:#7b1fa2;text-transform:uppercase;margin-bottom:10px;border-bottom:1px solid rgba(123,31,162,.25);padding-bottom:4px;}
.sp-row{display:flex;align-items:center;justify-content:space-between;margin-bottom:10px;gap:8px;}
.sp-label{font-size:11px;color:#b0b8c1;flex:1;line-height:1.3;}
.sp-unit{color:#555;font-size:9px;}
.sp-input{width:64px;background:#0d0d1a;border:1px solid rgba(123,31,162,.4);border-radius:5px;color:#e0c8f0;font-size:12px;padding:4px 7px;text-align:right;font-family:monospace;transition:border .2s;}
.sp-input:focus{outline:none;border-color:#7b1fa2;}
.sp-save{width:100%;margin-top:16px;padding:8px;background:#7b1fa2;border:none;border-radius:6px;color:#fff;font-size:12px;font-weight:700;cursor:pointer;transition:background .2s;}
.sp-save:hover{background:#8e24aa;}
.sp-reset{width:100%;margin-top:8px;padding:7px;background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.15);border-radius:6px;color:#b0b8c1;font-size:11px;font-weight:600;cursor:pointer;transition:background .2s;}
.sp-reset:hover{background:rgba(255,80,80,.15);color:#ff6b6b;border-color:rgba(255,80,80,.3);}
.sp-feedback{font-size:10px;color:#a5d6a7;text-align:center;margin-top:8px;min-height:14px;}";

        private string BuildJs() => @"
var API   = 'https://sistemadetarefas.online';
var UID   = 2;
var X_KEY = 'STI_DESKTOP_K3Y_2025_XrQ9mVpL';

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

var studyingTaskId        = null;
var studyingTaskName      = 'Nenhuma tarefa';
var studyingUserId        = UID;
var studyingProjectId     = null;
var studyingProjectName   = '';
var studyingTempoEstimado = null;

var accumulatedSeconds    = 0;
var lastSavedSessionSec   = 0;

var pauseStartTs          = null;
var pauseDisplayInterval  = null;
var pendingDesvio         = null;

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

    setInterval(function() {
        if (!isRunning || isBreakTime || !studyingTaskId) return;
        // Se lastSavedSessionSec > currentSessionSeconds, houve reset — realinha
        if (lastSavedSessionSec > currentSessionSeconds) lastSavedSessionSec = currentSessionSeconds;
        var diff = currentSessionSeconds - lastSavedSessionSec;
        if (diff > 0) { accumulatedSeconds += diff; lastSavedSessionSec = currentSessionSeconds; }
    }, 1000);

    console.log('[INIT] Widget pronto — 10 requests via fetch()');
});

async function syncCheck() {
    try {
        var resp = await fetch(API + '/pomodoro_sync.php?user_id=' + UID);
        var data = await resp.json();
        if (!data.success || !data.data) return;
        var d = data.data;

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
        var resp = await fetch(API + '/pomodoro_sync.php?action=get_urls_questoes_erradas&user_id=' + UID);
        var data = await resp.json();
        if (data.success && data.urls) {
            data.urls.forEach(function(u) { hostCall('OpenLink', u.url); });
            console.log('[Q_ERRADAS] ' + data.urls.length + ' URLs abertas');
        }
        await postAction({ action: 'clear_task_responder_q_erradas' });
    } catch(e) { console.error('[Q_ERRADAS] Erro:', e); }
}

async function fetchAnotacoes() {
    try {
        var resp = await fetch(API + '/pomodoro_sync.php?action=get_anotacoes_da_task&user_id=' + UID);
        var data = await resp.json();
        if (data.success && data.urls) {
            data.urls.forEach(function(u) { hostCall('OpenLink', u.url); });
            console.log('[ANOTACOES] ' + data.urls.length + ' URLs abertas');
        }
        await postAction({ action: 'clear_task_abrir_anotacoes', user_id: UID });
    } catch(e) { console.error('[ANOTACOES] Erro:', e); }
}

async function fetchSession(sessionId) {
    try {
        var resp = await fetch(API + '/save_session.php?action=get_session&id=' + sessionId);
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
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                action:                  'save_estudo_registro',
                usuario_id:              studyingUserId,
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
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                action:          'save_pausa',
                usuario_id:      studyingUserId,
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
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
    } catch(e) { console.error('[POST_ACTION]', e); }
}

async function updateTarefaSegundos(segundos) {
    if (!studyingTaskId || segundos <= 0) return;
    try {
        var resp = await fetch(API + '/tarefas.php', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'X-App-Key': X_KEY, 'X-Client': 'PomodoroWidget' },
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
    if (!studyingTaskId || accumulatedSeconds <= 0) return;
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
        studyingUserId        = d.userId      || UID;
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

function toggleTimer() { isRunning ? stopTimer() : startTimer(); saveState(); }

function startTimer() {
    if (pauseStartTs !== null) {
        var dur = stopPauseDisplay();
        if (dur >= 1) {
            pendingDesvio = { dur: dur };
            if (dur >= 10) { showDesvioOverlay(dur); return; }
            sendPendingDesvio('');
        } else { pendingDesvio = null; }
    }
    _startTick();
}

function _startTick() {
    isRunning = true;
    var btn = document.getElementById('pomodoroToggle');
    var hdr = document.getElementById('pomodoroHeader');
    if (btn) { btn.textContent = 'Pausar'; btn.className = 'btn-start running'; }
    if (hdr) { hdr.classList.add('running'); if (isBreakTime) hdr.classList.add('break'); }
    if (tickInterval) clearInterval(tickInterval);
    tickInterval = setInterval(tick, 1000);
}

function stopTimer() {
    isRunning = false;
    var btn = document.getElementById('pomodoroToggle');
    var hdr = document.getElementById('pomodoroHeader');
    if (btn) { btn.textContent = 'Iniciar'; btn.className = 'btn-start'; }
    if (hdr) hdr.classList.remove('running', 'break');
    if (tickInterval) { clearInterval(tickInterval); tickInterval = null; }
    if (!isBreakTime) { pauseStartTs = Date.now(); startPauseDisplay(); }
    else { pauseStartTs = null; }
    saveState();
}

function tick() {
    if (currentTime > 0) {
        currentTime--;
        if (!isBreakTime) {
            currentSessionSeconds++;
            totalTodaySeconds++;
            checkTempoExcedido();
        }
    } else { handleTimerComplete(); }
    updateDisplay();
}

function handleTimerComplete() {
    stopTimer();
    if (!isBreakTime) {
        // Foco acabou → prepara break mas mostra overlay antes de iniciar
        isBreakTime = true; currentTime = cfg.breakMinutes * 60;
        var badge = document.getElementById('pomodoroSessionType');
        if (badge) { badge.textContent = 'Descanso'; badge.className = 'session-badge break'; badge.style.display = ''; }
        document.getElementById('pomodoroHeader').classList.add('break');
        savePausa(cfg.breakMinutes, 'pausa', 'descanso_pomodoro');
        updateHgMode(true); updateDisplay(); saveState();
        // Overlay + alarme
        var bEl = document.getElementById('fimFocoBreakMin');
        if (bEl) bEl.textContent = cfg.breakMinutes;
        document.getElementById('fimFocoOverlay').style.display = 'block';
        try { var a = getAlarm(); a.currentTime = 0; a.play(); } catch(e) {}
    } else {
        // Descanso acabou → prepara foco mas mostra overlay antes de iniciar
        isBreakTime = false; currentTime = cfg.focoMinutes * 60;
        var badge = document.getElementById('pomodoroSessionType');
        if (badge) badge.style.display = 'none';
        document.getElementById('pomodoroHeader').classList.remove('break');
        updateHgMode(false); updateDisplay(); saveState();
        // Overlay + alarme
        document.getElementById('fimDescansoOverlay').style.display = 'block';
        try { var a = getAlarm(); a.currentTime = 0; a.play(); } catch(e) {}
    }
}

function fecharFimFoco() {
    document.getElementById('fimFocoOverlay').style.display = 'none';
    try { if (alarmAudio) { alarmAudio.pause(); alarmAudio.currentTime = 0; } } catch(e) {}
    _startTick();
}

function fecharFimDescanso() {
    document.getElementById('fimDescansoOverlay').style.display = 'none';
    try { if (alarmAudio) { alarmAudio.pause(); alarmAudio.currentTime = 0; } } catch(e) {}
    _startTick();
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
        var e = Math.floor((Date.now() - pauseStartTs) / 1000);
        var m = Math.floor(e / 60), s = e % 60;
        var el = document.getElementById('pauseElapsed');
        var lb = document.getElementById('pauseStatLabel');
        if (!el) return;
        el.textContent = (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
        if      (m >= 30) { el.style.color = '#c53030'; if (lb) { lb.textContent = '⚠ Desvio'; lb.style.color = '#c53030'; } }
        else if (m >= 10) { el.style.color = '#e53e3e'; if (lb) { lb.textContent = '⚠ Desvio'; lb.style.color = '#e53e3e'; } }
        else              { el.style.color = '#7b1fa2'; if (lb) { lb.textContent = 'Desvio'; lb.style.color = ''; } }
    }, 1000);
}

function stopPauseDisplay() {
    if (pauseDisplayInterval) { clearInterval(pauseDisplayInterval); pauseDisplayInterval = null; }
    var block = document.getElementById('pauseStatBlock');
    if (block) block.style.display = 'none';
    if (!pauseStartTs) return 0;
    var dur = (Date.now() - pauseStartTs) / 60000;
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
    var m = Math.floor(currentTime / 60), s = currentTime % 60;
    var str = m + ':' + (s < 10 ? '0' : '') + s;
    var timer = document.getElementById('pomodoroTimer');
    var mini  = document.getElementById('pomodoroTimeMini');
    var bar   = document.getElementById('pomodoroProgressBar');
    if (timer) { timer.textContent = str; timer.className = 'timer' + (isBreakTime ? ' break' : ''); }
    if (mini)  { mini.textContent  = str; mini.className  = 'time-mini' + (isBreakTime ? ' break' : ''); }
    var total = isBreakTime ? cfg.breakMinutes * 60 : cfg.focoMinutes * 60;
    if (bar)   { bar.style.width = (((total - currentTime) / total) * 100) + '%'; bar.className = 'progress-bar' + (isBreakTime ? ' break' : ''); }
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
    try { localStorage.setItem('pState', JSON.stringify({ currentTime: currentTime, isBreakTime: isBreakTime, isRunning: isRunning, currentSessionSeconds: currentSessionSeconds, totalTodaySeconds: totalTodaySeconds, lastSavedSessionSec: lastSavedSessionSec })); }
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
        updateHgMode(isBreakTime);
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
    var c = brk ? '#4caf50' : '#7b1fa2';
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

function gradeAddMinutos(h, m) { if (h>=0&&h<24&&m>0) { gradeHoras[h]+=m; gradeRefresh(); gradeSave(); } }
function gradeRefresh() {
    var mount = document.getElementById('gradeMount'); if (!mount) return;
    var hoje = new Date(), dias=['Dom','Seg','Ter','Qua','Qui','Sex','Sab'];
    var nowMin = hoje.getHours()*60+hoje.getMinutes();
    var html = '<table style=""width:100%;border-collapse:separate;border-spacing:2px;table-layout:fixed;""><tr>';
    for (var h=0;h<24;h+=2) {
        var m=(gradeHoras[h]||0)+(gradeHoras[h+1]||0);
        var cor=m===0?'#252525':m<=15?'#3d1454':m<=30?'#5c1a7a':m<=59?'#7b1fa2':m<=79?'#8e24aa':'#ab47bc';
        var cur=(h<=hoje.getHours()&&hoje.getHours()<h+2);
        var brd='none'; if(cur){var pot=m+((h+2)*60-nowMin)*(60/75);brd=pot>=80?'2px solid #FFD700':'2px solid #ce93d8';}
        var lbl=m>0?Math.round(m)+'':'';
        var cellContent = lbl;
        if (m >= 80) {
            cellContent = '<div style=""position:relative;width:100%;height:26px;""><svg viewBox=""0 0 24 24"" style=""position:absolute;top:0;left:0;width:100%;height:100%;""><polygon points=""12,2 15.09,8.26 22,9.27 17,14.14 18.18,21.02 12,17.77 5.82,21.02 7,14.14 2,9.27 8.91,8.26"" fill=""none"" stroke=""#FFD700"" stroke-width=""1.5""/></svg><span style=""position:relative;z-index:1;"">' + lbl + '</span></div>';
        }
        var tip=dias[hoje.getDay()]+' '+(h<10?'0':'')+h+':00 ['+lbl+'min]';
        html+='<td style=""background:'+cor+';border:'+brd+';border-radius:3px;height:26px;text-align:center;vertical-align:middle;font-size:8px;font-weight:700;color:'+(m>=60?'#e0e0e0':'#999')+';padding:0;"" data-tip=""'+tip+'"">'+cellContent+'</td>';
    }
    html+='</tr></table>'; mount.innerHTML=html;
}

function initTooltip() {
    var tip=null,delay=null,mx=0,my=0;
    function gT(){if(!tip)tip=document.getElementById('customTooltip');return tip;}
    function sT(t){var e=gT();if(!e)return;e.innerHTML=t;pT(e);e.style.display='block';}
    function pT(e){var l=mx+4;if(mx>255)l=mx-e.offsetWidth-4;e.style.left=l+'px';e.style.top=(my+22)+'px';}
    function hT(){var e=gT();if(e)e.style.display='none';if(delay){clearTimeout(delay);delay=null;}}
    function fT(el){while(el&&el.nodeType===1){var t=el.getAttribute&&el.getAttribute('data-tip');if(t)return t;el=el.parentNode;}return null;}
    document.body.addEventListener('mousemove',function(e){mx=e.clientX;my=e.clientY;var el=gT();if(el&&el.style.display!=='none')pT(el);});
    document.body.addEventListener('mouseover',function(e){var t=fT(e.target);if(!t){hT();return;}if(delay)clearTimeout(delay);delay=setTimeout(function(){sT(t);},0);});
    document.body.addEventListener('mouseout',function(){hT();});
}

var cfg = {
    syncInterval:  15,
    saveInterval:  30,
    minMinutes:    0.5,
    focoMinutes:   60,
    breakMinutes:  15
};

var _syncTimer = null;
var _saveTimer = null;

function loadSettings() {
    try {
        var s = JSON.parse(localStorage.getItem('pwCfg') || 'null');
        if (s) {
            cfg.syncInterval = s.syncInterval || cfg.syncInterval;
            cfg.saveInterval = s.saveInterval || cfg.saveInterval;
            cfg.minMinutes   = s.minMinutes   || cfg.minMinutes;
            cfg.focoMinutes  = s.focoMinutes  || cfg.focoMinutes;
            cfg.breakMinutes = s.breakMinutes || cfg.breakMinutes;
        }
    } catch(e) {}
    applySettings();
    syncInputsFromCfg();
}

function applySettings() {
    if (_syncTimer) clearInterval(_syncTimer);
    if (_saveTimer) clearInterval(_saveTimer);
    _syncTimer = setInterval(syncCheck,   cfg.syncInterval * 1000);
    _saveTimer = setInterval(saveTimeAcc, cfg.saveInterval * 1000);
    console.log('[CFG] sync=' + cfg.syncInterval + 's save=' + cfg.saveInterval + 's minMin=' + cfg.minMinutes + ' foco=' + cfg.focoMinutes + 'min break=' + cfg.breakMinutes + 'min');
}

function syncInputsFromCfg() {
    var f = function(id, val) { var el=document.getElementById(id); if(el) el.value=val; };
    f('cfgSync',   cfg.syncInterval);
    f('cfgSave',   cfg.saveInterval);
    f('cfgMinMin', cfg.minMinutes);
    f('cfgFoco',   cfg.focoMinutes);
    f('cfgBreak',  cfg.breakMinutes);
}

function saveSettings() {
    var g = function(id) { return parseFloat(document.getElementById(id).value); };
    cfg.syncInterval = Math.max(5,   Math.min(300, g('cfgSync')   || 15));
    cfg.saveInterval = Math.max(10,  Math.min(600, g('cfgSave')   || 30));
    cfg.minMinutes   = Math.max(0.1, Math.min(5,   g('cfgMinMin') || 0.5));
    cfg.focoMinutes  = Math.max(1,   Math.min(120, g('cfgFoco')   || 60));
    cfg.breakMinutes = Math.max(1,   Math.min(60,  g('cfgBreak')  || 15));
    try { localStorage.setItem('pwCfg', JSON.stringify(cfg)); } catch(e) {}
    applySettings();
    syncInputsFromCfg();
    var fb = document.getElementById('spFeedback');
    if (fb) { fb.textContent = 'Salvo!'; setTimeout(function(){ fb.textContent=''; }, 2000); }
    console.log('[CFG] Configuracoes salvas');
}

function confirmarReset() {
    var o = document.getElementById('resetConfirmOverlay');
    if (o) o.style.display = 'block';
}

function resetSessaoConfirmado() {
    var o = document.getElementById('resetConfirmOverlay');
    if (o) o.style.display = 'none';
    resetSessao();
}

function resetSessao() {
    if (isRunning) stopTimer();
    currentSessionSeconds = 0;
    totalTodaySeconds     = 0;
    lastSavedSessionSec   = 0;
    currentTime           = cfg.focoMinutes * 60;
    isBreakTime           = false;
    accumulatedSeconds    = 0;
    lastSavedSessionSec   = 0;
    pauseStartTs          = null;
    tempoExcedidoNotificado = false;
    gradeHoras = new Array(24).fill(0);
    gradeSave();
    stopPauseDisplay();
    updateHgMode(false);
    updateDisplay();
    gradeRefresh();
    saveState();
    var fb = document.getElementById('spFeedbackReset');
    if (fb) { fb.textContent = 'Sessão zerada!'; setTimeout(function(){ fb.textContent=''; }, 2000); }
    console.log('[RESET] Sessão local zerada');
}

function togglePanel() {
    var panel   = document.getElementById('settingsPanel');
    var overlay = document.getElementById('settingsOverlay');
    if (!panel) return;
    var opening = !panel.classList.contains('open');
    panel.classList.toggle('open');
    overlay.classList.toggle('open');
    if (opening) syncInputsFromCfg();
}

function shouldProcess(dict, id) {
    var COOLDOWN = 5*60*1000;
    if (dict[id] && Date.now()-dict[id] < COOLDOWN) return false;
    dict[id] = Date.now(); return true;
}
function dateToday() { var d=new Date(); return d.getFullYear()+'-'+pad(d.getMonth()+1)+'-'+pad(d.getDate()); }
function dateNow()   { return dateToday()+' '+new Date().toTimeString().slice(0,8); }
function fmtDateTime(d) { return d.getFullYear()+'-'+pad(d.getMonth()+1)+'-'+pad(d.getDate())+' '+d.toTimeString().slice(0,8); }
function pad(n) { return n < 10 ? '0'+n : ''+n; }";
    }
}