using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Zharp.Internal;
using Zharp.Internal.Apps;

namespace Zharp.Godot
{
    /// <summary>
    /// Zharp for the C# side of a Godot .NET game. The Zharp Godot addon (its <c>Zharp</c> autoload)
    /// reports script and engine errors, crashes, hangs and scenes; this joins the .NET SDK to it:
    /// C# exceptions that end the game (on any thread; saved to disk and sent on the next launch),
    /// exceptions you catch, HttpClient calls and, with logs on, <c>ILogger</c> lines, all with the
    /// addon's session, device, scene and player.
    /// <code>
    /// public override void _Ready() => ZharpGodot.Start(this);
    /// </code>
    /// </summary>
    public static class ZharpGodot
    {
        static GodotHost? host;

        /// <summary>
        /// Start the .NET SDK with the addon's settings (Project Settings › Zharp, or <c>ZHARP_*</c>
        /// in the environment). Call it once, on the main thread, from any node once the scene tree
        /// is up (a C# autoload or the first scene's <c>_Ready</c>). False when Zharp is off or has
        /// no key.
        /// </summary>
        public static bool Start(Node node, Action<ZharpOptions>? configure = null)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (host != null) return true;
            Node? addon = node.GetTree()?.Root?.GetNodeOrNull("Zharp");
            if (addon == null)
            {
                GD.Print("[Zharp] The Zharp addon's autoload isn't there: enable the Zharp plugin (Project Settings › Plugins). Starting without it.");
            }
            var options = FromProjectSettings();
            configure?.Invoke(options);
            var created = new GodotHost(addon);
            // The addon watches the main thread; a second watchdog would report each hang twice.
            ZharpCore? core = ZharpSdk.StartApp(options, created, new ZharpAppOptions { DetectHangs = false, TrackScreens = false });
            if (core?.App == null) return false;
            host = created;
            return true;
        }

        /// <summary>The signed-in player, here and in the addon (<c>enduser.*</c>).</summary>
        public static void SetUser(string? id, string? email = null, string? name = null)
        {
            ZharpSdk.SetUser(id, email, name);
            host?.CallAddon("set_user", id ?? "", email ?? "", name ?? "");
        }

        /// <summary>Forget the player (sign-out); the addon starts a new session.</summary>
        public static void ClearUser()
        {
            ZharpSdk.SetUser(null, null, null);
            host?.CallAddon("clear_user");
        }

        /// <summary>Report an exception the game caught, with its .NET stack and the code that ran.</summary>
        public static void CaptureException(Exception exception) => ZharpSdk.CaptureException(exception);

        /// <summary>
        /// A handler that traces the requests an <c>HttpClient</c> makes: <c>new HttpClient(ZharpGodot.HttpHandler())</c>.
        /// Godot loads the game into its own load context, where the runtime's own HttpClient
        /// diagnostics don't reach the SDK, so requests are traced here instead: a client span with
        /// the method, URL (secrets filtered), status and headers, and <c>traceparent</c> so your
        /// server's trace continues it.
        /// </summary>
        public static DelegatingHandler HttpHandler(HttpMessageHandler? inner = null) =>
            new ZharpHttpHandler { InnerHandler = inner ?? new HttpClientHandler() };

        /// <summary>Send everything waiting (the addon's too). False when the .NET side didn't finish in time.</summary>
        public static bool Flush(TimeSpan? timeout = null)
        {
            host?.CallAddon("flush");
            return ZharpSdk.Flush(timeout);
        }

        /// <summary>
        /// The addon's settings as the .NET SDK's options. The environment (<c>ZHARP_KEY</c>…) wins,
        /// as it does in the addon, so a setting is passed only when its variable isn't set.
        /// </summary>
        internal static ZharpOptions FromProjectSettings()
        {
            var options = new ZharpOptions();
            string? Text(string name, string env)
            {
                if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(env))) return null;
                if (!ProjectSettings.HasSetting(name)) return null;
                string value = ProjectSettings.GetSetting(name).AsString().Trim();
                return value.Length > 0 ? value : null;
            }
            bool? Flag(string name, string env)
            {
                if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(env))) return null;
                return ProjectSettings.HasSetting(name) ? ProjectSettings.GetSetting(name).AsBool() : null;
            }
            options.Key = Text("zharp/key", "ZHARP_KEY");
            options.Endpoint = Text("zharp/endpoint", "ZHARP_ENDPOINT");
            options.Environment = Text("zharp/environment", "ZHARP_ENVIRONMENT") ?? (OS.HasFeature("editor") && System.Environment.GetEnvironmentVariable("ZHARP_ENVIRONMENT") == null ? "development" : null);
            options.Release = Text("zharp/release", "ZHARP_RELEASE");
            options.Service = Text("zharp/service", "ZHARP_SERVICE");
            options.Logs = Flag("zharp/logs", "ZHARP_LOGS");
            options.Tracing = Flag("zharp/tracing", "ZHARP_TRACING");
            options.Debug = Flag("zharp/debug", "ZHARP_DEBUG");
            bool? enabled = Flag("zharp/enabled", "ZHARP_ENABLED");
            if (enabled == false) options.Disabled = true;
            string? env = System.Environment.GetEnvironmentVariable("ZHARP_ENABLED");
            if (env != null && (env.Trim() == "0" || env.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))) options.Disabled = true;
            return options;
        }
    }

    /// <summary>
    /// Godot: storage under <c>user://zharp/dotnet</c>, the device and engine, and the addon's
    /// device ID, session and scene, read once and then followed through its signals (so another
    /// thread never has to call into the engine).
    /// </summary>
    internal sealed class GodotHost : AppHost
    {
        readonly Node? addon;
        readonly string storage;
        readonly string? deviceId;
        readonly List<KeyValuePair<string, object>> resource = new List<KeyValuePair<string, object>>();
        readonly string? service;
        readonly string? release;
        volatile string? session;
        volatile string? screen;

        public GodotHost(Node? addon)
        {
            this.addon = addon;
            storage = ProjectSettings.GlobalizePath("user://zharp/dotnet");
            if (addon != null)
            {
                deviceId = Text(addon.Get("device_id"));
                session = Text(addon.Get("session_id"));
                screen = Text(addon.Get("screen_name"));
                addon.Connect("session_changed", Callable.From<string>(id => session = id));
                addon.Connect("screen_changed", Callable.From<string>(name => screen = name));
            }
            string name = ServiceNameFor(ProjectSettings.GetSetting("application/config/name").AsString());
            service = name.Length > 0 ? name : null;
            string version = ProjectSettings.HasSetting("application/config/version") ? ProjectSettings.GetSetting("application/config/version").AsString() : "";
            release = version.Length > 0 ? version : null;
            // Read here, on the main thread: the resource is built later, on whichever thread starts exporting.
            void Add(string key, string? value)
            {
                if (!string.IsNullOrEmpty(value)) resource.Add(new KeyValuePair<string, object>(key, value!));
            }
            string os = OS.GetName() switch
            {
                "Windows" => "windows",
                "macOS" => "darwin",
                "Linux" => "linux",
                "FreeBSD" => "freebsd",
                "Android" => "android",
                "iOS" => "ios",
                "Web" => "web",
                var other => other.ToLowerInvariant(),
            };
            Add("os.name", os);
            Add("os.type", os);
            Add("os.version", OS.GetVersion());
            string model = OS.GetModelName();
            if (model != "GenericDevice")
            {
                Add("device.model.identifier", model);
                Add("device.model.name", model);
            }
            Add("host.arch", Engine.GetArchitectureName());
            var info = Engine.GetVersionInfo();
            Add("godot.version", info["string"].AsString());
            Add("godot.build", OS.IsDebugBuild() ? "debug" : "release");
        }

        /// <summary>"Shop Quest" → "shop-quest": the game's name as the addon reports it, and as Zharp names an app created as "Shop Quest".</summary>
        internal static string ServiceNameFor(string name)
        {
            var text = new System.Text.StringBuilder();
            foreach (char c in name.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-') text.Append(c);
                else if (char.IsWhiteSpace(c) || c == '_') text.Append('-');
            }
            string slug = text.ToString();
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return slug.Trim('-');
        }

        static string? Text(Variant value)
        {
            string text = value.VariantType == Variant.Type.String ? value.AsString() : "";
            return text.Length > 0 ? text : null;
        }

        public override string StorageDirectory => storage;

        public override string? DefaultService => service;

        public override string? DefaultRelease => release;

        /// <summary>The game's name and version, as the addon names its service.</summary>
        public override bool DefaultsFirst => true;

        public override bool SuspendsInBackground => OS.GetName() is "Android" or "iOS";

        public override string? DeviceId => deviceId;

        public override string? CurrentSession => session;

        public override string? CurrentScreen => screen;

        public override IEnumerable<KeyValuePair<string, object>> ResourceAttributes() => resource;

        /// <summary>The addon watches the main thread for hangs.</summary>
        public override bool PostToUi(Action action) => false;

        public override void Install(AppRuntime app)
        {
            if (addon == null) return;
            // What's waiting goes when the game quits (the addon's own outbox covers what it queued).
            addon.Connect("tree_exiting", Callable.From(() => ZharpSdk.Flush(TimeSpan.FromSeconds(2))));
        }

        /// <summary>Call the addon on the main thread, from any thread.</summary>
        public void CallAddon(string method, params Variant[] args)
        {
            if (addon == null || !GodotObject.IsInstanceValid(addon)) return;
            if (System.Environment.CurrentManagedThreadId == mainThread) addon.Call(method, args);
            else addon.CallDeferred(method, args);
        }

        readonly int mainThread = System.Environment.CurrentManagedThreadId;
    }

    /// <summary>Client spans for HttpClient, on the SDK's own ActivitySource.</summary>
    internal sealed class ZharpHttpHandler : DelegatingHandler
    {
        static readonly ActivitySource Source = new ActivitySource("Zharp", ZharpSdk.Version);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ZharpCore? core = ZharpCore.Current;
            Uri? uri = request.RequestUri;
            if (core == null || uri == null || uri.ToString().StartsWith(core.Settings.Endpoint, StringComparison.OrdinalIgnoreCase))
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string method = request.Method.Method;
            using Activity? span = Source.StartActivity(method, ActivityKind.Client);
            if (span != null)
            {
                span.SetTag("http.request.method", method);
                span.SetTag("url.full", Secrets.RedactUrl(uri.ToString()));
                span.SetTag("server.address", uri.Host);
                span.SetTag("server.port", uri.Port);
                request.Headers.Remove("traceparent");
                request.Headers.TryAddWithoutValidation("traceparent", "00-" + span.TraceId.ToHexString() + "-" + span.SpanId.ToHexString() + (span.Recorded ? "-01" : "-00"));
                Headers.OnClientRequest(core, span, request);
            }
            try
            {
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (span != null)
                {
                    span.SetTag("http.response.status_code", (int)response.StatusCode);
                    Headers.OnClientResponse(core, span, response);
                    if ((int)response.StatusCode >= 400)
                    {
                        span.SetTag("error.type", ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        span.SetStatus(ActivityStatusCode.Error);
                    }
                }
                return response;
            }
            catch (Exception e)
            {
                span?.SetTag("error.type", e.GetType().FullName);
                span?.SetStatus(ActivityStatusCode.Error, e.Message);
                throw;
            }
        }
    }
}
