using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using Zharp.Internal;
using Zharp.Internal.Apps;

namespace Zharp.Windows
{
    /// <summary>
    /// Windows Forms:
    /// <list type="bullet">
    /// <item>An exception on the UI thread goes to <c>Application.ThreadException</c>: reported as
    /// unhandled (written to disk first), then what Windows Forms does itself — its dialog, and
    /// "Quit" ends the app. Windows Forms keeps one handler per thread: an app that sets its own
    /// after <c>ZharpApp.Start</c> replaces this one (call <c>ZharpSdk.CaptureException(e.Exception,
    /// handled: false)</c> in it). Exceptions on other threads, and on the UI thread with
    /// <c>UnhandledExceptionMode.ThrowException</c>, end the app: the AppDomain's handler reports them.</item>
    /// <item>Each form is a screen when it's active; no active form is the background.</item>
    /// </list>
    /// </summary>
    internal sealed class FormsHooks
    {
        readonly AppRuntime app;
        SynchronizationContext? ui;
        Form? lastActive;
        bool background;
        readonly System.Collections.Generic.HashSet<Form> watched = new System.Collections.Generic.HashSet<Form>();

        FormsHooks(AppRuntime app)
        {
            this.app = app;
        }

        /// <summary>On the UI thread, before Application.Run (ThreadException is per thread).</summary>
        public static FormsHooks Install(AppRuntime app)
        {
            var hooks = new FormsHooks(app);
            hooks.ui = SynchronizationContext.Current as WindowsFormsSynchronizationContext;
            Application.ThreadException += hooks.OnThreadException;
            Application.Idle += hooks.OnIdle;
            Application.ApplicationExit += (_, _) => ZharpCore.Current?.Flush(5000);
            return hooks;
        }

        public bool Post(Action action)
        {
            SynchronizationContext? context = ui;
            if (context == null) return false;
            context.Post(_ => action(), null);
            return true;
        }

        void OnIdle(object? sender, EventArgs e)
        {
            // The message loop runs: its context is what the hang watchdog posts to.
            if (ui == null) ui = SynchronizationContext.Current;
            Form? active = Form.ActiveForm;
            if (active != null && !watched.Contains(active)) Watch(active);
            if (active != null && active != lastActive)
            {
                lastActive = active;
                app.ScreenShown(active.GetType().Name, "form");
            }
        }

        void Watch(Form form)
        {
            watched.Add(form);
            form.Activated += (_, _) =>
            {
                if (background)
                {
                    background = false;
                    app.OnForeground();
                }
                lastActive = form;
                app.ScreenShown(form.GetType().Name, "form");
            };
            form.Deactivate += (_, _) =>
            {
                // Another of the app's forms may be activated next: the background is no form at all.
                Post(() =>
                {
                    if (Form.ActiveForm == null && !background)
                    {
                        background = true;
                        app.OnBackground();
                    }
                });
            };
            form.FormClosed += (_, _) => watched.Remove(form);
        }

        void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Exception error = e.Exception;
            app.ReportCrash(error, "winforms", terminating: false);
            // What Windows Forms does when no handler is set.
            if (AppContext.TryGetSwitch("System.Windows.Forms.DoNotCatchUnhandledExceptions", out bool rethrow) && rethrow)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }
            if (!SystemInformation.UserInteractive) return;
            DialogResult result;
            using (var dialog = new ThreadExceptionDialog(error)) result = dialog.ShowDialog();
            if (result == DialogResult.Abort)
            {
                Application.Exit();
                Environment.Exit(0);
            }
        }
    }
}
