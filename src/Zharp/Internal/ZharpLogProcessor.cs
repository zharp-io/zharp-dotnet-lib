using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Zharp.Internal
{
    /// <summary>
    /// Every log record on its way to the exporter (it wraps the exporting processor):
    /// <list type="bullet">
    /// <item>Zharp's own error reports go out;</item>
    /// <item>an exception logged at Error or above is handed to the error tracker, which reports it
    /// once; the line itself keeps its text but loses its exception, so it isn't a second error;</item>
    /// <item>with logs off (the default) nothing else is sent: the Zharp agent collects the app's
    /// output, linked to its requests by the stamp.</item>
    /// </list>
    /// </summary>
    internal sealed class ZharpLogProcessor : BaseProcessor<LogRecord>
    {
        /// <summary>Loggers through which hosts report what escaped a request or a service: unhandled.</summary>
        static readonly string[] UncaughtCategories =
        {
            "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware",
            "Microsoft.AspNetCore.Diagnostics.DeveloperExceptionPageMiddleware",
            "Microsoft.AspNetCore.Server.Kestrel",
            "Microsoft.AspNetCore.Hosting.Diagnostics",
            "Microsoft.Extensions.Hosting.Internal.Host",
            "Microsoft.Extensions.Hosting",
        };

        public static ZharpLogProcessor? Instance { get; private set; }

        readonly ZharpCore core;
        readonly BaseProcessor<LogRecord>? export;

        public ZharpLogProcessor(ZharpCore core, BaseProcessor<LogRecord>? export)
        {
            this.core = core;
            this.export = export;
            Instance = this;
        }

        public static bool IsUncaughtCategory(string? category)
        {
            if (category == null) return false;
            foreach (string prefix in UncaughtCategories)
            {
                if (category.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

#pragma warning disable CS0618 // LogRecord.LogLevel: the level as ILogger gave it.
        public override void OnEnd(LogRecord record)
        {
            try
            {
                if (!parentSet) PassParent();
                if (record.CategoryName == ZharpCore.ErrorsCategory)
                {
                    Activity? span = ZharpCore.EmittingFor;
                    if (span != null)
                    {
                        record.TraceId = span.TraceId;
                        record.SpanId = span.SpanId;
                        record.TraceFlags = span.ActivityTraceFlags;
                    }
                    export?.OnEnd(record);
                    return;
                }
                if (record.Exception != null)
                {
                    if (record.LogLevel >= LogLevel.Error)
                    {
                        bool uncaught = IsUncaughtCategory(record.CategoryName);
                        string mechanism = !uncaught ? "logger" : record.CategoryName!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ? "aspnetcore" : "hosting";
                        core.Errors.Hold(record.Exception, mechanism, uncaught, Activity.Current);
                    }
                    record.Exception = null;
                }
                if (!core.Settings.Logs || export == null) return;
                AddUser(record);
                export.OnEnd(record);
            }
            catch (Exception e)
            {
                core.DebugLog("log: " + e.Message);
            }
        }
#pragma warning restore CS0618

        /// <summary>The request's user and session on its log lines, as on its spans.</summary>
        void AddUser(LogRecord record)
        {
            Activity? span = Activity.Current;
            if (span == null) return;
            List<KeyValuePair<string, object?>>? extra = null;
            foreach (var kv in core.Users.AttributesFor(span))
            {
                if (!kv.Key.StartsWith("enduser.", StringComparison.Ordinal) && kv.Key != "session.id") continue;
                (extra ??= new List<KeyValuePair<string, object?>>()).Add(kv);
            }
            if (extra == null) return;
            var all = new List<KeyValuePair<string, object?>>(record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>());
            all.AddRange(extra);
            record.Attributes = all;
        }

        bool parentSet;

        /// <summary>
        /// The wrapped processor needs the provider too (its exporter reads the service's resource
        /// from it), and only the provider may set it.
        /// </summary>
        void PassParent()
        {
            parentSet = true;
            if (export == null || ParentProvider == null) return;
            try
            {
                typeof(BaseProcessor<LogRecord>)
                    .GetMethod("SetParentProvider", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                    ?.Invoke(export, new object[] { ParentProvider });
            }
            catch (Exception e)
            {
                core.DebugLog("log exporter setup: " + e.Message);
            }
        }

        protected override bool OnForceFlush(int timeoutMilliseconds) => export?.ForceFlush(timeoutMilliseconds) ?? true;

        protected override bool OnShutdown(int timeoutMilliseconds) => export?.Shutdown(timeoutMilliseconds) ?? true;

        protected override void Dispose(bool disposing)
        {
            if (disposing) export?.Dispose();
            base.Dispose(disposing);
        }
    }
}
