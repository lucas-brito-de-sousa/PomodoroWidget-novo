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
    // ── Janela de debug do vídeo (WinForms + WebView2) ──────────────────
    // Substitui a antiga janela nativa do OpenCV. O Python não desenha mais
    // nada — só manda frames JPEG/base64 (mensagem "gaze_frame") e status
    // via stdout; esta Form exibe tudo em HTML/CSS/JS de verdade, com
    // sliders/checkboxes estilizados e "sempre no topo" via Form.TopMost
    // nativo (muito mais confiável que mexer na janela do OpenCV de fora).
    //
    // Ajustes de slider/checkbox voltam pro Python via stdin do processo:
    // JS -> CameraHostObject.SendConfig(json) -> Form1.SendGazeCommand(json)
    // -> _gazeProcess.StandardInput.WriteLine(json).
    public class GazeCameraForm : Form
    {
        private readonly Action<string> _sendCommand;
        private WebView2 webView;
        private bool _webViewReady = false;

        public GazeCameraForm(Action<string> sendCommand)
        {
            _sendCommand = sendCommand;

            Text = "Câmera - Monitoramento de Foco";
            Width = 420;
            Height = 720;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = true;
            MaximizeBox = true;
            ShowInTaskbar = true;

            webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(webView);

            Load += async (s, e) =>
            {
                try { await SetupWebView(); }
                catch (Exception ex) { MessageBox.Show("Falha ao iniciar a janela da câmera: " + ex.Message); }
            };
        }

        private async Task SetupWebView()
        {
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PomodoroWidget");
            string userDataFolder = Path.Combine(baseDir, "WebView2Data_Camera");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await webView.EnsureCoreWebView2Async(env);

            var s = webView.CoreWebView2.Settings;
            s.AreDevToolsEnabled = true;
            s.IsScriptEnabled = true;
            s.AreDefaultContextMenusEnabled = true;
            s.AreDefaultScriptDialogsEnabled = true;
            s.IsStatusBarEnabled = false;
            s.AreHostObjectsAllowed = true;

            webView.CoreWebView2.AddHostObjectToScript("camera", new CameraHostObject(this));
            webView.NavigationCompleted += (o, e) => { _webViewReady = true; };

            string tempDir = Path.Combine(Path.GetTempPath(), "PomodoroWidget_Camera");
            Directory.CreateDirectory(tempDir);
            string htmlFile = Path.Combine(tempDir, "camera.html");
            File.WriteAllText(htmlFile, BuildCameraHtml(), Encoding.UTF8);

            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "gazecamera.local",
                tempDir,
                CoreWebView2HostResourceAccessKind.Allow);

            webView.CoreWebView2.Navigate("https://gazecamera.local/camera.html");
        }

        // ── Chamados pelo Form1 quando chegam mensagens do Python ───────
        public void UpdateFrame(string base64Jpeg)
        {
            if (!_webViewReady) return;
            string js = "updateCameraFrame(" + Form1.JsString("data:image/jpeg;base64," + base64Jpeg) + ");";
            _ = webView.ExecuteScriptAsync(js);
        }

        public void UpdateStatus(string direction, bool currentFocused, bool faceDetected)
        {
            if (!_webViewReady) return;
            string js = "updateCameraStatus(" + Form1.JsString(direction) + "," +
                (currentFocused ? "true" : "false") + "," + (faceDetected ? "true" : "false") + ");";
            _ = webView.ExecuteScriptAsync(js);
        }

        // configJsonLine já é a linha JSON inteira e válida vinda do Python
        // (mensagem "gaze_config") — passa direto como objeto literal pro JS,
        // sem precisar reconstruir campo por campo aqui no C#.
        public void InitConfig(string configJsonLine)
        {
            if (!_webViewReady) return;
            string js = "initCameraConfig(" + configJsonLine + ");";
            _ = webView.ExecuteScriptAsync(js);
        }

        // Chamado pelo CameraHostObject quando o JS manda um novo config
        // (slider/checkbox mexido) — encaminha pro processo Python via stdin.
        internal void ForwardConfigToPython(string json)
        {
            _sendCommand?.Invoke(json);
        }

        // Chamado pelo CameraHostObject depois de mostrar/esconder os
        // controles — ajusta a ALTURA real da janela (não só do conteúdo
        // HTML) pra bater com o que sobrou de conteúdo, em vez de deixar
        // espaço vazio sobrando ou conteúdo cortado.
        internal void ResizeToContent(int contentHeight)
        {
            if (IsDisposed) return;
            try
            {
                int screenHeight = Screen.FromControl(this).WorkingArea.Height;
                int minHeight = 250;
                int maxHeight = screenHeight - 40;
                int target = Math.Max(minHeight, Math.Min(contentHeight + 4, maxHeight));
                // ClientSize é a área interna (onde o WebView2 fica, via
                // Dock=Fill) — ajustar por ela garante que a largura da
                // borda/título não distorça a conta de altura.
                ClientSize = new Size(ClientSize.Width, target);
            }
            catch { }
        }

        private string BuildCameraHtml()
        {
            return @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<style>
  body { background:#1e1e1e; color:#e0e0e0; font-family: 'Segoe UI', sans-serif; margin:0; padding:14px; }
  #cameraFrame { width:100%; max-width:100%; height:auto; border-radius:8px; background:#000; display:block; margin-bottom:10px; min-height:200px; object-fit:contain; }
  .row { display:flex; align-items:center; gap:8px; margin-bottom:8px; font-size:12px; }
  .row label { width:100px; flex-shrink:0; color:#b0b8c1; }
  input[type=range] { flex:1; accent-color:#7b1fa2; }
  .val { width:44px; text-align:right; color:#7b1fa2; font-weight:bold; }
  .chk-row { display:flex; align-items:center; gap:6px; margin-bottom:8px; font-size:12px; }
  .chk-row input[type=checkbox] { accent-color:#7b1fa2; width:16px; height:16px; }
  .chk-row label { color:#b0b8c1; }
  #statusText { font-size:13px; font-weight:bold; margin-bottom:10px; padding:8px 10px; border-radius:6px; background:rgba(255,255,255,.05); text-align:center; }
  h3 { font-size:11px; color:#7f848e; text-transform:uppercase; letter-spacing:.5px; margin:14px 0 6px; }
  .toggle-row { display:flex; gap:8px; margin-bottom:8px; }
  .toggle-btn { flex:1; background:#2a2a2a; color:#b0b8c1; border:1px solid rgba(255,255,255,.1); border-radius:6px; padding:6px 10px; font-size:11px; cursor:pointer; }
  .toggle-btn.active { background:#7b1fa2; color:#fff; border-color:#7b1fa2; }
  .toggle-btn:hover { filter:brightness(1.15); }
  #controlsSection.hidden { display:none; }
</style>
</head>
<body>
  <img id='cameraFrame' src='' alt='camera'>
  <div class='toggle-row'>
    <button type='button' class='toggle-btn active' id='btnLegendas' onclick='onToggleLegendas()'>Legendas</button>
    <button type='button' class='toggle-btn active' id='btnControles' onclick='onToggleControles()'>Controles</button>
  </div>
  <div id='statusText'>Aguardando...</div>

  <div id='controlsSection'>
  <h3>Controles</h3>
  <div class='chk-row'><input type='checkbox' id='chkTopMost' onchange='onTopMostChange()'><label for='chkTopMost'>Sempre no topo</label></div>
  <div class='chk-row'><input type='checkbox' id='chkApply' onchange='onConfigChange()'><label for='chkApply'>Aplicar ajustes</label></div>
  <div class='chk-row'><input type='checkbox' id='chkAutoExp' onchange='onConfigChange()'><label for='chkAutoExp'>Auto-exposicao</label></div>

  <h3>Equalizador</h3>
  <div class='row'><label>Brilho</label><input type='range' id='sBrilho' min='-100' max='100' value='0' oninput='onSliderInput(this)'><span class='val' id='vBrilho'>0</span></div>
  <div class='row'><label>Contraste</label><input type='range' id='sContraste' min='-100' max='100' value='0' oninput='onSliderInput(this)'><span class='val' id='vContraste'>0</span></div>
  <div class='row'><label>Exposicao</label><input type='range' id='sExposicao' min='-13' max='13' value='-7' oninput='onSliderInput(this)'><span class='val' id='vExposicao'>-7</span></div>
  <div class='row'><label>Ganho</label><input type='range' id='sGanho' min='0' max='200' value='0' oninput='onSliderInput(this)'><span class='val' id='vGanho'>0</span></div>

  <h3>Calibracao manual</h3>
  <div class='row'><label>Centro</label><input type='range' id='sCentro' min='0' max='1000' value='500' oninput='onSliderInput(this)'><span class='val' id='vCentro'>0.500</span></div>
  <div class='row'><label>Margem</label><input type='range' id='sMargem' min='0' max='500' value='150' oninput='onSliderInput(this)'><span class='val' id='vMargem'>0.150</span></div>
  <div class='row'><label>Limiar piscada</label><input type='range' id='sPiscada' min='10' max='100' value='38' oninput='onSliderInput(this)'><span class='val' id='vPiscada'>3.8</span></div>
  </div>

<script>
var currentConfig = {
  brightness:0, contrast:0, exposure:-7, gain:0,
  center_x1000:500, margin_x1000:150, blink_x10:38,
  apply_adjustments:false, auto_exposure:true, show_legends:true
};

function onToggleLegendas() {
    currentConfig.show_legends = !currentConfig.show_legends;
    document.getElementById('btnLegendas').classList.toggle('active', currentConfig.show_legends);
    sendConfig();
}

function onToggleControles() {
    var section = document.getElementById('controlsSection');
    var nowHidden = section.classList.toggle('hidden');
    document.getElementById('btnControles').classList.toggle('active', !nowHidden);
    resizeToContent();
}

// Espera o layout recalcular (o toggle muda display:none, que só reflete
// no scrollHeight depois de um reflow) e manda a altura real do conteúdo
// pro C# ajustar a janela — sem isso, a Form ficava com o tamanho fixo de
// sempre, sobrando ou faltando espaço conforme os controles apareciam/
// sumiam.
function resizeToContent() {
    setTimeout(function() {
        hostCall('ResizeWindow', document.body.scrollHeight);
    }, 20);
}

function updateCameraFrame(dataUrl) {
    var img = document.getElementById('cameraFrame');
    if (img) img.src = dataUrl;
}

function updateCameraStatus(direction, currentFocused, faceDetected) {
    var el = document.getElementById('statusText');
    if (!el) return;
    var label;
    if (!faceDetected) label = 'Rosto nao detectado';
    else if (direction === 'blinking') label = 'Piscando';
    else if (direction === 'right') label = 'Olhando direita';
    else if (direction === 'left') label = 'Olhando esquerda';
    else if (direction === 'center') label = 'Olhando centro';
    else label = 'Indefinido';
    el.textContent = label + ' - Foco: ' + (currentFocused ? 'SIM' : 'NAO');
    el.style.color = currentFocused ? '#4caf50' : '#e53935';
}

function initCameraConfig(cfg) {
    currentConfig.brightness = cfg.brightness;
    currentConfig.contrast = cfg.contrast;
    currentConfig.exposure = cfg.exposure;
    currentConfig.gain = cfg.gain;
    currentConfig.center_x1000 = cfg.manual_center_x1000;
    currentConfig.margin_x1000 = cfg.manual_margin_x1000;
    currentConfig.blink_x10 = cfg.blink_threshold_x10;
    currentConfig.apply_adjustments = !!cfg.apply_enabled;
    currentConfig.auto_exposure = !!cfg.auto_exposure;

    document.getElementById('sBrilho').value = currentConfig.brightness;
    document.getElementById('sContraste').value = currentConfig.contrast;
    document.getElementById('sExposicao').value = currentConfig.exposure;
    document.getElementById('sGanho').value = currentConfig.gain;
    document.getElementById('sCentro').value = currentConfig.center_x1000;
    document.getElementById('sMargem').value = currentConfig.margin_x1000;
    document.getElementById('sPiscada').value = currentConfig.blink_x10;
    document.getElementById('chkApply').checked = currentConfig.apply_adjustments;
    document.getElementById('chkAutoExp').checked = currentConfig.auto_exposure;

    refreshLabels();
}

function refreshLabels() {
    document.getElementById('vBrilho').textContent = currentConfig.brightness;
    document.getElementById('vContraste').textContent = currentConfig.contrast;
    document.getElementById('vExposicao').textContent = currentConfig.exposure;
    document.getElementById('vGanho').textContent = currentConfig.gain;
    document.getElementById('vCentro').textContent = (currentConfig.center_x1000/1000).toFixed(3);
    document.getElementById('vMargem').textContent = (currentConfig.margin_x1000/1000).toFixed(3);
    document.getElementById('vPiscada').textContent = (currentConfig.blink_x10/10).toFixed(1);
}

function hostCall(method) {
    var args = Array.prototype.slice.call(arguments, 1);
    try { var h = window.chrome.webview.hostObjects.sync.camera; h[method].apply(h, args); }
    catch(e) { console.error('[hostCall camera] ' + method + ':', e); }
}

function onSliderInput(el) {
    var map = {
        sBrilho:'brightness', sContraste:'contrast', sExposicao:'exposure',
        sGanho:'gain', sCentro:'center_x1000', sMargem:'margin_x1000', sPiscada:'blink_x10'
    };
    var key = map[el.id];
    if (key) currentConfig[key] = parseInt(el.value, 10);
    refreshLabels();
    sendConfig();
}

function onConfigChange() {
    currentConfig.apply_adjustments = document.getElementById('chkApply').checked;
    currentConfig.auto_exposure = document.getElementById('chkAutoExp').checked;
    sendConfig();
}

function onTopMostChange() {
    var checked = document.getElementById('chkTopMost').checked;
    hostCall('SetTopMost', checked);
}

var sendTimer = null;
function sendConfig() {
    if (sendTimer) clearTimeout(sendTimer);
    sendTimer = setTimeout(function() {
        var payload = Object.assign({cmd:'update_config'}, currentConfig);
        hostCall('SendConfig', JSON.stringify(payload));
    }, 80);
}
</script>
</body>
</html>";
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.AutoDual)]
        public class CameraHostObject
        {
            private readonly GazeCameraForm f;
            public CameraHostObject(GazeCameraForm form) { f = form; }

            public void SendConfig(string json) => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.ForwardConfigToPython(json); }
                catch { }
            }));

            public void SetTopMost(bool value) => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.TopMost = value; }
                catch { }
            }));

            public void ResizeWindow(int contentHeight) => f.Invoke(new Action(() =>
            {
                try { if (!f.IsDisposed) f.ResizeToContent(contentHeight); }
                catch { }
            }));
        }
    }
}