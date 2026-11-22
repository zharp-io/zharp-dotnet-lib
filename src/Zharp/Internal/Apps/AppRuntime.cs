using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Zharp.Internal.Apps
{
    /// <summary>
    /// What an app package (Zharp.Windows, Zharp.Maui) tells the core about where it runs. The rest —
    /// sessions, the device ID, screens, crashes, hangs and the outbox — is the same everywhere and
    /// lives in <see cref="AppRuntime"/>.
    /// </summary>
    internal abstract class AppHost
    {
        /// <summary>Where the app may keep Zharp's files (the outbox, the session, the device ID).</summary>
        public abstract string StorageDirectory { get; }

        /// <summary>os.*, device.* and app.* for the resource.</summary>
        public abstract IEnumerable<KeyValuePair<string, object>> ResourceAttributes();

        /// <summary>Run <paramref name="action"/> on the UI thread, for the hang watchdog; false when there's none (yet).</summary>
        public abstract bool PostToUi(Action action);

        /// <summary>The service name when none is set: the app's own name.</summary>
        public virtual string? DefaultService => null;

        /// <summary>The release when none is set: the app's version.</summary>
        public virtual string? DefaultRelease => null;

        /// <summary>Whether the OS suspends the app in the background (mobile), so time away isn't a hang.</summary>
        public virtual bool SuspendsInBackground => false;

        /// <summary>Whether the defaults above win over the entry assembly's name and version.</summary>
        public virtual bool DefaultsFirst => false;

        /// <summary>Hook the platform's crash events, screens and lifecycle. Called once Zharp runs.</summary>
        public abstract void Install(AppRuntime app);
    }

    /// <summary>
    /// Zharp on an app, as the Android, iOS and Flutter SDKs do it:
    /// <list type="bullet">
    /// <item><b>Crashes</b> are written to disk before they are sent, and sent on the next launch
    /// when the process didn't live long enough.</item>
    /// <item><b>Hangs:</b> the UI thread not answering for 5 s is reported once per freeze, like
    /// Android's ANR (<c>ApplicationNotResponding</c>).</item>
    /// <item><b>Sessions:</b> <c>session.id</c> on every span, log and error; a new one after 30
    /// minutes in the background, or on <c>ClearUser</c>.</item>
    /// <item><b>Screens:</b> <c>screen.name</c> on what happens while it's shown, and a
    /// <c>screen {name}</c> span each time one is shown.</item>
    /// <item><b>A device ID</b> (<c>device.id</c>): random, kept in the app's own storage.</item>
    /// <item><b>An outbox</b> on disk for what couldn't be sent.</item>
    /// </list>
    /// </summary>
    internal sealed class AppRuntime
    {
        public const string AnrType = "ApplicationNotResponding";
        public static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);

        public AppHost Host { get; }
        public Outbox Outbox { get; }
        public OutboxHandler? Handler { get; internal set; }
        public string DeviceId { get; }
        public SessionManager Session { get; }
        public TimeSpan HangThreshold { get; set; } = TimeSpan.FromSeconds(5);
        public bool DetectHangs { get; set; } = true;
        public bool TrackScreens { get; set; } = true;

        /// <summary>The app's settings, before Zharp starts (the hang watchdog starts with it).</summary>
        public void Apply(ZharpAppOptions? options)
        {
            if (options == null) return;
            DetectHangs = options.DetectHangs;
            HangThreshold = options.HangThreshold;
            TrackScreens = options.TrackScreens;
        }

        volatile string? screen;
        ZharpCore? core;
        HangWatchdog? watchdog;
        static readonly ActivitySource Screens = new ActivitySource("Zharp", ZharpSdk.Version);

        public AppRuntime(AppHost host)
        {
            Host = host;
            string root = host.StorageDirectory;
            Outbox = new Outbox(Path.Combine(root, "outbox"));
            DeviceId = ReadOrCreate(Path.Combine(root, "device-id"));
            Session = new SessionManager(Path.Combine(root, "session"));
        }

        public string? Screen => screen;

        /// <summary>Zharp runs: hook the platform, send what earlier runs left, watch the UI thread.</summary>
        internal void Started(ZharpCore core)
        {
            this.core = core;
            try { Host.Install(this); }
            catch (Exception e) { core.DebugLog("app hooks: " + e.Message); }
            Handler?.DrainLater();
            if (DetectHangs && HangThreshold > TimeSpan.Zero)
            {
                watchdog = new HangWatchdog(this, HangThreshold);
                watchdog.Start();
            }
        }

        /// <summary>session.id and screen.name for a span, log or error made now.</summary>
        public IEnumerable<KeyValuePair<string, object?>> Attributes()
        {
            yield return new KeyValuePair<string, object?>("session.id", Session.Current());
            string? s = screen;
            if (s != null) yield return new KeyValuePair<string, object?>("screen.name", s);
        }

        /// <summary>A screen was shown: what happens next is on it, and a <c>screen {name}</c> span marks it.</summary>
        public void ScreenShown(string name, string type)
        {
            if (!TrackScreens || string.IsNullOrEmpty(name) || name == screen) return;
            screen = name;
            if (core == null || !core.Settings.Tracing) return;
            Activity? saved = Activity.Current;
            try
            {
                Activity.Current = null;
                using Activity? span = Screens.StartActivity("screen " + name, ActivityKind.Internal);
                span?.SetTag("screen.name", name);
                span?.SetTag("screen.type", type);
            }
            catch (Exception e)
            {
                core.DebugLog("screen: " + e.Message);
            }
            finally
            {
                Activity.Current = saved;
            }
        }

        /// <summary>In the background, or back from it just now: a suspended app isn't a frozen one.</summary>
        internal bool Suspended => Host.SuspendsInBackground && (background || DateTime.UtcNow - foregroundAt < HangThreshold + HangThreshold);
        volatile bool background;
        DateTime foregroundAt = DateTime.MinValue;

        public void OnBackground()
        {
            background = true;
            Session.OnBackground();
            // The app may not come back: what's waiting goes now.
            ThreadPool.QueueUserWorkItem(_ => core?.Flush(5000));
        }

        public void OnForeground()
        {
            if (background) foregroundAt = DateTime.UtcNow;
            background = false;
            Session.OnForeground();
            Handler?.DrainLater();
        }

        /// <summary>
        /// An exception that ends the app (or would, unless the platform's handler says otherwise):
        /// written to disk first, then a bounded try at sending it and what's queued.
        /// </summary>
        public void ReportCrash(Exception error, string mechanism, bool terminating = true)
        {
            ZharpCore? c = core;
            if (c == null) return;
            if (!terminating)
            {
                // The app goes on (Windows Forms' dialog): an unhandled error, sent as usual.
                c.Report(error, handled: false, mechanism: mechanism, span: Activity.Current);
                return;
            }
            Session.Save();
            // From here every batch is on disk before it's sent: the process may die any moment.
            if (Handler != null) Handler.PersistFirst = true;
            c.Report(error, handled: false, mechanism: mechanism, span: Activity.Current,
                extra: new[] { new KeyValuePair<string, object?>("zharp.error.kind", "crash") },
                level: LogLevel.Critical);
            c.Flush(3000);
        }

        /// <summary>The UI thread didn't answer for <paramref name="blocked"/>.</summary>
        public void ReportHang(TimeSpan blocked)
        {
            ZharpCore? c = core;
            if (c == null) return;
            string where = screen != null ? " on " + screen : "";
            string message = "The UI thread was blocked for " + blocked.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s" + where;
            c.ReportAnr(message);
        }

        static string ReadOrCreate(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    string saved = File.ReadAllText(path).Trim();
                    if (saved.Length == 32) return saved;
                }
                string id = Ids.Random128();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, id);
                return id;
            }
            catch
            {
                return Ids.Random128();
            }
        }
    }

    internal static class Ids
    {
        /// <summary>32 lowercase hex characters: 128 random bits.</summary>
        public static string Random128()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var chars = new char[32];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < 16; i++)
            {
                chars[i * 2] = hex[bytes[i] >> 4];
                chars[i * 2 + 1] = hex[bytes[i] & 0xf];
            }
            return new string(chars);
        }
    }

    /// <summary>
    /// The session every span and log carries. A new one starts after 30 minutes in the
    /// background, on <see cref="Rotate"/> (<c>ClearUser</c>), and when the app starts again after
    /// being away that long; an app restarted sooner keeps it.
    /// </summary>
    internal sealed class SessionManager
    {
        readonly string? path;
        readonly Func<DateTime> clock;
        readonly TimeSpan timeout;
        readonly object gate = new object();
        string id;
        DateTime? backgroundSince;

        public SessionManager(string? path, Func<DateTime>? clock = null, TimeSpan? timeout = null)
        {
            this.path = path;
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.timeout = timeout ?? AppRuntime.SessionTimeout;
            id = Load() ?? Ids.Random128();
            Save();
        }

        public string Current()
        {
            lock (gate)
            {
                if (backgroundSince.HasValue && clock() - backgroundSince.Value >= timeout)
                {
                    id = Ids.Random128();
                    backgroundSince = clock();
                    Save();
                }
                return id;
            }
        }

        public void OnBackground()
        {
            lock (gate)
            {
                backgroundSince = clock();
                Save();
            }
        }

        public void OnForeground()
        {
            lock (gate)
            {
                Current();
                backgroundSince = null;
                Save();
            }
        }

        public void Rotate()
        {
            lock (gate)
            {
                id = Ids.Random128();
                Save();
            }
        }

        /// <summary>Remember the time, so an app started again soon after keeps the session.</summary>
        public void Save()
        {
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                long ms = (long)(clock() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                File.WriteAllText(path, id + "\n" + ms.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                // In memory only.
            }
        }

        string? Load()
        {
            if (path == null) return null;
            try
            {
                if (!File.Exists(path)) return null;
                string[] parts = File.ReadAllText(path).Split('\n');
                if (parts.Length < 2 || parts[0].Trim().Length != 32) return null;
                if (!long.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long ms)) return null;
                DateTime lastSeen = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
                return clock() - lastSeen < timeout ? parts[0].Trim() : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Hangs, the way Android's watchdog sees them: a task posted to the UI thread that hasn't run
    /// within the threshold means the UI is frozen. Reported once per freeze; skipped while a
    /// debugger is attached (breakpoints freeze the UI thread too).
    /// </summary>
    internal sealed class HangWatchdog
    {
        readonly AppRuntime app;
        readonly TimeSpan threshold;
        volatile bool answered = true;
        Thread? thread;

        public HangWatchdog(AppRuntime app, TimeSpan threshold)
        {
            this.app = app;
            this.threshold = threshold;
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "zharp-hang" };
            thread.Start();
        }

        void Run()
        {
            Action pong = () => answered = true;
            try
            {
                while (true)
                {
                    answered = false;
                    if (!app.Host.PostToUi(pong))
                    {
                        // No UI thread yet (still starting): look again later.
                        answered = true;
                        Thread.Sleep(threshold);
                        continue;
                    }
                    var watch = Stopwatch.StartNew();
                    while (!answered && watch.Elapsed < threshold) Thread.Sleep(100);
                    if (answered)
                    {
                        Thread.Sleep(TimeSpan.FromMilliseconds(Math.Max(0, threshold.TotalMilliseconds - watch.Elapsed.TotalMilliseconds)));
                        continue;
                    }
                    if (Debugger.IsAttached || app.Suspended) continue;
                    app.ReportHang(threshold);
                    // Once per freeze: wait for the UI thread to come back before watching again.
                    while (!answered) Thread.Sleep(500);
                }
            }
            catch
            {
                // Hang detection stopped.
            }
        }
    }
}
