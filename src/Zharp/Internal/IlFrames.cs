using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Zharp.Internal
{
    /// <summary>An app's stack: its text, and where to find each line-less frame of the app's own code.</summary>
    internal sealed class AppStack
    {
        public string Stacktrace = "";
        /// <summary><c>zharp.il_frames</c>, or null when every app frame has its line.</summary>
        public string? IlFrames;
        /// <summary>The MVID of the innermost app frame's module: the build's key on apps (see <see cref="IlFrames"/>).</summary>
        public string? BuildId;
    }

    /// <summary>
    /// Stacks from apps, which ship without PDBs (trimmed, AOT-compiled for iOS, or just never
    /// copied): their frames have no file or line. For those frames of the app's own code the SDK
    /// sends what the build's PDB needs to find the line — the method's metadata token, the IL
    /// offset in it and the module's MVID — and <c>Zharp.Build</c> uploads each module's PDB under its
    /// MVID (<c>portable-pdb</c>). The MVID is read from the loaded module, so it is there on Android
    /// and iOS too, where the assembly isn't a file the PDB ID could be read from.
    ///
    /// The stack text is written here, frame by frame from the same frames, so that the n-th frame
    /// line of the text is the n-th frame named in <c>zharp.il_frames</c>:
    /// <code>[{"frame":0,"module":"Shop.dll","mvid":"…","token":100663310,"il_offset":28}]</code>
    /// It reads as .NET writes it — <c>at Ns.Type.Method(Int32 id) in /src/Cart.cs:line 12</c>, inner
    /// exceptions first after <c>---&gt;</c> — always in English.
    /// </summary>
    internal static class IlFrames
    {
        public const string Attribute = "zharp.il_frames";
        const int MaxEntries = 100;
        static readonly PropertyInfo? ForeignBoundary =
            typeof(StackFrame).GetProperty("IsLastFrameFromForeignExceptionStackTrace", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static AppStack Describe(Exception error)
        {
            var stack = new AppStack();
            var entries = new List<string>();
            int index = 0;
            var sb = new StringBuilder();
            Write(error, 0, sb, entries, ref index, stack);
            stack.Stacktrace = sb.ToString();
            if (entries.Count > 0) stack.IlFrames = "[" + string.Join(",", entries) + "]";
            return stack;
        }

        static void Write(Exception error, int depth, StringBuilder sb, List<string> entries, ref int index, AppStack stack)
        {
            sb.Append(Stacks.TypeName(error));
            string message = error.Message ?? "";
            if (message.Length > 0) sb.Append(": ").Append(message);
            Exception? inner = error.InnerException;
            if (inner != null && depth < 10)
            {
                sb.Append(" ---> ");
                Write(inner, depth + 1, sb, entries, ref index, stack);
                sb.Append('\n').Append("   --- End of inner exception stack trace ---");
            }
            StackFrame[] frames;
            try { frames = new StackTrace(error, true).GetFrames() ?? Array.Empty<StackFrame>(); }
            catch { frames = Array.Empty<StackFrame>(); }
            foreach (StackFrame frame in frames)
            {
                MethodBase? method;
                try { method = frame.GetMethod(); } catch { method = null; }
                if (method == null || !Shown(method)) continue;
                sb.Append('\n').Append("   at ").Append(Name(method));
                string? file = null;
                int line = 0;
                try
                {
                    file = frame.GetFileName();
                    line = frame.GetFileLineNumber();
                }
                catch
                {
                    // No PDB.
                }
                if (!string.IsNullOrEmpty(file) && line > 0)
                {
                    sb.Append(" in ").Append(file).Append(":line ").Append(line);
                    if (stack.BuildId == null && IsAppMethod(method)) stack.BuildId = Mvid(method);
                }
                else if (IsAppMethod(method))
                {
                    string? mvid = Mvid(method);
                    if (stack.BuildId == null) stack.BuildId = mvid;
                    int offset = frame.GetILOffset();
                    int token = Token(method);
                    if (mvid != null && token != 0 && entries.Count < MaxEntries)
                    {
                        entries.Add("{\"frame\":" + index + ",\"module\":" + Json.Quote(ModuleName(method)) + ",\"mvid\":" + Json.Quote(mvid) +
                                    ",\"token\":" + token + ",\"il_offset\":" + (offset >= 0 ? offset : -1) + "}");
                    }
                }
                index++;
                if (IsForeignBoundary(frame)) sb.Append('\n').Append("--- End of stack trace from previous location ---");
            }
        }

        /// <summary>The app's own code: not the runtime, the platform or a well-known library.</summary>
        public static bool IsAppMethod(MethodBase method)
        {
            string? type = method.DeclaringType?.FullName;
            if (type != null && Stacks.IsLibraryType(type)) return false;
            return !IsPlatformAssembly(method);
        }

        /// <summary>
        /// Signed by Microsoft (the runtime, WPF, Windows Forms, MAUI) or by Xamarin/Mono (Android and
        /// iOS bindings): never the app's code, whatever its namespaces (WPF's MS.Internal…).
        /// </summary>
        static readonly string[] PlatformKeys =
        {
            "b03f5f7f11d50a3a", "b77a5c561934e089", "31bf3856ad364e35", "cc7b13ffcd2ddd51", "7cec85d7bea7798e",
            "adb9793829ddae60", "0738eb9f132ed756", "84e04ff9cfb79065",
        };

        static readonly System.Collections.Concurrent.ConcurrentDictionary<Assembly, bool> Platform = new System.Collections.Concurrent.ConcurrentDictionary<Assembly, bool>();

        static bool IsPlatformAssembly(MethodBase method)
        {
            Assembly assembly;
            try { assembly = method.Module.Assembly; }
            catch { return false; }
            return Platform.GetOrAdd(assembly, a =>
            {
                try
                {
                    byte[]? token = a.GetName().GetPublicKeyToken();
                    if (token == null || token.Length == 0) return false;
                    var hex = new StringBuilder(16);
                    foreach (byte b in token) hex.Append(b.ToString("x2"));
                    return Array.IndexOf(PlatformKeys, hex.ToString()) >= 0;
                }
                catch
                {
                    return false;
                }
            });
        }

        static string? Mvid(MethodBase method)
        {
            try { return method.Module.ModuleVersionId.ToString("D"); }
            catch { return null; }
        }

        static int Token(MethodBase method)
        {
            try { return method.MetadataToken; }
            catch { return 0; }
        }

        static string ModuleName(MethodBase method)
        {
            try { return method.Module.Name; }
            catch { return ""; }
        }

        static bool IsForeignBoundary(StackFrame frame)
        {
            try { return ForeignBoundary?.GetValue(frame) is bool b && b; }
            catch { return false; }
        }

        /// <summary>As .NET decides: [StackTraceHidden] methods and types, and aggressively inlined methods, aren't listed.</summary>
        static bool Shown(MethodBase method)
        {
            try
            {
                if ((method.MethodImplementationFlags & MethodImplAttributes.AggressiveInlining) != 0) return false;
                if (HasHidden(method.GetCustomAttributesData())) return false;
                Type? type = method.DeclaringType;
                if (type != null && HasHidden(type.GetCustomAttributesData())) return false;
            }
            catch
            {
                // Shown.
            }
            return true;
        }

        static bool HasHidden(IList<CustomAttributeData> attributes)
        {
            foreach (CustomAttributeData a in attributes)
            {
                if (a.AttributeType.FullName == "System.Diagnostics.StackTraceHiddenAttribute") return true;
            }
            return false;
        }

        /// <summary><c>Ns.Type.Method[T](Int32 id, String name)</c>, async and iterator state machines as the method written.</summary>
        static string Name(MethodBase method)
        {
            var sb = new StringBuilder();
            Type? type = method.DeclaringType;
            string? suffix = null;
            MethodBase shown = method;
            if (type != null && IsCompilerGenerated(type))
            {
                bool isAsync = typeof(IAsyncStateMachine).IsAssignableFrom(type);
                if (isAsync || typeof(IEnumerator).IsAssignableFrom(type))
                {
                    MethodBase? original = StateMachineOwner(type);
                    if (original != null)
                    {
                        shown = original;
                        type = original.DeclaringType;
                        if (!isAsync) suffix = "+" + method.Name + "()";
                    }
                }
            }
            if (type != null)
            {
                string full = type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition().FullName ?? type.Name : type.FullName ?? type.Name;
                sb.Append(full.Replace('+', '.')).Append('.');
            }
            sb.Append(shown.Name);
            if (shown is MethodInfo mi && mi.IsGenericMethod)
            {
                Type[] args = mi.GetGenericArguments();
                sb.Append('[');
                for (int i = 0; i < args.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(args[i].Name);
                }
                sb.Append(']');
            }
            sb.Append('(');
            try
            {
                ParameterInfo[] parameters = shown.GetParameters();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(parameters[i].ParameterType.Name);
                    if (!string.IsNullOrEmpty(parameters[i].Name)) sb.Append(' ').Append(parameters[i].Name);
                }
            }
            catch
            {
                // Parameters not readable (trimmed metadata).
            }
            sb.Append(')');
            if (suffix != null) sb.Append(suffix);
            return sb.ToString();
        }

        static bool IsCompilerGenerated(Type type)
        {
            try { return type.IsDefined(typeof(CompilerGeneratedAttribute), false); }
            catch { return false; }
        }

        /// <summary>The method whose [AsyncStateMachine] or [IteratorStateMachine] names this state machine.</summary>
        static MethodBase? StateMachineOwner(Type stateMachine)
        {
            Type? parent = stateMachine.DeclaringType;
            if (parent == null) return null;
            if (stateMachine.IsGenericType && !stateMachine.IsGenericTypeDefinition) stateMachine = stateMachine.GetGenericTypeDefinition();
            try
            {
                foreach (MethodInfo candidate in parent.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    foreach (StateMachineAttribute a in candidate.GetCustomAttributes<StateMachineAttribute>(false))
                    {
                        if (a.StateMachineType == stateMachine) return candidate;
                    }
                }
            }
            catch
            {
                // Not resolvable: the state machine's own name.
            }
            return null;
        }
    }
}
