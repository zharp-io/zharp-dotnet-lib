using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Zharp.Internal;
using Zharp.Internal.Apps;

namespace Zharp.Windows
{
    /// <summary>
    /// WPF:
    /// <list type="bullet">
    /// <item>An exception the dispatcher didn't get handled ends the app: it leaves <c>Application.Run</c>
    /// and is reported, written to disk first, by the AppDomain's handler as the crash it is. One an
    /// app's <c>DispatcherUnhandledException</c> handler handled (<c>e.Handled = true</c>) is reported
    /// as caught, once the dispatcher is back.</item>
    /// <item>Each window, and each page in a navigation window or frame, is a screen.</item>
    /// <item>The app deactivated is the background, for sessions.</item>
    /// </list>
    /// </summary>
    internal sealed class WpfHooks
    {
        readonly AppRuntime app;
        readonly Dispatcher dispatcher;

        WpfHooks(AppRuntime app, Dispatcher dispatcher)
        {
            this.app = app;
            this.dispatcher = dispatcher;
        }

        public static WpfHooks? TryInstall(AppRuntime app)
        {
            Application? current = Application.Current;
            if (current == null) return null;
            var hooks = new WpfHooks(app, current.Dispatcher);
            if (current.Dispatcher.CheckAccess()) hooks.Install(current);
            else current.Dispatcher.Invoke(() => hooks.Install(current));
            return hooks;
        }

        void Install(Application application)
        {
            application.DispatcherUnhandledException += OnDispatcherException;
            application.Activated += (_, _) => app.OnForeground();
            application.Deactivated += (_, _) => app.OnBackground();
            application.Exit += (_, _) => ZharpCore.Current?.Flush(5000);
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
            EventManager.RegisterClassHandler(typeof(Page), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnPageLoaded));
            // A window already up (Start called after the main window was made).
            foreach (Window window in application.Windows) Watch(window);
            Window? active = application.MainWindow;
            if (active != null && active.IsLoaded) app.ScreenShown(active.GetType().Name, "window");
        }

        public bool Post(Action action)
        {
            if (dispatcher.HasShutdownStarted) return false;
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
            return true;
        }

        void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Exception error = e.Exception;
            // Handlers after this one decide: unhandled, it ends the app and the AppDomain reports
            // the crash; handled, the dispatcher runs this next.
            dispatcher.BeginInvoke(new Action(() =>
            {
                ZharpCore? core = ZharpCore.Current;
                if (core == null || core.Errors.IsReported(error)) return;
                core.Report(error, handled: true, mechanism: "wpf", span: null);
            }), DispatcherPriority.Background);
        }

        void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window window)
            {
                Watch(window);
                if (window.IsActive) app.ScreenShown(window.GetType().Name, "window");
            }
        }

        void Watch(Window window)
        {
            window.Activated -= OnWindowActivated;
            window.Activated += OnWindowActivated;
        }

        void OnWindowActivated(object? sender, EventArgs e)
        {
            if (sender is Window window) app.ScreenShown(ScreenOf(window), "window");
        }

        /// <summary>A navigation window shows its page; any other window is itself.</summary>
        static string ScreenOf(Window window)
        {
            if (window is System.Windows.Navigation.NavigationWindow nav && nav.Content is Page page) return page.GetType().Name;
            return window.GetType().Name;
        }

        void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Page page) app.ScreenShown(page.GetType().Name, "page");
        }
    }
}
