using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Zharp.Internal
{
    /// <summary>What an error report says about an exception.</summary>
    internal sealed class ExceptionInfo
    {
        public string Type = "";
        public string Message = "";
        public string Stacktrace = "";
    }

    /// <summary>
    /// An exception written as .NET writes it — <c>at Ns.Class.Method(Type arg) in /src/Cart.cs:line 12</c>,
    /// inner exceptions first with <c>---&gt;</c>, and <c>--- End of stack trace from previous location ---</c> —
    /// but always in English: the runtime's words ("at", "in", "line") are looked up in the UI
    /// culture, and on a German Windows read "bei … in …:Zeile 12", which no parser expects.
    /// </summary>
    internal static class Stacks
    {
        /// <summary>Namespaces that are never the app's own code (the same list as the backend's).</summary>
        static readonly string[] LibraryPrefixes =
        {
            "System.", "Microsoft.", "MS.", "Windows.", "Android.", "Java.", "Foundation.", "UIKit.", "ObjCRuntime.", "Npgsql.",
            "MySqlConnector.", "Newtonsoft.", "Grpc.", "Google.", "Amazon.", "Azure.", "OpenTelemetry.", "Zharp.",
            "Serilog.", "NLog.", "Dapper.", "Polly.", "MediatR.", "AutoMapper.", "FluentValidation.", "StackExchange.",
            "Swashbuckle.", "Hangfire.", "MassTransit.", "RabbitMQ.", "Confluent.", "MongoDB.", "Pomelo.", "Oracle.",
        };

        public static ExceptionInfo Describe(Exception error)
        {
            CultureInfo saved = CultureInfo.CurrentUICulture;
            bool swapped = false;
            try
            {
                if (!Equals(saved, CultureInfo.InvariantCulture))
                {
                    Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
                    swapped = true;
                }
                return new ExceptionInfo
                {
                    Type = TypeName(error),
                    Message = error.Message ?? "",
                    Stacktrace = Write(error, 0),
                };
            }
            finally
            {
                if (swapped) Thread.CurrentThread.CurrentUICulture = saved;
            }
        }

        public static string TypeName(Exception error) => error.GetType().FullName ?? error.GetType().Name;

        /// <summary>The text of Exception.ToString(), in English.</summary>
        static string Write(Exception error, int depth)
        {
            var sb = new StringBuilder();
            sb.Append(TypeName(error));
            string message = error.Message ?? "";
            if (message.Length > 0) sb.Append(": ").Append(message);
            Exception? inner = error.InnerException;
            if (inner != null && depth < 10)
            {
                sb.Append(" ---> ").Append(Write(inner, depth + 1));
                sb.Append('\n').Append("   --- End of inner exception stack trace ---");
            }
            string? stack = null;
            try { stack = error.StackTrace; } catch { /* a broken exception */ }
            if (!string.IsNullOrEmpty(stack)) sb.Append('\n').Append(stack!.Replace("\r\n", "\n"));
            return sb.ToString();
        }

        /// <summary>A frame of the app's own code: from a PDB (file and line), not a library's namespace.</summary>
        public static bool IsAppFrame(StackFrame frame)
        {
            string? file;
            try { file = frame.GetFileName(); } catch { return false; }
            if (string.IsNullOrEmpty(file) || frame.GetFileLineNumber() <= 0) return false;
            MethodBase? method = frame.GetMethod();
            string? type = method?.DeclaringType?.FullName;
            if (type == null) return true;
            return !IsLibraryType(type);
        }

        /// <summary>A type of the runtime, the platform or a well-known library: never the app's own code.</summary>
        public static bool IsLibraryType(string type)
        {
            foreach (string prefix in LibraryPrefixes)
            {
                if (type.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Each exception's frames, innermost exception first, as the stack text lists them.</summary>
        public static List<StackFrame> FramesOf(Exception error)
        {
            var chain = new List<Exception>();
            for (Exception? e = error; e != null && chain.Count < 10; e = e.InnerException) chain.Add(e);
            var frames = new List<StackFrame>();
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                try
                {
                    StackFrame[]? list = new StackTrace(chain[i], true).GetFrames();
                    if (list != null) frames.AddRange(list);
                }
                catch
                {
                    // No frames for this one.
                }
            }
            return frames;
        }
    }
}
