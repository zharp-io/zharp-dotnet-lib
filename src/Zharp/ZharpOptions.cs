using System;
using System.Collections.Generic;

namespace Zharp
{
    /// <summary>
    /// Zharp's settings. Each one left unset is read from the environment (<c>ZHARP_*</c>), then
    /// from the <c>"Zharp"</c> section of the app's configuration (appsettings.json), then takes
    /// its default — the same variables as the Node, Python and Java SDKs.
    /// </summary>
    public sealed class ZharpOptions
    {
        /// <summary>The app's ingest key (<c>ZHARP_KEY</c>, or <c>ZHARP_API_KEY</c>). Without one nothing is sent.</summary>
        public string? Key { get; set; }

        /// <summary>The service name (<c>ZHARP_SERVICE</c>, then <c>OTEL_SERVICE_NAME</c>). Default: the entry assembly's name.</summary>
        public string? Service { get; set; }

        /// <summary><c>ZHARP_ENVIRONMENT</c>, then <c>ZHARP_ENV</c>, then <c>ASPNETCORE_ENVIRONMENT</c> or <c>DOTNET_ENVIRONMENT</c>.</summary>
        public string? Environment { get; set; }

        /// <summary><c>ZHARP_RELEASE</c>. Default: the entry assembly's informational version (<c>1.4.0+3f2c9ab…</c>, commit included).</summary>
        public string? Release { get; set; }

        /// <summary><c>ZHARP_ENDPOINT</c>. Default <c>https://api.zharp.io/api/v1/otlp</c>.</summary>
        public string? Endpoint { get; set; }

        /// <summary>Send the app's logs (<c>ZHARP_LOGS</c>). Default off: the Zharp agent collects them from the app's output, linked by the stamp.</summary>
        public bool? Logs { get; set; }

        /// <summary>Send traces (<c>ZHARP_TRACING</c>). Default on. Off is the ".NET/HTTP" setup: errors (and logs) only.</summary>
        public bool? Tracing { get; set; }

        /// <summary>Add <c>trace_id=… span_id=…</c> to console log lines (<c>ZHARP_LOG_CORRELATION</c>). Default on.</summary>
        public bool? LogCorrelation { get; set; }

        /// <summary>Request and response headers on HTTP spans, secrets as <c>[Filtered]</c> (<c>ZHARP_CAPTURE_HEADERS</c>). Default on.</summary>
        public bool? CaptureHeaders { get; set; }

        /// <summary>More header names to filter (<c>ZHARP_CAPTURE_HEADERS_REDACT</c>, comma-separated).</summary>
        public IList<string> RedactHeaders { get; set; } = new List<string>();

        /// <summary>
        /// Request and response bodies on server spans (<c>ZHARP_CAPTURE_BODIES</c>): <c>"errors"</c>
        /// (status 400 and above), <c>"all"</c>, or null/<c>"off"</c> (the default). Secret-looking
        /// fields are filtered; text is capped at 32 KB.
        /// </summary>
        public string? CaptureBodies { get; set; }

        /// <summary>More body keys to filter, on top of the built-in secret names.</summary>
        public IList<string> RedactKeys { get; set; } = new List<string>();

        /// <summary>Report failed queries with their statement (<c>ZHARP_CAPTURE_QUERY_ERRORS</c>). Default on.</summary>
        public bool? CaptureQueryErrors { get; set; }

        /// <summary>Send the code around each error's lines, read from the build's embedded sources (<c>ZHARP_CAPTURE_SOURCE_CONTEXT</c>). Default on.</summary>
        public bool? CaptureSourceContext { get; set; }

        /// <summary>Report unhandled and unobserved exceptions (<c>ZHARP_CAPTURE_UNCAUGHT</c>). Default on.</summary>
        public bool? CaptureUncaught { get; set; }

        /// <summary>Share of traces sent, 0 to 1 (<c>ZHARP_SAMPLE_RATE</c>). Errors are always sent. Default 1.</summary>
        public double? SampleRate { get; set; }

        /// <summary>Print what Zharp does to stderr (<c>ZHARP_DEBUG</c>).</summary>
        public bool? Debug { get; set; }

        /// <summary>Start nothing (<c>ZHARP_DISABLED</c>).</summary>
        public bool? Disabled { get; set; }

        /// <summary>More ActivitySource names to trace (libraries that trace themselves, such as <c>"MySqlConnector"</c>).</summary>
        public IList<string> Sources { get; set; } = new List<string>();
    }
}
