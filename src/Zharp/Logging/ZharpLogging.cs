using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Zharp.Internal;

namespace Zharp.Logging
{
    /// <summary>
    /// The app's loggers into Zharp's pipeline: with <c>ZHARP_LOGS=true</c> every line is sent; either
    /// way an exception logged at Error is reported (once) as an error.
    /// </summary>
    [ProviderAlias("Zharp")]
    internal sealed class ZharpLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            ZharpCore.Current?.LoggerFactory.CreateLogger(categoryName) ?? NullLogger.Instance;

        /// <summary>Zharp's pipeline outlives any one factory.</summary>
        public void Dispose() { }
    }

    /// <summary>Which console formatter the stamp wraps: the one the app chose.</summary>
    internal sealed class ZharpConsoleChoice
    {
        public string Inner = ConsoleFormatterNames.Simple;
    }

    /// <summary>
    /// The agent stamp: the app's console formatter (simple, json or systemd) with
    /// <c> trace_id=… span_id=…</c> at the end of each line written during a request — two fields
    /// on a JSON line — so the Zharp agent links the lines it collects to their traces even with
    /// logs off. <c>ZHARP_LOG_CORRELATION=false</c> turns it off.
    /// </summary>
    internal sealed class ZharpConsoleFormatter : ConsoleFormatter
    {
        public const string FormatterName = "zharp";
        readonly IServiceProvider services;
        readonly ZharpConsoleChoice choice;
        ConsoleFormatter? inner;

        public ZharpConsoleFormatter(IServiceProvider services, ZharpConsoleChoice choice) : base(FormatterName)
        {
            this.services = services;
            this.choice = choice;
        }

        ConsoleFormatter? Inner()
        {
            if (inner != null) return inner;
            List<ConsoleFormatter> all = services.GetServices<ConsoleFormatter>().Where(f => !(f is ZharpConsoleFormatter)).ToList();
            inner = all.FirstOrDefault(f => string.Equals(f.Name, choice.Inner, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(f => f.Name == ConsoleFormatterNames.Simple);
            return inner;
        }

        public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
        {
            ConsoleFormatter? f = Inner();
            if (f == null) return;
            Activity? span = Activity.Current;
            if (span == null || span.TraceId == default)
            {
                f.Write(logEntry, scopeProvider, textWriter);
                return;
            }
            using var buffer = new StringWriter();
            f.Write(logEntry, scopeProvider, buffer);
            string text = buffer.ToString();
            if (text.Length == 0) return;
            textWriter.Write(Stamp(text, span.TraceId.ToHexString(), span.SpanId.ToHexString()));
        }

        /// <summary>A line (or JSON object) with the ids added, its line ending kept.</summary>
        internal static string Stamp(string text, string traceId, string spanId)
        {
            int end = text.Length;
            while (end > 0 && (text[end - 1] == '\n' || text[end - 1] == '\r')) end--;
            string body = text.Substring(0, end);
            string ending = text.Substring(end);
            string trimmed = body.TrimEnd();
            if (trimmed.EndsWith("}", StringComparison.Ordinal) && body.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                int close = body.LastIndexOf('}');
                string before = body.Substring(0, close).TrimEnd();
                string comma = before.EndsWith("{", StringComparison.Ordinal) ? "" : ",";
                return before + comma + "\"trace_id\":\"" + traceId + "\",\"span_id\":\"" + spanId + "\"" + body.Substring(close) + ending;
            }
            return trimmed + " trace_id=" + traceId + " span_id=" + spanId + ending;
        }
    }

    /// <summary>Points the console logger at the stamping formatter, remembering the app's own.</summary>
    internal sealed class ZharpConsoleOptionsSetup : IPostConfigureOptions<ConsoleLoggerOptions>
    {
        readonly ZharpConsoleChoice choice;

        public ZharpConsoleOptionsSetup(ZharpConsoleChoice choice)
        {
            this.choice = choice;
        }

        public void PostConfigure(string? name, ConsoleLoggerOptions options)
        {
            if (options.FormatterName == ZharpConsoleFormatter.FormatterName) return;
            choice.Inner = string.IsNullOrEmpty(options.FormatterName) ? ConsoleFormatterNames.Simple : options.FormatterName!;
            options.FormatterName = ZharpConsoleFormatter.FormatterName;
        }
    }

    internal static class ZharpLoggingSetup
    {
        /// <summary>The provider, and the stamp on console lines unless turned off.</summary>
        public static void Add(ILoggingBuilder builder, bool stamp)
        {
            IServiceCollection services = builder.Services;
            if (services.Any(d => d.ServiceType == typeof(ZharpConsoleChoice))) return;
            services.AddSingleton<ZharpConsoleChoice>();
            services.AddSingleton<ILoggerProvider, ZharpLoggerProvider>();
            if (!stamp) return;
            builder.AddConsoleFormatter<ZharpConsoleFormatter, ConsoleFormatterOptions>();
            services.AddSingleton<IPostConfigureOptions<ConsoleLoggerOptions>, ZharpConsoleOptionsSetup>();
        }
    }
}
