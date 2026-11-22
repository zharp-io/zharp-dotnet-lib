using System;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Zharp.Internal;

namespace Zharp
{
    /// <summary>
    /// Zharp for any .NET app. On a Generic Host or ASP.NET Core, call
    /// <c>builder.Services.AddZharp()</c> instead; elsewhere (a plain <c>Main</c>):
    /// <code>using var zharp = ZharpSdk.Init();</code>
    /// </summary>
    public static class ZharpSdk
    {
        /// <summary>This SDK's version.</summary>
        public const string Version = "0.1.0";

        /// <summary>
        /// Start Zharp: errors (unhandled exceptions, failed queries, exceptions logged at Error),
        /// traces of HttpClient and database calls, and with <c>ZHARP_LOGS=true</c> the app's logs.
        /// Dispose the result when the app ends to send what's waiting.
        /// </summary>
        public static IDisposable Init(Action<ZharpOptions>? configure = null)
        {
            var options = new ZharpOptions();
            configure?.Invoke(options);
            return Start(options, null) ?? (IDisposable)new Nothing();
        }

        internal static ZharpCore? Start(ZharpOptions options, IConfiguration? configuration, IZharpIntegration? integration = null)
        {
            Settings settings = Settings.Resolve(options, configuration);
            if (settings.Disabled) return null;
            try
            {
                return ZharpCore.Start(settings, integration != null ? new[] { integration } : null);
            }
            catch (Exception e)
            {
                ZharpCore.Announce("could not start: " + e.Message);
                return null;
            }
        }

        /// <summary>Whether Zharp is running.</summary>
        public static bool IsEnabled => ZharpCore.Current != null;

        /// <summary>
        /// Zharp's logger factory, for apps without a host: loggers from it send to Zharp with
        /// <c>ZHARP_LOGS=true</c>, and their exceptions at Error are reported either way.
        /// </summary>
        public static ILoggerFactory LoggerFactory => ZharpCore.Current?.LoggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

        /// <summary>Report an exception the app caught. Each exception is reported once, however often it's passed here.</summary>
        public static void CaptureException(Exception exception, bool handled = true)
        {
            if (exception == null) return;
            ZharpCore.Current?.Report(exception, handled, handled ? "manual" : "uncaught", Activity.Current);
        }

        /// <summary>The signed-in user: on the current request's errors and trace, or for the whole process outside requests.</summary>
        public static void SetUser(string? id, string? email = null, string? name = null) =>
            ZharpCore.Current?.Users.SetUser(id, email, name);

        /// <summary>Forget the user (sign-out).</summary>
        public static void ClearUser() => ZharpCore.Current?.Users.SetUser(null, null, null);

        /// <summary>A tag on every error and request from now on; null removes it.</summary>
        public static void SetTag(string key, string? value) => ZharpCore.Current?.Users.SetTag(key, value);

        /// <summary>Send everything waiting. False when it didn't finish in time.</summary>
        public static bool Flush(TimeSpan? timeout = null) =>
            ZharpCore.Current?.Flush((int)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds) ?? true;

        sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
