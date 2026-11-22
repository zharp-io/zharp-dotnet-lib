using System;
using System.Threading;
using System.Windows.Forms;
using Zharp;

namespace Shop.Desktop
{
    /// <summary>
    /// Run with one of: error (a caught error), ui-error (an exception on the UI thread: Windows
    /// Forms' ThreadException), crash (an exception on another thread, which ends the app), hang
    /// (the UI thread blocked), quit (start, then quit: what earlier runs left is sent).
    /// </summary>
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ZharpApp.Start(o => { o.Debug = true; o.Environment = "e2e"; }, a => a.HangThreshold = TimeSpan.FromSeconds(2));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Length > 0 ? args[0] : "quit"));
        }
    }

    sealed class MainForm : Form
    {
        readonly string mode;

        public MainForm(string mode)
        {
            this.mode = mode;
            Text = "Shop";
            Shown += (_, _) => Later(1.5, Run);
        }

        void Run()
        {
            switch (mode)
            {
                case "error":
                    try { Checkout.Pay(new int[0]); }
                    catch (Exception error) { ZharpSdk.CaptureException(error); }
                    Later(3, Close);
                    break;
                case "ui-error":
                    // Windows Forms shows its dialog for this (on an interactive desktop): the app
                    // is ended from another thread a few seconds on, whatever the dialog does.
                    new Thread(() => { Thread.Sleep(6000); Environment.Exit(0); }) { IsBackground = true }.Start();
                    Checkout.Pay(new int[0]);
                    break;
                case "crash":
                    new Thread(() => Checkout.Pay(new int[0])).Start();
                    break;
                case "hang":
                    Thread.Sleep(5000);
                    Later(4, Close);
                    break;
                default:
                    Later(4, Close);
                    break;
            }
        }

        void Later(double seconds, Action action)
        {
            var timer = new System.Windows.Forms.Timer { Interval = (int)(seconds * 1000) };
            timer.Tick += (_, _) => { timer.Stop(); action(); };
            timer.Start();
        }
    }
}
