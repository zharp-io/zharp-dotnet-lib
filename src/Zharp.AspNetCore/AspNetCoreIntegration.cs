using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Zharp.Internal;

namespace Zharp.AspNetCore
{
    /// <summary>
    /// ASP.NET Core, found by <c>AddZharp()</c> when this package is referenced:
    /// <list type="bullet">
    /// <item>requests traced by OpenTelemetry's instrumentation, named by route (<c>GET /orders/{id}</c>);</item>
    /// <item>request and response headers on the request's span, secrets filtered;</item>
    /// <item>what escaped a request, taken from ASP.NET Core's diagnostic events, so it is seen even
    /// when <c>UseExceptionHandler</c> or the developer page handled it — reported once, when the
    /// request ends, as unhandled when the answer was 5xx;</item>
    /// <item>request and response bodies with <c>ZHARP_CAPTURE_BODIES</c>.</item>
    /// </list>
    /// </summary>
    internal sealed class AspNetCoreIntegration : IZharpIntegration
    {
        static readonly HashSet<string> ExceptionEvents = new HashSet<string>
        {
            "Microsoft.AspNetCore.Diagnostics.UnhandledException",
            "Microsoft.AspNetCore.Diagnostics.HandledException",
            "Microsoft.AspNetCore.Hosting.UnhandledException",
        };

        public void ConfigureTracing(TracerProviderBuilder builder, ZharpCore core)
        {
            builder.AddAspNetCoreInstrumentation(o =>
            {
                o.RecordException = true;
                o.EnrichWithHttpRequest = (activity, request) => OnRequest(core, activity, request);
                o.EnrichWithHttpResponse = (activity, response) =>
                    Headers.Record(core, activity, "response", response.Headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.Select(v => v ?? ""))));
            });
        }

        static void OnRequest(ZharpCore core, Activity activity, HttpRequest request)
        {
            Headers.Record(core, activity, "request", request.Headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.Select(v => v ?? ""))));
            // The browser SDK's user and session, from its baggage header.
            string baggage = request.Headers["baggage"].ToString();
            if (baggage.Length == 0) return;
            foreach (string part in baggage.Split(','))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).Trim();
                if (Array.IndexOf(UserStore.BaggageKeys, key) < 0) continue;
                string value = part.Substring(eq + 1).Split(';')[0].Trim();
                try { value = Uri.UnescapeDataString(value); } catch { /* as sent */ }
                if (value.Length > 0) activity.SetTag(key, value);
            }
        }

        public void Started(ZharpCore core)
        {
            DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name != "Microsoft.AspNetCore") return;
                listener.Subscribe(new Observer<KeyValuePair<string, object?>>(e => OnEvent(core, e)), (name, _, _) => ExceptionEvents.Contains(name));
            }));
        }

        static void OnEvent(ZharpCore core, KeyValuePair<string, object?> e)
        {
            if (!ExceptionEvents.Contains(e.Key) || e.Value == null) return;
            try
            {
                if (e.Value.GetType().GetProperty("exception", BindingFlags.Public | BindingFlags.Instance)?.GetValue(e.Value) is Exception error)
                {
                    core.Errors.Hold(error, "aspnetcore", uncaught: true, Activity.Current);
                }
            }
            catch (Exception ex)
            {
                core.DebugLog("aspnetcore event: " + ex.Message);
            }
        }

        public void AddServices(IServiceCollection services, ZharpCore core)
        {
            if (core.Settings.CaptureBodies != null) services.AddSingleton<IStartupFilter>(new BodiesStartupFilter(core));
        }

        sealed class Observer<T> : IObserver<T>
        {
            readonly Action<T> next;
            public Observer(Action<T> next) { this.next = next; }
            public void OnNext(T value) => next(value);
            public void OnError(Exception error) { }
            public void OnCompleted() { }
        }
    }
}
