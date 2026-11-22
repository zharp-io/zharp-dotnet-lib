using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using OpenTelemetry.Instrumentation.AspNet;
using OpenTelemetry.Trace;
using Zharp.Internal;

[assembly: PreApplicationStartMethod(typeof(Zharp.AspNet.ZharpAspNet), nameof(Zharp.AspNet.ZharpAspNet.Start))]

namespace Zharp.AspNet
{
    /// <summary>
    /// Classic ASP.NET: referencing the package is the setup. Before the app starts, Zharp
    /// registers OpenTelemetry's request module and its own, so <c>web.config</c> needs no edit.
    /// The key goes in <c>&lt;appSettings&gt;</c> (<c>&lt;add key="Zharp:Key" value="zpk_…" /&gt;</c>) or the
    /// environment (<c>ZHARP_KEY</c>); <c>Zharp:Disabled</c> = true turns it off.
    /// </summary>
    public static class ZharpAspNet
    {
        static bool started;

        /// <summary>Called by ASP.NET before the application starts; also safe to call from Application_Start.</summary>
        public static void Start()
        {
            if (started) return;
            started = true;
            try
            {
                Settings.FallbackService = System.Web.Hosting.HostingEnvironment.SiteName;
                ZharpCore? core = ZharpSdk.Start(new ZharpOptions(), new AppSettingsConfiguration());
                if (core == null) return;
                HttpApplication.RegisterModule(typeof(TelemetryHttpModule));
                HttpApplication.RegisterModule(typeof(ZharpHttpModule));
                AppDomain.CurrentDomain.DomainUnload += (_, _) => core.Dispose();
            }
            catch (Exception e)
            {
                ZharpCore.Announce("could not start for ASP.NET: " + e.Message);
            }
        }
    }

    /// <summary>OpenTelemetry's ASP.NET instrumentation on Zharp's tracer, with headers.</summary>
    internal sealed class AspNetIntegration : IZharpIntegration
    {
        public void ConfigureTracing(TracerProviderBuilder builder, ZharpCore core)
        {
            builder.AddAspNetInstrumentation(o =>
            {
                o.RecordException = true;
                o.EnrichWithHttpRequest = (activity, request) => Headers.Record(core, activity, "request", Pairs(request.Headers));
                o.EnrichWithHttpResponse = (activity, response) =>
                {
                    try { Headers.Record(core, activity, "response", Pairs(response.Headers)); }
                    catch (PlatformNotSupportedException) { /* classic pipeline: response headers can't be read */ }
                };
            });
        }

        static IEnumerable<KeyValuePair<string, IEnumerable<string>>> Pairs(System.Collections.Specialized.NameValueCollection headers)
        {
            foreach (string? name in headers.AllKeys)
            {
                if (name == null) continue;
                yield return new KeyValuePair<string, IEnumerable<string>>(name, headers.GetValues(name) ?? Array.Empty<string>());
            }
        }

        public void Started(ZharpCore core) { }
        public void AddServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services, ZharpCore core) { }
    }

    /// <summary>What escaped a request (Application_Error): reported once, when the request ends.</summary>
    public sealed class ZharpHttpModule : IHttpModule
    {
        public void Init(HttpApplication application)
        {
            application.Error += (sender, _) =>
            {
                try
                {
                    var app = (HttpApplication)sender;
                    Exception? error = app.Server.GetLastError();
                    if (error is HttpUnhandledException && error.InnerException != null) error = error.InnerException;
                    if (error == null) return;
                    // 404s and other client errors ASP.NET raises as HttpException are answers, not failures.
                    if (error is HttpException http && http.GetHttpCode() < 500) return;
                    ZharpCore.Current?.Errors.Hold(error, "aspnet", uncaught: true, Activity.Current);
                }
                catch
                {
                    // Never in the app's way.
                }
            };
        }

        public void Dispose() { }
    }

    /// <summary>
    /// <c>&lt;appSettings&gt;</c> as the <c>"Zharp"</c> configuration section: <c>Zharp:Key</c>,
    /// <c>Zharp:Service</c>, <c>Zharp:Logs</c>, … (environment variables still win).
    /// </summary>
    internal sealed class AppSettingsConfiguration : IConfiguration
    {
        public string? this[string key]
        {
            get => System.Configuration.ConfigurationManager.AppSettings[key];
            set => throw new NotSupportedException();
        }

        public IConfigurationSection GetSection(string key) => new Section(this, key);
        public IEnumerable<IConfigurationSection> GetChildren() => Enumerable.Empty<IConfigurationSection>();
        public IChangeToken GetReloadToken() => NullChangeToken.Instance;

        sealed class Section : IConfigurationSection
        {
            readonly AppSettingsConfiguration root;
            public Section(AppSettingsConfiguration root, string path)
            {
                this.root = root;
                Path = path;
            }

            public string? this[string key]
            {
                get => root[Path + ":" + key];
                set => throw new NotSupportedException();
            }

            public string Key => Path.Split(':').Last();
            public string Path { get; }
            public string? Value
            {
                get => root[Path];
                set => throw new NotSupportedException();
            }

            public IConfigurationSection GetSection(string key) => new Section(root, Path + ":" + key);
            public IEnumerable<IConfigurationSection> GetChildren() => Enumerable.Empty<IConfigurationSection>();
            public IChangeToken GetReloadToken() => NullChangeToken.Instance;
        }

        sealed class NullChangeToken : IChangeToken
        {
            public static readonly NullChangeToken Instance = new NullChangeToken();
            public bool HasChanged => false;
            public bool ActiveChangeCallbacks => false;
            public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => new Nothing();
            sealed class Nothing : IDisposable { public void Dispose() { } }
        }
    }
}
