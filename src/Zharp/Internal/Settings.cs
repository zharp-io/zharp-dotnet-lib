using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Zharp.Internal
{
    /// <summary>
    /// The settings in force: what the code set, then the environment (<c>ZHARP_*</c>), then the
    /// <c>"Zharp"</c> configuration section, then the defaults.
    /// </summary>
    internal sealed class Settings
    {
        public const string DefaultEndpoint = "https://api.zharp.io/api/v1/otlp";

        public string? Key { get; private set; }
        public string Service { get; private set; } = "dotnet-app";
        public string? Environment { get; private set; }
        public string? Release { get; private set; }
        public string Endpoint { get; private set; } = DefaultEndpoint;
        public bool Logs { get; private set; }
        public bool Tracing { get; private set; } = true;
        public bool LogCorrelation { get; private set; } = true;
        public bool CaptureHeaders { get; private set; } = true;
        public IReadOnlyList<string> RedactHeaders { get; private set; } = Array.Empty<string>();
        /// <summary>null (off), "errors" or "all".</summary>
        public string? CaptureBodies { get; private set; }
        public IReadOnlyList<string> RedactKeys { get; private set; } = Array.Empty<string>();
        public bool CaptureQueryErrors { get; private set; } = true;
        public bool CaptureSourceContext { get; private set; } = true;
        public bool CaptureUncaught { get; private set; } = true;
        public double SampleRate { get; private set; } = 1.0;
        public bool Debug { get; private set; }
        public bool Disabled { get; private set; }
        public IReadOnlyList<string> Sources { get; private set; } = Array.Empty<string>();

        /// <summary>For tests: where environment variables are read from.</summary>
        internal static Func<string, string?> EnvironmentReader = name =>
        {
            try { return System.Environment.GetEnvironmentVariable(name); }
            catch { return null; }
        };

        public bool HasKey => !string.IsNullOrEmpty(Key);

        public static Settings Resolve(ZharpOptions? code, IConfiguration? configuration)
        {
            code ??= new ZharpOptions();
            IConfiguration? section = configuration?.GetSection("Zharp");
            string? Conf(string name)
            {
                try { return Trim(section?[name]); }
                catch { return null; }
            }
            string? Str(string? fromCode, string conf, params string[] vars)
            {
                if (!string.IsNullOrWhiteSpace(fromCode)) return fromCode!.Trim();
                foreach (string v in vars)
                {
                    string? e = Env(v);
                    if (e != null) return e;
                }
                return Conf(conf);
            }
            bool Flag(bool? fromCode, string conf, string var, bool fallback) =>
                fromCode ?? ParseFlag(Env(var)) ?? ParseFlag(Conf(conf)) ?? fallback;

            var s = new Settings();
            s.Key = Str(code.Key, "Key", "ZHARP_KEY", "ZHARP_API_KEY") ?? Conf("ApiKey");
            s.Service = Str(code.Service, "Service", "ZHARP_SERVICE", "OTEL_SERVICE_NAME") ?? DefaultService();
            s.Environment = Str(code.Environment, "Environment", "ZHARP_ENVIRONMENT", "ZHARP_ENV")
                ?? Env("ASPNETCORE_ENVIRONMENT") ?? Env("DOTNET_ENVIRONMENT");
            s.Release = Str(code.Release, "Release", "ZHARP_RELEASE", "ZHARP_VERSION") ?? DefaultRelease();
            s.Endpoint = (Str(code.Endpoint, "Endpoint", "ZHARP_ENDPOINT") ?? DefaultEndpoint).TrimEnd('/');
            s.Logs = Flag(code.Logs, "Logs", "ZHARP_LOGS", false);
            s.Tracing = Flag(code.Tracing, "Tracing", "ZHARP_TRACING", true);
            s.LogCorrelation = Flag(code.LogCorrelation, "LogCorrelation", "ZHARP_LOG_CORRELATION", true);
            s.CaptureHeaders = Flag(code.CaptureHeaders, "CaptureHeaders", "ZHARP_CAPTURE_HEADERS", true);
            s.RedactHeaders = code.RedactHeaders.Concat(List(Env("ZHARP_CAPTURE_HEADERS_REDACT") ?? Conf("RedactHeaders"))).ToList();
            s.CaptureBodies = BodyMode(Str(code.CaptureBodies, "CaptureBodies", "ZHARP_CAPTURE_BODIES"));
            s.RedactKeys = code.RedactKeys.Concat(List(Env("ZHARP_CAPTURE_BODIES_REDACT") ?? Conf("RedactKeys"))).ToList();
            s.CaptureQueryErrors = Flag(code.CaptureQueryErrors, "CaptureQueryErrors", "ZHARP_CAPTURE_QUERY_ERRORS", true);
            s.CaptureSourceContext = Flag(code.CaptureSourceContext, "CaptureSourceContext", "ZHARP_CAPTURE_SOURCE_CONTEXT", true);
            s.CaptureUncaught = Flag(code.CaptureUncaught, "CaptureUncaught", "ZHARP_CAPTURE_UNCAUGHT", true);
            s.Debug = Flag(code.Debug, "Debug", "ZHARP_DEBUG", false);
            s.Disabled = Flag(code.Disabled, "Disabled", "ZHARP_DISABLED", false);
            double rate = code.SampleRate ?? ParseDouble(Env("ZHARP_SAMPLE_RATE")) ?? ParseDouble(Conf("SampleRate")) ?? 1.0;
            s.SampleRate = Math.Min(1.0, Math.Max(0.0, rate));
            s.Sources = code.Sources.ToList();
            return s;
        }

        static string? Env(string name) => Trim(EnvironmentReader(name));

        static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        public static bool? ParseFlag(string? value)
        {
            if (value == null) return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": return true;
                case "false": case "0": case "no": case "off": case "none": return false;
                default: return null;
            }
        }

        static double? ParseDouble(string? value) =>
            value != null && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : (double?)null;

        static IEnumerable<string> List(string? value) =>
            value == null ? Enumerable.Empty<string>() : value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0);

        /// <summary>"true" is "errors", as in Node.</summary>
        static string? BodyMode(string? value)
        {
            if (value == null) return null;
            string v = value.Trim().ToLowerInvariant();
            if (v == "all") return "all";
            if (v == "errors" || ParseFlag(v) == true) return "errors";
            return null;
        }

        static Assembly? Entry()
        {
            try { return Assembly.GetEntryAssembly(); }
            catch { return null; }
        }

        /// <summary>The service name when there's no entry assembly (IIS: the site's name).</summary>
        internal static string? FallbackService;

        /// <summary>The release when there's no entry assembly (MAUI on Android and iOS: the app's version).</summary>
        internal static string? FallbackRelease;

        /// <summary>Apps whose platform names them better than the entry assembly (MAUI: the bundle ID and "1.4.0 (212)").</summary>
        internal static bool PreferFallback;

        static string DefaultService() => (PreferFallback ? FallbackService : null) ?? Entry()?.GetName().Name ?? FallbackService ?? "dotnet-app";

        /// <summary>The entry assembly's informational version: .NET 8+ SDKs add the commit after a '+'.</summary>
        static string? DefaultRelease()
        {
            if (PreferFallback && FallbackRelease != null) return FallbackRelease;
            Assembly? entry = Entry();
            if (entry == null) return FallbackRelease;
            try
            {
                string? info = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info!.Trim();
                return entry.GetName().Version?.ToString();
            }
            catch
            {
                return null;
            }
        }

        public string DescribeTarget() =>
            $"service \"{Service}\", " + (Environment != null ? $"environment \"{Environment}\"" : "no environment set");
    }
}
