using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PomodoroWidget
{
    public partial class Form1 : Form
    {
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;

        private Timer syncTimer;
        private Timer secondsUpdateTimer;
        private string appDataPath;
        private bool isWebBrowserReady = false;

        // Variáveis para sincronização
        private int? currentStudyingTaskId = null;
        private string currentStudyingTaskName = "Nenhuma tarefa";
        private int lastSavedSeconds = 0;
        private int totalSecondsAdded = 0;

        // Variáveis para movimento
        private bool isDragging = false;
        private Point dragStartPoint;


        private DateTime lastUpdateTime = DateTime.MinValue;
        private DateTime lastSyncCheckTime = DateTime.MinValue;
        private readonly TimeSpan updateInterval = TimeSpan.FromSeconds(30); // Atualizar a cada 30 segundos
        private readonly TimeSpan syncCheckInterval = TimeSpan.FromSeconds(15); // Verificar sync a cada 15 segundos
        private int accumulatedSeconds = 0;



        public Form1()
        {
            InitializeComponent();
            // Configurar caminho de dados
            appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PomodoroWidget");
            Directory.CreateDirectory(appDataPath);

            SetupForm();
            SetupWebBrowser();
            SetupSyncTimer();
            SetupSecondsUpdateTimer();

            this.FormClosing += Form1_FormClosing;



            Log("Pomodoro Widget iniciado - SEM BORDAS COM BOTÕES CORRETOS");
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveAccumulatedTime();
        }

        private void SetupForm()
        {
            this.Text = "Pomodoro Widget";
            this.Size = new Size(350, 500);
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(
                Screen.PrimaryScreen.WorkingArea.Width - this.Width - 20,
                Screen.PrimaryScreen.WorkingArea.Height - this.Height - 20
            );

            // REMOVER BORDA COMPLETAMENTE
            this.FormBorderStyle = FormBorderStyle.None;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.Padding = new Padding(0);
            this.DoubleBuffered = true;

            // Aplicar cantos arredondados
            SetRoundRegion(22);

            // Manter sempre no topo
            SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }

        private void SetRoundRegion(int radius)
        {
            IntPtr region = CreateRoundRectRgn(0, 0, this.Width + 1, this.Height + 1, radius, radius);
            SetWindowRgn(this.Handle, region, true);
            DeleteObject(region);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Reaplicar cantos arredondados quando redimensionar
            SetRoundRegion(22);
        }

        private void SetupWebBrowser()
        {
            // Configurar WebBrowser para versão mais recente do IE possível
            var appName = Process.GetCurrentProcess().ProcessName + ".exe";
            SetBrowserFeatureControl();

            webBrowser1.ScriptErrorsSuppressed = true;
            webBrowser1.ScrollBarsEnabled = false;
            webBrowser1.AllowNavigation = false;
            webBrowser1.BackColor = Color.FromArgb(30, 30, 30);
            webBrowser1.AllowWebBrowserDrop = false;
            webBrowser1.Dock = DockStyle.Fill;

            webBrowser1.DocumentCompleted += WebBrowser1_DocumentCompleted;

            // Carregar conteúdo inicial
            LoadHtmlContent();
        }

        private static void SetBrowserFeatureControl()
        {
            var appName = System.IO.Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);

            // IE11
            SetBrowserFeatureControlKey("FEATURE_BROWSER_EMULATION", appName, 11001);
            SetBrowserFeatureControlKey("FEATURE_AJAX_CONNECTIONEVENTS", appName, 1);
            SetBrowserFeatureControlKey("FEATURE_ENABLE_CLIPCHILDREN_OPTIMIZATION", appName, 1);
            SetBrowserFeatureControlKey("FEATURE_GPU_RENDERING", appName, 1);
            SetBrowserFeatureControlKey("FEATURE_WEBOC_DOCUMENT_ZOOM", appName, 1);
            SetBrowserFeatureControlKey("FEATURE_NINPUT_LEGACY", appName, 1);
        }

        private static void SetBrowserFeatureControlKey(string feature, string appName, uint value)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    String.Concat(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\", feature),
                    Microsoft.Win32.RegistryKeyPermissionCheck.ReadWriteSubTree))
                {
                    key.SetValue(appName, (UInt32)value, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao definir chave de registro: {ex.Message}");
            }
        }

        private void WebBrowser1_DocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            isWebBrowserReady = true;
            webBrowser1.ObjectForScripting = new ObjectForScripting(this);

            // Configurar eventos de mouse para movimento
            SetupMoveEvents();

            Log("WebBrowser carregado e pronto");
        }

        private void SetupMoveEvents()
        {
            if (webBrowser1.Document != null && webBrowser1.Document.Body != null)
            {
                // Adicionar eventos de mouse para todo o documento
                webBrowser1.Document.MouseDown += Document_MouseDown;
                webBrowser1.Document.MouseMove += Document_MouseMove;
                webBrowser1.Document.MouseUp += Document_MouseUp;

                // Adicionar estilo para cursor de movimento
                webBrowser1.Document.InvokeScript("eval", new object[] { @"
                    document.body.style.cursor = 'move';
                    // Prevenir seleção de texto durante o arrasto
                    document.body.style.userSelect = 'none';
                    document.body.style.webkitUserSelect = 'none';
                    document.body.style.mozUserSelect = 'none';
                    document.body.style.msUserSelect = 'none';
                " });
            }
        }

        private void Document_MouseDown(object sender, HtmlElementEventArgs e)
        {
            // Verificar se não é um clique em botão
            HtmlElement target = e.ToElement;
            while (target != null)
            {
                if (target.TagName.ToLower() == "button")
                {
                    return; // Não iniciar arrasto se for clique em botão
                }
                target = target.Parent;
            }

            isDragging = true;
            dragStartPoint = new Point(e.MousePosition.X + this.Left, e.MousePosition.Y + this.Top);
            webBrowser1.Document.InvokeScript("eval", new object[] { "document.body.style.cursor = 'grabbing';" });
        }

        private void Document_MouseMove(object sender, HtmlElementEventArgs e)
        {
            if (isDragging)
            {
                int newX = e.MousePosition.X + this.Left;
                int newY = e.MousePosition.Y + this.Top;

                int deltaX = newX - dragStartPoint.X;
                int deltaY = newY - dragStartPoint.Y;

                this.Location = new Point(this.Left + deltaX, this.Top + deltaY);
                dragStartPoint = new Point(newX, newY);
            }
        }

        private void Document_MouseUp(object sender, HtmlElementEventArgs e)
        {
            isDragging = false;
            webBrowser1.Document.InvokeScript("eval", new object[] { "document.body.style.cursor = 'move';" });
        }

        private void LoadHtmlContent()
        {
            string htmlContent = GetCompleteHtmlContent();
            webBrowser1.DocumentText = htmlContent;
        }

        private string GetCompleteHtmlContent()
        {
            string css = GetCssStyles();
            string javascript = GetJavaScriptCode();

            return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <meta http-equiv='X-UA-Compatible' content='IE=edge'>
    <style>{css}</style>
</head>
<body>
    <div class='pomodoro-widget' id='pomodoroWidget'>
        <div class='pomodoro-header' id='pomodoroHeader'>
            <div class='pomodoro-title' id='pomodoroTitle'>
                <span class='pomodoro-task-title' id='pomodoroTaskTitle'>Pomodoro Widget</span>
                <span class='pomodoro-time-mini' id='pomodoroTimeMini'>25:00</span>
            </div>
            <div class='pomodoro-controls'>
                <button class='pomodoro-minimize' id='pomodoroMinimize' onclick='window.external.MinimizeWidget()'>▼</button>
                <button class='pomodoro-toggle' id='pomodoroToggle' onclick='window.external.ToggleTimer()'>Iniciar</button>
                <button class='pomodoro-close' id='pomodoroClose' onclick='window.external.CloseApp()'>✕</button>
            </div>
        </div>
        <div class='pomodoro-content' id='pomodoroContent'>
            <div class='pomodoro-timer' id='pomodoroTimer'>25:00</div>
            <div class='pomodoro-session-info'>
                <span class='pomodoro-session-type-badge' id='pomodoroSessionType'>Foco</span>
            </div>
            <div class='pomodoro-progress'>
                <div class='pomodoro-progress-bar' id='pomodoroProgressBar' style='width: 0%'></div>
            </div>
            <div class='pomodoro-stats'>
                <div class='pomodoro-session'>
                    <div class='pomodoro-session-label'>Sessão Atual</div>
                    <div class='pomodoro-session-value' id='currentSession'>00:00:00</div>
                </div>
                <div class='pomodoro-session'>
                    <div class='pomodoro-session-label'>Total Hoje</div>
                    <div class='pomodoro-session-value' id='totalToday'>00:00:00</div>
                </div>
            </div>
            <div class='task-info' id='taskInfo'>
                Tarefa atual: Nenhuma
            </div>
        </div>
    </div>
    <script>{javascript}</script>
</body>
</html>";
        }
        private string GetCssStyles()
        {
            return @"
* { 
    margin: 0; 
    padding: 0; 
    box-sizing: border-box; 
}
body { 
    margin: 0; 
    padding: 0; 
    background: #1E1E1E; 
    color: white; 
    font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; 
    overflow: hidden; 
    height: 100vh;
    cursor: move;
    -webkit-user-select: none;
    -moz-user-select: none;
    -ms-user-select: none;
    user-select: none;
    border-radius: 15px;
}
.pomodoro-widget { 
    background: #1E1E1E; 
    height: 100vh; 
    display: flex; 
    flex-direction: column;
    border-radius: 15px;
    border: 1px solid rgba(255, 255, 255, 0.1);
    box-shadow: 0 10px 40px rgba(0, 0, 0, 0.5);
    overflow: hidden;
}
.pomodoro-header { 
    background: #2D2D2D; 
    padding: 12px 15px; 
    display: flex; 
    justify-content: space-between; 
    align-items: center; 
    border-bottom: 1px solid #404040; 
    user-select: none;
    cursor: move;
    border-top-left-radius: 15px;
    border-top-right-radius: 15px;
    position: relative;
    overflow: hidden;
}

/* Efeito de borda animada infinita */
.pomodoro-header.border-animation::before {
    content: '';
    width: calc(100% + 2px);
    height: calc(100% - 12px);
    position: absolute;
    top: 6px;
    left: 0px;
    background: #353638;
    transition: transform 0.5s ease-in-out;
    transform: scaleY(1);
    z-index: 1;
    animation: borderTopBottom 2s infinite;
}

.pomodoro-header.border-animation::after {
    content: '';
    width: calc(100% - 12px);
    height: calc(100% + 4px);
    position: absolute;
    top: -2px;
    left: 6px;
    background: #353638;
    transition: transform 0.5s ease-in-out;
    transform: scaleX(1);
    z-index: 1;
    animation: borderLeftRight 2s infinite;
}

@keyframes borderTopBottom {
    0%, 100% {
        transform: scaleY(1);
    }
    50% {
        transform: scaleY(0);
    }
}

@keyframes borderLeftRight {
    0%, 100% {
        transform: scaleX(1);
    }
    50% {
        transform: scaleX(0);
    }
}

/* Efeito para modo break */
.pomodoro-header.border-animation.break::before,
.pomodoro-header.border-animation.break::after {
    background: #4caf50;
}

.pomodoro-title { 
    font-weight: 600; 
    color: #e0e0e0; 
    font-size: 14px; 
    display: flex; 
    align-items: center; 
    gap: 8px; 
    position: relative;
    z-index: 2;
}
.pomodoro-task-title { 
    max-width: 120px; 
    overflow: hidden; 
    text-overflow: ellipsis; 
    white-space: nowrap; 
}
.pomodoro-time-mini { 
    background: #7b1fa2; 
    color: white; 
    padding: 4px 8px; 
    border-radius: 12px; 
    font-size: 11px; 
    font-weight: bold; 
    font-family: 'Consolas', 'Monaco', monospace; 
}
.pomodoro-time-mini.break { 
    background: #4caf50; 
}
.pomodoro-controls { 
    display: flex; 
    gap: 10px; 
    align-items: center; 
    position: relative;
    z-index: 2;
}
.pomodoro-toggle { 
    background: #7b1fa2; 
    border: none; 
    color: white; 
    padding: 8px 16px; 
    border-radius: 6px; 
    cursor: pointer; 
    font-size: 12px; 
    font-weight: 600; 
    transition: all 0.3s ease; 
    margin-left: 8px;
}
.pomodoro-toggle:hover { 
    background: #8e24aa; 
    transform: translateY(-1px); 
}
.pomodoro-toggle.running { 
    background: #e53935; 
}
.pomodoro-minimize, .pomodoro-maximize, .pomodoro-close { 
    background: rgba(255, 255, 255, 0.1); 
    border: none; 
    color: #abb2bf; 
    width: 24px; 
    height: 24px; 
    border-radius: 4px; 
    cursor: pointer; 
    display: flex; 
    align-items: center; 
    justify-content: center; 
    font-size: 12px; 
    transition: all 0.2s ease; 
    font-weight: bold;
    margin-left: 8px;


}
.pomodoro-minimize:hover, .pomodoro-maximize:hover { 
    background: rgba(255, 255, 255, 0.2); 
    color: white; 
}
.pomodoro-close { 
    background: rgba(244, 67, 54, 0.3); 
    color: #ff6b6b; 
}
.pomodoro-close:hover { 
    background: rgba(244, 67, 54, 0.5); 
    color: white; 
}
.pomodoro-content { 
    padding: 20px; 
    flex: 1; 
    display: flex; 
    flex-direction: column; 
    gap: 15px; 
}
.pomodoro-timer { 
    font-size: 48px; 
    font-weight: bold; 
    text-align: center; 
    font-family: 'Consolas', 'Monaco', monospace; 
    color: #7b1fa2; 
    margin: 10px 0; 
}
.pomodoro-timer.break { 
    color: #4caf50; 
}
.pomodoro-session-info { 
    text-align: center; 
    font-size: 14px; 
    color: #7f848e; 
}
.pomodoro-session-type-badge { 
    display: inline-block; 
    padding: 4px 12px; 
    border-radius: 20px; 
    background: #7b1fa2; 
    color: white; 
    font-size: 12px; 
    font-weight: bold; 
}
.pomodoro-session-type-badge.break { 
    background: #4caf50; 
}
.pomodoro-progress { 
    height: 6px; 
    background: #404040; 
    border-radius: 3px; 
    margin: 15px 0; 
    overflow: hidden; 
}
.pomodoro-progress-bar { 
    height: 100%; 
    background: #7b1fa2; 
    border-radius: 3px; 
    transition: width 1s linear; 
}
.pomodoro-progress-bar.break { 
    background: #4caf50; 
}
.pomodoro-stats { 
    display: flex; 
    justify-content: space-between; 
    font-size: 12px; 
    color: #7f848e; 
}
.pomodoro-session { 
    text-align: center; 
}
.pomodoro-session-label { 
    font-size: 11px; 
    color: #7f848e; 
    margin-bottom: 2px; 
}
.pomodoro-session-value { 
    font-size: 14px; 
    font-weight: bold; 
    color: #7b1fa2; 
}
.task-info { 
    text-align: center; 
    font-size: 10px; 
    color: #e0e0e0; 
    background: rgba(123, 31, 162, 0.1); 
    padding: 8px; 
    border-radius: 8px; 
    border: 1px solid rgba(123, 31, 162, 0.3); 
}";
        }

        private string GetJavaScriptCode()
        {
            return @"
// Variáveis globais para o widget
var isRunning = false;
var currentTime = 60 * 60; // 25 minutos
var isBreakTime = false;
var currentSessionSeconds = 0;
var totalTodaySeconds = 0;
var updateInterval = null;

// Inicialização
document.addEventListener('DOMContentLoaded', function() {
    initializeWidget();
    loadState();
    updateDisplay();
});

function initializeWidget() {
    console.log('Widget inicializado - Com efeito de borda animada');
}

function toggleTimer() {
    if (isRunning) {
        stopTimer();
    } else {
        startTimer();
    }
    saveState();
}

function startTimer() {
    isRunning = true;
    var toggleBtn = document.getElementById('pomodoroToggle');
    var header = document.getElementById('pomodoroHeader');
    
    if (toggleBtn) {
        toggleBtn.textContent = 'Pausar';
        toggleBtn.className = 'pomodoro-toggle running';
    }
    
    // Ativar efeito de borda animada
    if (header) {
        header.classList.add('border-animation');
        if (isBreakTime) {
            header.classList.add('break');
        }
    }
    
    updateInterval = setInterval(function() {
        tick();
    }, 1000);
}

function stopTimer() {
    isRunning = false;
    var toggleBtn = document.getElementById('pomodoroToggle');
    var header = document.getElementById('pomodoroHeader');
    
    if (toggleBtn) {
        toggleBtn.textContent = 'Iniciar';
        toggleBtn.className = 'pomodoro-toggle';
    }
    
    // Desativar efeito de borda animada
    if (header) {
        header.classList.remove('border-animation', 'break');
    }
    
    if (updateInterval) {
        clearInterval(updateInterval);
        updateInterval = null;
    }
    saveState();
}

function tick() {
    if (currentTime > 0) {
        currentTime--;
        
        // APENAS ATUALIZAR CONTADORES SE NÃO FOR DESCANSO
        if (!isBreakTime) {
            currentSessionSeconds++;
            totalTodaySeconds++;
        }
    } else {
        handleTimerComplete();
    }
    updateDisplay();
}

function handleTimerComplete() {
    stopTimer();
    
    var header = document.getElementById('pomodoroHeader');
    
    if (!isBreakTime) {
        isBreakTime = true;
        currentTime = 15 * 60; // 15 minutos de descanso
        showNotification('Hora do descanso!', 'Tempo de foco completo! Descanse um pouco.');
        
        // Atualizar efeito de borda para modo break
        if (header) {
            header.classList.add('break');
        }
    } else {
        isBreakTime = false;
        currentTime = 60 * 60; // 60 minutos de foco
        showNotification('Volte ao trabalho!', 'Tempo de descanso terminou.');
        
        // Remover classe break do efeito de borda
        if (header) {
            header.classList.remove('break');
        }
    }
    
    updateDisplay();
    saveState();
    
    // Reiniciar automaticamente após 1 segundo
    setTimeout(function() {
        startTimer();
    }, 1000);
}

function updateDisplay() {
    var timerElement = document.getElementById('pomodoroTimer');
    var miniTimerElement = document.getElementById('pomodoroTimeMini');
    var sessionTypeElement = document.getElementById('pomodoroSessionType');
    var progressBar = document.getElementById('pomodoroProgressBar');
    var header = document.getElementById('pomodoroHeader');
    
    if (!timerElement || !miniTimerElement || !sessionTypeElement || !progressBar) return;
    
    var minutes = Math.floor(currentTime / 60);
    var seconds = currentTime % 60;
    var timeString = minutes + ':' + (seconds < 10 ? '0' : '') + seconds;
    
    timerElement.textContent = timeString;
    miniTimerElement.textContent = timeString;
    
    if (isBreakTime) {
        sessionTypeElement.textContent = 'Descanso';
        sessionTypeElement.className = 'pomodoro-session-type-badge break';
        timerElement.className = 'pomodoro-timer break';
        progressBar.className = 'pomodoro-progress-bar break';
        miniTimerElement.className = 'pomodoro-time-mini break';
        
        // Atualizar efeito de borda se estiver ativo
        if (header && header.classList.contains('border-animation')) {
            header.classList.add('break');
        }
    } else {
        sessionTypeElement.textContent = 'Foco';
        sessionTypeElement.className = 'pomodoro-session-type-badge';
        timerElement.className = 'pomodoro-timer';
        progressBar.className = 'pomodoro-progress-bar';
        miniTimerElement.className = 'pomodoro-time-mini';
        
        // Remover classe break do efeito de borda se estiver ativo
        if (header && header.classList.contains('border-animation')) {
            header.classList.remove('break');
        }
    }
    
    var totalTime = isBreakTime ? (15 * 60) : (60 * 60);
    var progress = ((totalTime - currentTime) / totalTime) * 100;
    progressBar.style.width = progress + '%';
    
    var currentSessionElement = document.getElementById('currentSession');
    var totalTodayElement = document.getElementById('totalToday');
    
    if (currentSessionElement) currentSessionElement.textContent = formatTime(currentSessionSeconds);
    if (totalTodayElement) totalTodayElement.textContent = formatTime(totalTodaySeconds);
}

function formatTime(seconds) {
    var hrs = Math.floor(seconds / 3600);
    var mins = Math.floor((seconds % 3600) / 60);
    var secs = seconds % 60;
    return (hrs < 10 ? '0' : '') + hrs + ':' + (mins < 10 ? '0' : '') + mins + ':' + (secs < 10 ? '0' : '') + secs;
}

function showNotification(title, message) {
    // Para WebBrowser, usar alert simples
    alert(title + ': ' + message);
}

function toggleMinimize() {
    var content = document.getElementById('pomodoroContent');
    var minimizeBtn = document.getElementById('pomodoroMinimize');
    
    if (content && minimizeBtn) {
        if (content.style.display === 'none') {
            content.style.display = 'flex';
            minimizeBtn.textContent = '▼';
        } else {
            content.style.display = 'none';
            minimizeBtn.textContent = '▶';
        }
    }
}

function saveState() {
    var state = {
        currentTime: currentTime,
        isBreakTime: isBreakTime,
        isRunning: isRunning,
        currentSessionSeconds: currentSessionSeconds,
        totalTodaySeconds: totalTodaySeconds
    };
    
    try {
        localStorage.setItem('pomodoroWidgetState', JSON.stringify(state));
    } catch(e) {
        console.log('Erro ao salvar estado: ' + e.message);
    }
}

function loadState() {
    try {
        var saved = localStorage.getItem('pomodoroWidgetState');
        if (saved) {
            var state = JSON.parse(saved);
            currentTime = state.currentTime || (60 * 60);
            isBreakTime = state.isBreakTime || false;
            currentSessionSeconds = state.currentSessionSeconds || 0;
            totalTodaySeconds = state.totalTodaySeconds || 0;
            
            if (state.isRunning) {
                startTimer();
            }
            
            updateDisplay();
        }
    } catch(e) {
        console.log('Erro ao carregar estado: ' + e.message);
    }
}

// Nova função para verificar se está em modo de foco (não descanso)
function isInFocusMode() {
    return !isBreakTime;
}

// Função para expor currentSessionSeconds para o C#
function getCurrentSessionSeconds() {
    return currentSessionSeconds;
}

// Função para obter se está em modo de foco
function getIsInFocusMode() {
    return !isBreakTime;
}

// Função para definir a tarefa estudando (chamada pelo C#)
function setStudyingTask(taskName) {
    var taskTitle = document.getElementById('pomodoroTaskTitle');
    var taskInfo = document.getElementById('taskInfo');
    
    if (taskTitle) {
        taskTitle.textContent = taskName;
    }
    if (taskInfo) {
        taskInfo.textContent = 'Tarefa atual: ' + taskName;
    }
    
    // Resetar contadores quando mudar de tarefa
    currentSessionSeconds = 0;
    updateDisplay();
}

// Prevenir comportamento padrão de arrasto em botões
document.addEventListener('DOMContentLoaded', function() {
    var buttons = document.querySelectorAll('button');
    buttons.forEach(function(button) {
        button.addEventListener('mousedown', function(e) {
            e.stopPropagation();
        });
        button.addEventListener('click', function(e) {
            e.stopPropagation();
        });
    });
});";
        }

        [ComVisible(true)]
        public class ObjectForScripting
        {
            private Form1 form;

            public ObjectForScripting(Form1 form)
            {
                this.form = form;
            }

            public void CloseApp()
            {
                form.Invoke(new Action(() => form.Close()));
            }

            public void ToggleTimer()
            {
                form.Invoke(new Action(() => {
                    form.ToggleTimer();
                }));
            }

            public void MinimizeWidget()
            {
                form.Invoke(new Action(() => {
                    form.MinimizeWidget();
                }));
            }

            public void MaximizeWidget()
            {
                form.Invoke(new Action(() => {
                    form.MaximizeWidget();
                }));
            }

            public void SetStudyingTask(int taskId, string taskName)
            {
                form.Invoke(new Action(() => {
                    form.SetStudyingTask(taskId, taskName);
                }));
            }
        }

        // Métodos públicos que podem ser chamados do JavaScript
        public void ToggleTimer()
        {
            Log("ToggleTimer chamado");
            if (webBrowser1.Document != null)
            {
                webBrowser1.Document.InvokeScript("toggleTimer");
            }
        }

        public void MinimizeWidget()
        {
            Log("MinimizeWidget chamado");
            // Modo compacto/expandido alternado
            if (this.Height > 100)
            {
                // Modo compacto
                this.Height = 57;
                this.Width = 350;

                // Esconder conteúdo, mostrar apenas header
                if (webBrowser1.Document != null)
                {
                    webBrowser1.Document.InvokeScript("eval", new object[] { @"
                        var content = document.getElementById('pomodoroContent');
                        if (content) content.style.display = 'none';
                    " });
                }
            }
            else
            {
                // Modo expandido
                this.Height = 300;
                this.Width = 350;

                // Mostrar conteúdo
                if (webBrowser1.Document != null)
                {
                    webBrowser1.Document.InvokeScript("eval", new object[] { @"
                        var content = document.getElementById('pomodoroContent');
                        if (content) content.style.display = 'flex';
                    " });
                }
            }

            // Reaplicar cantos arredondados
            SetRoundRegion(22);
        }

        public void MaximizeWidget()
        {
            Log("MaximizeWidget chamado");
            if (this.WindowState == FormWindowState.Maximized)
            {
                this.WindowState = FormWindowState.Normal;
                this.Size = new Size(350, 500);
                // Reposicionar
                this.Location = new Point(
                    Screen.PrimaryScreen.WorkingArea.Width - this.Width - 20,
                    Screen.PrimaryScreen.WorkingArea.Height - this.Height - 20
                );
            }
            else
            {
                this.WindowState = FormWindowState.Maximized;
            }
            // Reaplicar cantos arredondados
            SetRoundRegion(22);
        }

        public void SetStudyingTask(int taskId, string taskName)
        {
            currentStudyingTaskId = taskId;
            currentStudyingTaskName = taskName;

            // Atualizar título do widget
            UpdateTaskTitle(taskName);

            // Resetar contadores
            lastSavedSeconds = 0;
            totalSecondsAdded = 0;

            Log($"Tarefa estudando definida: {taskName} (ID: {taskId})");
        }

        private void SetupSyncTimer()
        {
            syncTimer = new Timer();
            syncTimer.Interval = 15000; // AUMENTADO para 15 segundos (era 3 segundos)
            syncTimer.Tick += SyncTimer_Tick;
            syncTimer.Start();
        }

        private void SetupSecondsUpdateTimer()
        {
            secondsUpdateTimer = new Timer();
            secondsUpdateTimer.Interval = 1000; // 1 segundo
            secondsUpdateTimer.Tick += SecondsUpdateTimer_Tick;
            secondsUpdateTimer.Start();
        }

        private void SyncTimer_Tick(object sender, EventArgs e)
        {
            CheckForStudyingTaskUpdates();
        }

        private void SecondsUpdateTimer_Tick(object sender, EventArgs e)
        {
            UpdateCurrentTaskStudyTime();
        }



        private List<string> BuscarUrlsDaSessao(int sessionId)
        {
            List<string> urls = new List<string>();

            try
            {
                using (var client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;

                    // Fazer requisição para o save_session.php
                    string requestUrl = $"https://sistemadetarefas.online/save_session.php?action=get_session&id={sessionId}";
                    string jsonResponse = client.DownloadString(requestUrl);

                    var response = Newtonsoft.Json.JsonConvert.DeserializeObject<SessionResponse>(jsonResponse);

                    if (response.success && response.urls != null)
                    {
                        urls = response.urls.Select(u => u.url).ToList();
                        Log($"Encontradas {urls.Count} URLs para sessão {sessionId}");
                    }
                    else
                    {
                        Log($"Nenhuma URL encontrada para sessão {sessionId}: {response.message}");
                    }

                    LimparSessionId();

                }
            }
            catch (WebException webEx)
            {
                Log($"Erro de conexão ao buscar URLs: {webEx.Message}");
            }
            catch (Exception ex)
            {
                Log($"Erro ao buscar URLs da sessão {sessionId}: {ex.Message}");
            }

            return urls;
        }

        // Classes para desserializar a resposta do save_session.php
        public class SessionResponse
        {
            public bool success { get; set; }
            public string message { get; set; }
            // public SessionData session { get; set; }
            public List<
                UrlData> urls
            { get; set; }
        }





        public class UrlData
        {
            public string url { get; set; }
            public string title { get; set; }
            public string favicon_url { get; set; }
            public int url_order { get; set; }
        }

        private void LimparSessionId()
        {
            try
            {
                using (var client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    client.Headers[HttpRequestHeader.ContentType] = "application/json";

                    // Atualizar diretamente o session_id para NULL via SQL
                    var data = new
                    {
                        action = "clear_session_id"
                    };

                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(data);
                    string response = client.UploadString("https://sistemadetarefas.online/pomodoro_sync.php", "POST", json);

                    Log("Session_id definido como NULL");
                }
            }
            catch (Exception ex)
            {
                Log($"Erro ao limpar session_id: {ex.Message}");
            }
        }





        // Verificar atualizações da tarefa estudando
        // Modifique o método CheckForStudyingTaskUpdates:
        private void CheckForStudyingTaskUpdates()
        {
            // Verificar se passou tempo suficiente desde a última verificação
            if (DateTime.Now - lastSyncCheckTime < syncCheckInterval && lastSyncCheckTime != DateTime.MinValue)
            {
                return;
            }

            lastSyncCheckTime = DateTime.Now;

            try
            {
                using (var client = new WebClient())
                {
                    // Adicionar timeout para evitar requisições longas
                    client.Encoding = Encoding.UTF8;
                    client.Headers[HttpRequestHeader.UserAgent] = "PomodoroWidget/1.0";

                    string jsonData = client.DownloadString("https://sistemadetarefas.online/pomodoro_sync.php");

                    var response = Newtonsoft.Json.JsonConvert.DeserializeObject<PomodoroSyncResponse>(jsonData);

                    if (response.success && response.data != null)
                    {
                        int? studyingTaskId = response.data.studyingTaskId;
                        string taskName = response.data.taskName;
                        string source = response.data.source;
                        int? session_id = response.data.sessionId;

                        // Buscar URLs se tiver session_id (apenas se não tiver buscado recentemente)
                        if (session_id.HasValue && ShouldProcessSession(session_id.Value))
                        {
                            List<string> urls = BuscarUrlsDaSessao(session_id.Value);

                            if (urls.Count > 0)
                            {
                                Log($"URLs encontradas: {string.Join(", ", urls)}");

                                // Adicionar delay entre abertura de URLs
                                Task.Run(async () =>
                                {
                                    Console.WriteLine("=== INICIANDO ABERTURA DE URLs ===");
                                    for (int i = 0; i < urls.Count; i++)
                                    {
                                        Console.WriteLine($"[{i + 1}/{urls.Count}] Tentando abrir: '{urls[i]}'");
                                        OpenLink(urls[i]);

                                        // Adicionar delay entre abertura de cada URL
                                        if (i < urls.Count - 1)
                                        {
                                            await Task.Delay(1000); // 1 segundo entre URLs
                                        }
                                    }
                                    Console.WriteLine("=== TODAS URLs PROCESSADAS ===");
                                });
                            }
                        }

                        // Só sincronizar se veio do site
                        if (source == "website")
                        {
                            UpdateFromDatabase(studyingTaskId, taskName);
                        }
                    }
                }
            }
            catch (WebException webEx)
            {
                Log($"Erro de conexão ao verificar atualizações: {webEx.Message}");

                // Em caso de erro, aumentar o intervalo temporariamente
                syncTimer.Interval = Math.Min(syncTimer.Interval * 2, 60000); // Máximo 60 segundos
            }
            catch (Exception ex)
            {
                Log($"Erro ao verificar atualizações: {ex.Message}");
            }
        }

        // Adicione este método para controlar processamento de sessões:
        private Dictionary<int, DateTime> processedSessions = new Dictionary<int, DateTime>();
        private readonly TimeSpan sessionProcessCooldown = TimeSpan.FromMinutes(5);

        private bool ShouldProcessSession(int sessionId)
        {
            // Verificar se já processamos esta sessão recentemente
            if (processedSessions.ContainsKey(sessionId))
            {
                if (DateTime.Now - processedSessions[sessionId] < sessionProcessCooldown)
                {
                    return false; // Sessão processada recentemente
                }
            }

            processedSessions[sessionId] = DateTime.Now;

            // Limpar sessões antigas do cache
            var oldSessions = processedSessions.Where(kvp => DateTime.Now - kvp.Value > TimeSpan.FromHours(1)).ToList();
            foreach (var oldSession in oldSessions)
            {
                processedSessions.Remove(oldSession.Key);
            }

            return true;
        }

        private void OpenLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao abrir link: {url}\n{ex.Message}");
            }

        }


        private void ProcessarUrls(List<string> urls)
        {
            // Aqui você pode processar as URLs como necessário
            // Por exemplo, exibir no widget, carregar no WebBrowser, etc.

            foreach (string url in urls)
            {
                Log($"URL da sessão: {url}");
            }

            // Exemplo: carregar a primeira URL no WebBrowser (se aplicável)
            if (urls.Count > 0 && webBrowser1 != null && isWebBrowserReady)
            {
                // webBrowser1.Navigate(urls[0]); // Descomente se quiser navegar
            }
        }


        // Atualizar a partir do banco de dados
        private void UpdateFromDatabase(int? studyingTaskId, string taskNameFromSync)
        {
            // Se studyingTaskId for null, significa que pararam de estudar
            if (!studyingTaskId.HasValue)
            {
                if (currentStudyingTaskId.HasValue)
                {
                    Log("Site parou de estudar");
                    SaveAccumulatedTime(); // Salvar tempo acumulado antes de limpar
                    ClearStudyingTask();
                }
                return;
            }

            // Se é uma tarefa diferente da atual
            if (studyingTaskId.Value != currentStudyingTaskId)
            {
                Log($"Site marcou tarefa como estudando: {studyingTaskId.Value} - {taskNameFromSync}");

                // Salvar tempo acumulado da tarefa anterior, se houver
                if (currentStudyingTaskId.HasValue && accumulatedSeconds > 0)
                {
                    UpdateTaskStudyTime(currentStudyingTaskId.Value, accumulatedSeconds, true);
                    accumulatedSeconds = 0;
                }

                // Usar o nome da tarefa que veio da tabela pomodoro_sync
                SetStudyingTask(studyingTaskId.Value, taskNameFromSync);

                // Resetar contadores quando mudar de tarefa
                lastSavedSeconds = 0;
                totalSecondsAdded = 0;
                accumulatedSeconds = 0;
            }
        }

        // Atualizar o tempo da tarefa atual a cada segundo
        // Modifique o método UpdateCurrentTaskStudyTime:
        private void UpdateCurrentTaskStudyTime()
        {
            if (currentStudyingTaskId.HasValue && isWebBrowserReady)
            {
                try
                {
                    // VERIFICAR SE ESTÁ EM MODO DE FOCO (NÃO DESCANSO)
                    object isInFocusModeResult = webBrowser1.Document.InvokeScript("eval",
                        new object[] { "getIsInFocusMode();" });

                    if (isInFocusModeResult != null && bool.TryParse(isInFocusModeResult.ToString(), out bool isInFocusMode))
                    {
                        if (!isInFocusMode)
                        {
                            // Se não estiver em modo de foco (está em descanso), não atualizar
                            return;
                        }
                    }

                    // Buscar o tempo atual da sessão do JavaScript
                    object result = webBrowser1.Document.InvokeScript("eval",
                        new object[] { "getCurrentSessionSeconds();" });

                    if (result != null && int.TryParse(result.ToString(), out int currentSeconds))
                    {
                        // Calcular a diferença desde a última atualização
                        int secondsToAdd = currentSeconds - lastSavedSeconds;

                        if (secondsToAdd > 0)
                        {
                            // Acumular segundos em vez de enviar imediatamente
                            accumulatedSeconds += secondsToAdd;
                            lastSavedSeconds = currentSeconds;
                            totalSecondsAdded += secondsToAdd;

                            // Verificar se é hora de enviar para o servidor
                            if (DateTime.Now - lastUpdateTime >= updateInterval || accumulatedSeconds >= 60)
                            {
                                SaveAccumulatedTime();
                            }

                            // Log a cada 60 segundos
                            if (currentSeconds % 60 == 0)
                            {
                                Log($"Tempo acumulado para tarefa {currentStudyingTaskId}: {currentSeconds} segundos totais, {accumulatedSeconds} não enviados");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Erro ao buscar tempo da sessão: {ex.Message}");
                }
            }
        }

        // Adicione este método para salvar tempo acumulado:
        private void SaveAccumulatedTime()
        {
            if (currentStudyingTaskId.HasValue && accumulatedSeconds > 0)
            {
                UpdateTaskStudyTime(currentStudyingTaskId.Value, accumulatedSeconds, false);
                lastUpdateTime = DateTime.Now;
                accumulatedSeconds = 0;
            }
        }
        // Atualizar segundos da tarefa no banco de dados
        // Modifique o método UpdateTaskStudyTime:
        private void UpdateTaskStudyTime(int taskId, int secondsToAdd, bool forceLog = false)
        {
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.ContentType] = "application/json";
                    client.Headers[HttpRequestHeader.UserAgent] = "PomodoroWidget/1.0";

                    // Adicionar um timestamp único para evitar cache
                    string timestamp = DateTime.Now.Ticks.ToString();

                    var data = new
                    {
                        action = "update_tarefa_segundos",
                        id = taskId,
                        segundos = secondsToAdd,
                        timestamp = timestamp // Evitar cache do servidor
                    };

                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(data);

                    // Usar UploadStringAsync para não bloquear a thread principal
                    client.UploadStringAsync(new Uri("https://sistemadetarefas.online/tarefas.php"), "POST", json);

                    // Log apenas se forçado ou se for uma quantidade significativa de tempo
                    if (forceLog || secondsToAdd >= 60)
                    {
                        Log($"Tempo enviado para tarefa {taskId}: +{secondsToAdd} segundos (enviado em lote)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Erro ao atualizar tempo da tarefa: {ex.Message}");
                // Em caso de erro, manter o tempo acumulado para tentar novamente depois
                accumulatedSeconds += secondsToAdd;
            }
        }
        // Atualizar título da tarefa no widget
        private void UpdateTaskTitle(string taskName)
        {
            if (webBrowser1.Document != null && isWebBrowserReady)
            {
                try
                {
                    webBrowser1.Document.InvokeScript("setStudyingTask", new object[] { taskName });
                }
                catch (Exception ex)
                {
                    Log($"Erro ao atualizar título: {ex.Message}");
                }
            }
        }

        // Limpar tarefa estudando
        private void ClearStudyingTask()
        {
            currentStudyingTaskId = null;
            currentStudyingTaskName = "Nenhuma tarefa";
            lastSavedSeconds = 0;
            totalSecondsAdded = 0;

            // Atualizar título do widget
            UpdateTaskTitle("Pomodoro Widget");

            Log("Tarefa estudando limpa");
        }

        // Classes para desserialização do JSON
        public class PomodoroSyncResponse
        {
            public bool success { get; set; }
            public PomodoroData data { get; set; }
        }

        public class PomodoroData
        {
            public int? studyingTaskId { get; set; }
            public string taskName { get; set; }
            public string source { get; set; }

            // CORREÇÃO: Use sessionId (com I maiúsculo) para combinar com o JSON
            public int? sessionId { get; set; }

            // Mantenha as outras propriedades para compatibilidade
            public List<string> urls { get; set; }
            public List<string> sessionUrls { get; set; }
            public string baseUrl { get; set; }
        }


        private void Log(string message)
        {
            try
            {
                string logFile = Path.Combine(appDataPath, "pomodoro_correct_buttons.log");
                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";
                File.AppendAllText(logFile, logEntry);

                Debug.WriteLine(message);
            }
            catch
            {
                // Ignora erros de log
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveAccumulatedTime();

            Log("Pomodoro Widget fechado");
            syncTimer?.Stop();
            secondsUpdateTimer?.Stop();
            base.OnFormClosing(e);
        }

        // Adicionar sombra à janela
        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x20000;
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }




    }
}