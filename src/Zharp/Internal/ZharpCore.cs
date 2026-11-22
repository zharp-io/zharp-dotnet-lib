using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Zharp.Internal
{
    /// <summary>Something a host package adds: instrumentation, and hooks once Zharp runs.</summary>
    internal interface IZharpIntegration
    {
        void ConfigureTracing(TracerProviderBuilder builder, ZharpCore core);
        void Started(ZharpCore core);
        /// <summary>On a host (AddZharp): services the integration needs, such as middleware.</summary>
        void AddServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services, ZharpCore core);
    }

    /// <summary>
    /// Zharp in one process: the settings, the error tracker, and the OpenTelemetry pipelines it
    /// reports through — its own tracer provider and logger factory, so whatever else the app
    /// does with OpenTelemetry is left alone.
    /// </summary>
    internal sealed class ZharpCore : IDisposable
    {
        public const string ErrorsCategory = "Zharp.Errors";
        /// <summary>Host packages Zharp looks for when it starts, so <c>AddZharp()</c> is the one line whatever the host.</summary>
        static readonly string[] KnownIntegrations =
        {
            "Zharp.AspNetCore.AspNetCoreIntegration, Zharp.AspNetCore",
            "Zharp.AspNet.AspNetIntegration, Zharp.AspNet",
        };

        static readonly object StartLock = new object();
        public static ZharpCore? Current { get; private set; }

        public Settings Settings { get; }
        public ErrorTracker Errors { get; }
        public UserStore Users { get; } = new UserStore();
        public ILoggerFactory LoggerFactory { get; private set; } = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

        TracerProvider? tracer;
        ILogger errorLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        readonly List<IZharpIntegration> integrations = new List<IZharpIntegration>();
        public IReadOnlyList<IZharpIntegration> Integrations => integrations;
        bool disposed;

        ZharpCore(Settings settings)
        {
            Settings = settings;
            Errors = new ErrorTracker(this);
        }

        /// <summary>Start Zharp once per process; later calls get the running one.</summary>
        public static ZharpCore Start(Settings settings, IEnumerable<IZharpIntegration>? extra = null)
        {
            lock (StartLock)
            {
                if (Current != null && !Current.disposed) return Current;
                var core = new ZharpCore(settings);
                core.Build(extra);
                Current = core;
                return core;
            }
        }

        void Build(IEnumerable<IZharpIntegration>? extra)
        {
            foreach (string name in KnownIntegrations)
            {
                try
                {
                    Type? type = Type.GetType(name, throwOnError: false);
                    if (type != null && Activator.CreateInstance(type) is IZharpIntegration integration) integrations.Add(integration);
                }
                catch (Exception e)
                {
                    DebugLog("integration " + name + ": " + e.Message);
                }
            }
            if (extra != null) integrations.AddRange(extra);

            if (!Settings.HasKey) Announce("ZHARP_KEY is not set: nothing will be sent. Set it in the environment or the \"Zharp\" section of appsettings.json.");

            ResourceBuilder resource = ResourceBuilder.CreateEmpty()
                .AddService(Settings.Service, serviceVersion: Settings.Release, autoGenerateServiceInstanceId: true)
                .AddTelemetrySdk()
                .AddAttributes(ResourceAttributes());

            // Traces: sampled spans are exported; the rest are still recorded, so every error has
            // its request, and nothing goes out when tracing is off.
            TracerProviderBuilder tb = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resource)
                .SetSampler(new ZharpSampler(Settings.Tracing ? Settings.SampleRate : 0))
                .AddSource("Npgsql", "MySqlConnector", "Zharp")
                .AddHttpClientInstrumentation(o =>
                {
                    o.RecordException = true;
                    o.EnrichWithHttpRequestMessage = (activity, request) => Headers.OnClientRequest(this, activity, request);
                    o.EnrichWithHttpResponseMessage = (activity, response) => Headers.OnClientResponse(this, activity, response);
                })
                .AddSqlClientInstrumentation();
            foreach (string source in Settings.Sources) tb.AddSource(source);
            foreach (IZharpIntegration integration in integrations)
            {
                try { integration.ConfigureTracing(tb, this); }
                catch (Exception e) { DebugLog("tracing setup: " + e.Message); }
            }
            tb.AddProcessor(new ZharpActivityProcessor(this));
            if (Settings.HasKey && Settings.Tracing)
            {
                tb.AddProcessor(new BatchActivityExportProcessor(new OtlpTraceExporter(ExporterOptions("/v1/traces")), scheduledDelayMilliseconds: 2000));
            }
            tracer = tb.Build();

            // Logs: Zharp's error reports always; the app's own lines only with ZHARP_LOGS=true.
            BaseProcessor<LogRecord>? export = Settings.HasKey
                ? new BatchLogRecordExportProcessor(new OtlpLogExporter(ExporterOptions("/v1/logs")), scheduledDelayMilliseconds: 2000)
                : null;
            var logProcessor = new ZharpLogProcessor(this, export);
            LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Trace);
                b.AddOpenTelemetry(o =>
                {
                    o.SetResourceBuilder(resource);
                    o.IncludeFormattedMessage = true;
                    o.IncludeScopes = false;
                    o.AddProcessor(logProcessor);
                });
            });
            errorLogger = LoggerFactory.CreateLogger(ErrorsCategory);

            DbFailures.Hook();
            if (Settings.CaptureUncaught)
            {
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                TaskScheduler.UnobservedTaskException += OnUnobserved;
            }
            AppDomain.CurrentDomain.ProcessExit += OnExit;
            foreach (IZharpIntegration integration in integrations)
            {
                try { integration.Started(this); }
                catch (Exception e) { DebugLog("integration start: " + e.Message); }
            }
            DebugLog("started: " + Settings.DescribeTarget() + ", endpoint " + Settings.Endpoint + ", logs " + (Settings.Logs ? "on" : "off") + ", tracing " + (Settings.Tracing ? "on" : "off"));
        }

        OtlpExporterOptions ExporterOptions(string path) => new OtlpExporterOptions
        {
            Endpoint = new Uri(Settings.Endpoint + path),
            Protocol = OtlpExportProtocol.HttpProtobuf,
            Headers = "Authorization=Bearer " + Settings.Key,
            TimeoutMilliseconds = 10000,
        };

        IEnumerable<KeyValuePair<string, object>> ResourceAttributes()
        {
            yield return new KeyValuePair<string, object>("telemetry.distro.name", "zharp");
            yield return new KeyValuePair<string, object>("telemetry.distro.version", ZharpSdk.Version);
            if (Settings.Environment != null) yield return new KeyValuePair<string, object>("deployment.environment.name", Settings.Environment);
        }

        // ── reports ─────────────────────────────────────────────────────

        /// <summary>
        /// Report an exception, once per failure: an exception (or one it wraps) already reported is
        /// skipped. The query it came from, if any, gives it the statement and the database's code.
        /// </summary>
        public void Report(Exception error, bool handled, string mechanism, Activity? span, IEnumerable<KeyValuePair<string, object?>>? extra = null, LogLevel level = LogLevel.Error)
        {
            try
            {
                if (disposed || !Errors.TryMark(error)) return;
                DbFailure? query = DbFailures.OfException(error);
                if (query != null && query.Done) query = null;
                if (query != null)
                {
                    if (mechanism == "logger") mechanism = "db";
                    query.Done = true;
                    if (query.Error != null) Errors.TryMark(query.Error);
                }
                Send(error, handled, mechanism, span, query, extra, level);
            }
            catch (Exception e)
            {
                DebugLog("report: " + e.Message);
            }
        }

        /// <summary>Report a failed query that no exception report covered.</summary>
        public void ReportQuery(DbFailure query, bool handled, string mechanism)
        {
            try
            {
                if (disposed || query.Done) return;
                query.Done = true;
                if (query.Error != null)
                {
                    if (!Errors.TryMark(query.Error)) return;
                    Send(query.Error, handled, mechanism, query.Span, query, null, LogLevel.Error);
                    return;
                }
                // The driver's exception wasn't seen: the span's own description.
                var attributes = new List<KeyValuePair<string, object?>>
                {
                    new KeyValuePair<string, object?>("exception.type", query.TypeName()),
                    new KeyValuePair<string, object?>("exception.message", query.Span?.StatusDescription ?? "The query failed"),
                    new KeyValuePair<string, object?>("zharp.handled", handled ? "true" : "false"),
                    new KeyValuePair<string, object?>("zharp.mechanism", mechanism),
                };
                attributes.AddRange(query.Attributes());
                attributes.AddRange(Users.AttributesFor(query.Span));
                Emit(LogLevel.Error, attributes, query.Span);
                Errors.NoteReported(query.Span);
            }
            catch (Exception e)
            {
                DebugLog("query report: " + e.Message);
            }
        }

        void Send(Exception error, bool handled, string mechanism, Activity? span, DbFailure? query, IEnumerable<KeyValuePair<string, object?>>? extra, LogLevel level)
        {
            ExceptionInfo info = Stacks.Describe(error);
            List<StackFrame> frames = Stacks.FramesOf(error);
            var attributes = new List<KeyValuePair<string, object?>>();
            if (extra != null) attributes.AddRange(extra);
            string type = info.Type;
            string message = info.Message;
            if (query != null)
            {
                type = query.TypeName();
                if (query.Error != null && !ReferenceEquals(query.Error, error)) message = query.Error.Message;
                attributes.AddRange(query.Attributes());
            }
            else
            {
                string? code = FindCode(error);
                if (code != null) attributes.Add(new KeyValuePair<string, object?>("db.error.code", code));
            }
            attributes.Add(new KeyValuePair<string, object?>("exception.type", type));
            attributes.Add(new KeyValuePair<string, object?>("exception.message", message));
            attributes.Add(new KeyValuePair<string, object?>("exception.stacktrace", info.Stacktrace));
            attributes.Add(new KeyValuePair<string, object?>("zharp.handled", handled ? "true" : "false"));
            attributes.Add(new KeyValuePair<string, object?>("zharp.mechanism", mechanism));
            if (Settings.CaptureSourceContext)
            {
                string? context = SourceContext.Of(frames);
                if (context != null) attributes.Add(new KeyValuePair<string, object?>(SourceContext.Attribute, context));
            }
            string? buildId = SourceContext.BuildIdOf(frames);
            if (buildId != null) attributes.Add(new KeyValuePair<string, object?>(SourceContext.BuildIdAttribute, buildId));
            attributes.AddRange(Users.AttributesFor(span));
            Emit(level, attributes, span);
            Errors.NoteReported(span);
        }

        static string? FindCode(Exception error)
        {
            for (Exception? e = error; e != null; e = e.InnerException)
            {
                if (e is System.Data.Common.DbException) return DbFailures.CodeOf(e);
            }
            return null;
        }

        /// <summary>The span the record being emitted belongs to: the logger takes Activity.Current, which can't be a span that ended.</summary>
        [ThreadStatic] internal static Activity? EmittingFor;

        void Emit(LogLevel level, List<KeyValuePair<string, object?>> attributes, Activity? span)
        {
            string type = "", message = "";
            foreach (var kv in attributes)
            {
                if (kv.Key == "exception.type") type = kv.Value as string ?? "";
                else if (kv.Key == "exception.message") message = kv.Value as string ?? "";
            }
            string body = message.Length > 0 ? type + ": " + message : type;
            Activity? saved = EmittingFor;
            EmittingFor = span;
            try
            {
                errorLogger.Log(level, default, attributes, null, (_, _) => body);
            }
            finally
            {
                EmittingFor = saved;
            }
        }

        // ── process events ──────────────────────────────────────────────

        void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception error)
            {
                Report(error, handled: false, mechanism: "uncaught", span: Activity.Current, level: e.IsTerminating ? LogLevel.Critical : LogLevel.Error);
            }
            if (e.IsTerminating) Flush(5000);
        }

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            Exception error = e.Exception.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception;
            Report(error, handled: false, mechanism: "unobserved-task", span: null);
        }

        void OnExit(object? sender, EventArgs e) => Flush(3000);

        /// <summary>Send everything waiting, within <paramref name="timeoutMilliseconds"/>.</summary>
        public bool Flush(int timeoutMilliseconds)
        {
            try
            {
                var watch = Stopwatch.StartNew();
                bool traces = tracer?.ForceFlush(timeoutMilliseconds) ?? true;
                int left = Math.Max(0, timeoutMilliseconds - (int)watch.ElapsedMilliseconds);
                bool logs = ZharpLogProcessor.Instance?.ForceFlush(left) ?? true;
                return traces && logs;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            Flush(5000);
            disposed = true;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            AppDomain.CurrentDomain.ProcessExit -= OnExit;
            try { tracer?.Dispose(); } catch { /* shutting down */ }
            try { LoggerFactory.Dispose(); } catch { /* shutting down */ }
            lock (StartLock)
            {
                if (Current == this) Current = null;
            }
        }

        public static void Announce(string message)
        {
            try { Console.Error.WriteLine("[Zharp] " + message); } catch { /* no console */ }
        }

        public void DebugLog(string message)
        {
            if (Settings.Debug) Announce(message);
        }
    }
}
