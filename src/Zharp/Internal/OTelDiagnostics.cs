using System;
using System.Diagnostics.Tracing;
using System.Linq;

namespace Zharp.Internal
{
    /// <summary>
    /// With <c>ZHARP_DEBUG</c>: OpenTelemetry's own warnings and errors (an export that failed, a
    /// batch dropped) printed with Zharp's, so a setup that sends nothing says why.
    /// </summary>
    internal sealed class OTelDiagnostics : EventListener
    {
        static OTelDiagnostics? instance;

        public static void Start()
        {
            try { instance ??= new OTelDiagnostics(); }
            catch { /* no event listeners here */ }
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal)) EnableEvents(source, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            try
            {
                string text = e.Message ?? e.EventName ?? "";
                object?[] args = e.Payload?.ToArray() ?? Array.Empty<object?>();
                try { if (args.Length > 0) text = string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args); }
                catch { text += " " + string.Join(", ", args); }
                ZharpCore.Announce("otel " + e.Level.ToString().ToLowerInvariant() + ": " + text);
            }
            catch
            {
                // Never in the way.
            }
        }
    }
}
