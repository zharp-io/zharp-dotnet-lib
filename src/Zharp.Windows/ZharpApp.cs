using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Zharp.Internal;
using Zharp.Windows;

namespace Zharp
{
    /// <summary>
    /// Zharp for a WPF or Windows Forms app. First thing in <c>App.OnStartup</c> (WPF) or <c>Main</c>
    /// (Windows Forms):
    /// <code>ZharpApp.Start(o => o.Key = "zpk_…");</code>
    /// Crashes are saved to disk and sent at once or on the next launch; a UI thread blocked for 5 s
    /// is reported as a hang; windows, pages and forms are screens; sessions and a device ID go on
    /// everything; what can't be sent waits on disk.
    /// </summary>
    public static class ZharpApp
    {
        /// <summary>
        /// Start Zharp for this app. Settings left unset come from the environment (<c>ZHARP_KEY</c>, …).
        /// Dispose the result when the app ends to send what's waiting (it's sent on exit anyway).
        /// </summary>
        public static IDisposable Start(Action<ZharpOptions>? configure = null) => Start(configure, null);

        /// <summary>As <see cref="Start(Action{ZharpOptions})"/>, with app settings: how long a UI freeze must last to be a hang.</summary>
        public static IDisposable Start(Action<ZharpOptions>? configure, Action<ZharpAppOptions>? app)
        {
            var options = new ZharpOptions();
            configure?.Invoke(options);
            var appOptions = new ZharpAppOptions();
            app?.Invoke(appOptions);
            ZharpCore? core = ZharpSdk.StartApp(options, new WindowsHost(), appOptions);
            return new Handle(core);
        }

        /// <summary>Tell Zharp which screen is showing, for screens it can't see (a view swapped inside one window).</summary>
        public static void SetScreen(string name) => ZharpCore.Current?.App?.ScreenShown(name, "view");

        sealed class Handle : IDisposable
        {
            readonly ZharpCore? core;
            public Handle(ZharpCore? core) { this.core = core; }
            public void Dispose() => core?.Flush(5000);
        }
    }
}

namespace Zharp.Windows
{
    using Zharp.Internal.Apps;

    /// <summary>WPF and Windows Forms: whichever the app uses (both, for a Forms app hosting WPF).</summary>
    internal sealed class WindowsHost : AppHost
    {
        WpfHooks? wpf;
        FormsHooks? forms;

        public override string StorageDirectory
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
                return Path.Combine(root, "Zharp", Safe(AppName()));
            }
        }

        public override string? DefaultService => AppName();

        static string AppName()
        {
            try
            {
                string? name = Assembly.GetEntryAssembly()?.GetName().Name;
                if (!string.IsNullOrEmpty(name)) return name!;
                return Process.GetCurrentProcess().ProcessName;
            }
            catch
            {
                return "windows-app";
            }
        }

        static string Safe(string name) => new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        public override IEnumerable<KeyValuePair<string, object>> ResourceAttributes()
        {
            yield return new KeyValuePair<string, object>("os.type", "windows");
            yield return new KeyValuePair<string, object>("os.name", "Windows");
            yield return new KeyValuePair<string, object>("os.version", Environment.OSVersion.Version.ToString());
#if NET
            string? description = null;
            try { description = RuntimeInformation.OSDescription; } catch { /* unknown */ }
            if (!string.IsNullOrEmpty(description)) yield return new KeyValuePair<string, object>("os.description", description!);
            yield return new KeyValuePair<string, object>("host.arch", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
#else
            yield return new KeyValuePair<string, object>("os.description", Environment.OSVersion.ToString());
            yield return new KeyValuePair<string, object>("host.arch", Environment.Is64BitOperatingSystem ? "x64" : "x86");
#endif
            yield return new KeyValuePair<string, object>("device.model.name", "PC");
        }

        public override bool PostToUi(Action action)
        {
            if (wpf != null && wpf.Post(action)) return true;
            if (forms != null && forms.Post(action)) return true;
            return false;
        }

        public override void Install(AppRuntime app)
        {
            if (IsLoaded("PresentationFramework")) wpf = WpfHooks.TryInstall(app);
            if (IsLoaded("System.Windows.Forms") || References("System.Windows.Forms")) forms = FormsHooks.Install(app);
        }

        static bool IsLoaded(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));

        static bool References(string name)
        {
            try
            {
                return Assembly.GetEntryAssembly()?.GetReferencedAssemblies().Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) ?? false;
            }
            catch
            {
                return false;
            }
        }
    }
}
