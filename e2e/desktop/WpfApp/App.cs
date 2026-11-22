using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Zharp;

namespace Shop.Desktop
{
    /// <summary>
    /// Run with one of: error (a caught error), handled (an exception the app's dispatcher handler
    /// handles), crash (an exception on the UI thread nobody handles), hang (the UI thread blocked),
    /// quit (start, then quit: what earlier runs left is sent).
    /// </summary>
    public class App : Application
    {
        readonly string mode;

        App(string mode) { this.mode = mode; }

        [STAThread]
        static void Main(string[] args) => new App(args.Length > 0 ? args[0] : "quit").Run();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ZharpApp.Start(o => { o.Debug = true; o.Environment = "e2e"; }, a => a.HangThreshold = TimeSpan.FromSeconds(2));
            if (mode == "handled") DispatcherUnhandledException += (_, args) => args.Handled = true;
            var window = new MainWindow();
            window.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Run(mode);
            };
            timer.Start();
        }

        void Run(string what)
        {
            switch (what)
            {
                case "error":
                    try { Checkout.Pay(Array.Empty<int>()); }
                    catch (Exception error) { ZharpSdk.CaptureException(error); }
                    Later(3, Shutdown);
                    break;
                case "handled":
                    Later(4, Shutdown);
                    Checkout.Pay(Array.Empty<int>());
                    break;
                case "crash":
                    Checkout.Pay(Array.Empty<int>());
                    break;
                case "hang":
                    Thread.Sleep(5000);
                    Later(4, Shutdown);
                    break;
                default:
                    Later(4, Shutdown);
                    break;
            }
        }

        void Later(double seconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); action(); };
            timer.Start();
        }
    }

    public class MainWindow : Window
    {
        public MainWindow()
        {
            Title = "Shop";
            Width = 400;
            Height = 200;
            Content = new TextBlock { Text = "Shop", Margin = new Thickness(20) };
        }
    }
}
