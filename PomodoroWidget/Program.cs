using System;
using System.IO;
using System.Windows.Forms;

namespace PomodoroWidget
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Captura exceções não tratadas no thread da UI
            Application.ThreadException += (s, e) =>
            {
                LogCrash("[THREAD_EXCEPTION] " + e.Exception.ToString());
            };

            // Captura exceções fatais em qualquer thread
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                LogCrash("[UNHANDLED] " + e.ExceptionObject.ToString());
            };

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }

        static void LogCrash(string msg)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PomodoroWidget", "pomodoro.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}