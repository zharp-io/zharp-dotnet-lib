using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;
using Microsoft.Maui.LifecycleEvents;
using Microsoft.Maui.Storage;
using Zharp;
using Zharp.Internal;
using Zharp.Internal.Apps;

namespace Microsoft.Maui.Hosting
{
    /// <summary><c>builder.UseZharp()</c> in <c>MauiProgram</c>.</summary>
    public static class ZharpMauiAppBuilderExtensions
    {
        /// <summary>
        /// Start Zharp for this app, on Android, iOS, Mac Catalyst and Windows: crashes (saved to disk
        /// and sent at once or on the next launch), the UI thread blocked for 5 s, pages as screens,
        /// sessions (a new one after 30 minutes in the background) and a device ID, and an offline
        /// queue.
        /// <code>builder.UseMauiApp&lt;App&gt;().UseZharp(o => o.Key = "zpk_…");</code>
        /// </summary>
        public static MauiAppBuilder UseZharp(this MauiAppBuilder builder, Action<ZharpOptions>? configure = null, Action<ZharpAppOptions>? app = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            var options = new ZharpOptions();
            configure?.Invoke(options);
            var appOptions = new ZharpAppOptions();
            app?.Invoke(appOptions);
            var host = new Zharp.Maui.MauiHost();
            ZharpCore? core = ZharpSdk.StartApp(options, host, appOptions);
            if (core?.App == null) return builder;
            builder.ConfigureLifecycleEvents(events => host.Lifecycle(events));
            return builder;
        }
    }
}

namespace Zharp.Maui
{
    /// <summary>MAUI: the app's storage, device, UI thread, crash events, pages and lifecycle.</summary>
    internal sealed class MauiHost : AppHost
    {
        AppRuntime? app;
        bool pagesHooked;

        public override string StorageDirectory => Path.Combine(FileSystem.AppDataDirectory, "zharp");

        public override bool DefaultsFirst => true;

        public override bool SuspendsInBackground => DeviceInfo.Platform != DevicePlatform.WinUI;

        /// <summary>The bundle ID / package name, as the iOS and Android SDKs name the service.</summary>
        public override string? DefaultService
        {
            get
            {
                try { return string.IsNullOrEmpty(AppInfo.PackageName) ? null : AppInfo.PackageName; }
                catch { return null; }
            }
        }

        /// <summary>"1.4.0 (212)": the app's version and build, as the iOS SDK writes it.</summary>
        public override string? DefaultRelease
        {
            get
            {
                try
                {
                    string version = AppInfo.VersionString, build = AppInfo.BuildString;
                    if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(build) && version != build) return version + " (" + build + ")";
                    return string.IsNullOrEmpty(version) ? (string.IsNullOrEmpty(build) ? null : build) : version;
                }
                catch
                {
                    return null;
                }
            }
        }

        public override IEnumerable<KeyValuePair<string, object>> ResourceAttributes()
        {
            var list = new List<KeyValuePair<string, object>>();
            void Add(string key, string? value)
            {
                if (!string.IsNullOrEmpty(value)) list.Add(new KeyValuePair<string, object>(key, value!));
            }
            try
            {
                DevicePlatform platform = DeviceInfo.Platform;
                if (platform == DevicePlatform.Android) { Add("os.type", "linux"); Add("os.name", "Android"); }
                else if (platform == DevicePlatform.iOS) { Add("os.type", "darwin"); Add("os.name", "iOS"); }
                else if (platform == DevicePlatform.MacCatalyst) { Add("os.type", "darwin"); Add("os.name", "macOS"); }
                else if (platform == DevicePlatform.WinUI) { Add("os.type", "windows"); Add("os.name", "Windows"); }
                else Add("os.name", platform.ToString());
                Add("os.version", DeviceInfo.VersionString);
                Add("device.model.identifier", DeviceInfo.Model);
                Add("device.model.name", DeviceInfo.Model);
                Add("device.manufacturer", DeviceInfo.Manufacturer);
                Add("app.id", AppInfo.PackageName);
            }
            catch
            {
                // Device info not available yet.
            }
            return list;
        }

        public override bool PostToUi(Action action)
        {
            try
            {
                MainThread.BeginInvokeOnMainThread(action);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override void Install(AppRuntime app)
        {
            this.app = app;
#if ANDROID
            Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => app.ReportCrash(e.Exception, "android", terminating: !e.Handled);
#elif IOS || MACCATALYST
            ObjCRuntime.Runtime.MarshalManagedException += (_, e) => app.ReportCrash(e.Exception, "ios", terminating: true);
#elif WINDOWS
            HookWinUI(Microsoft.UI.Xaml.Application.Current);
#endif
        }

#if WINDOWS
        bool winuiHooked;

        /// <summary>WinUI unhandled exceptions; the Application may only exist once the app is launched.</summary>
        void HookWinUI(Microsoft.UI.Xaml.Application? winui)
        {
            if (winui == null || winuiHooked || app == null) return;
            winuiHooked = true;
            AppRuntime runtime = app;
            winui.UnhandledException += (_, e) => runtime.ReportCrash(e.Exception, "winui", terminating: !e.Handled);
        }
#endif

        /// <summary>Foreground and background, for sessions; the first time, the app's pages too.</summary>
        public void Lifecycle(ILifecycleBuilder events)
        {
#if ANDROID
            events.AddAndroid(android => android
                .OnResume(_ => Foreground())
                .OnStop(_ => Background()));
#elif IOS || MACCATALYST
            events.AddiOS(ios => ios
                .OnActivated(_ => Foreground())
                .DidEnterBackground(_ => Background()));
#elif WINDOWS
            events.AddWindows(windows => windows
                .OnLaunching((winui, _) => HookWinUI(winui))
                .OnLaunched((winui, _) => HookWinUI(winui))
                .OnActivated((_, args) =>
                {
                    if (args.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated) Background();
                    else Foreground();
                }));
#endif
        }

        void Foreground()
        {
            HookPages();
            app?.OnForeground();
        }

        void Background() => app?.OnBackground();

        /// <summary>Once the app's Application exists (after the builder): each page that appears is a screen.</summary>
        void HookPages()
        {
            if (pagesHooked || app == null) return;
            Application? application = Application.Current;
            if (application == null) return;
            pagesHooked = true;
            application.PageAppearing += (_, page) => Shown(page);
            try
            {
                foreach (Window window in application.Windows)
                {
                    Page? current = Visible(window.Page);
                    if (current != null) Shown(current);
                }
            }
            catch
            {
                // The first page is reported when it appears.
            }
        }

        void Shown(Page page)
        {
            // Containers aren't screens: what they show is.
            if (page is Shell || page is NavigationPage || page is TabbedPage || page is FlyoutPage) return;
            app?.ScreenShown(page.GetType().Name, "page");
        }

        /// <summary>The page on screen inside a container.</summary>
        static Page? Visible(Page? page)
        {
            for (int i = 0; i < 10 && page != null; i++)
            {
                Page? next = page switch
                {
                    Shell shell => shell.CurrentPage,
                    NavigationPage nav => nav.CurrentPage,
                    TabbedPage tabs => tabs.CurrentPage,
                    FlyoutPage flyout => flyout.Detail,
                    _ => null,
                };
                if (next == null || next == page) return page;
                page = next;
            }
            return page;
        }
    }
}
