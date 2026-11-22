using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Zharp.Internal
{
    /// <summary>
    /// The code around an error's lines, read on the app's own server and sent with the error as
    /// <c>zharp.source_context</c>, the way the Node and Python SDKs send theirs.
    ///
    /// A build with <c>&lt;EmbedAllSources&gt;true&lt;/EmbedAllSources&gt;</c> carries its <c>.cs</c> files
    /// inside the PDB, so the code shown is exactly the code that ran, with nothing to upload. A
    /// source file still on disk at the path the PDB names (a development run) is read too. Only
    /// the app's own frames, the innermost five, five lines either side, each line capped.
    /// </summary>
    internal static class SourceContext
    {
        public const string Attribute = "zharp.source_context";
        public const string BuildIdAttribute = "zharp.build_id";
        const int Around = 5;
        const int MaxFrames = 5;
        const int MaxLine = 300;
        const int MaxBytes = 12_000;
        static readonly Guid EmbeddedSourceKind = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

        static readonly ConcurrentDictionary<string, ModuleSources?> Modules = new ConcurrentDictionary<string, ModuleSources?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The JSON for an error's frames (innermost first): [{file, line, pre, code, post}], or null.</summary>
        public static string? Of(IEnumerable<StackFrame> frames)
        {
            var output = new List<string>();
            var seen = new HashSet<string>();
            foreach (StackFrame frame in frames)
            {
                if (output.Count >= MaxFrames) break;
                try
                {
                    if (!Stacks.IsAppFrame(frame)) continue;
                    string file = frame.GetFileName()!;
                    int line = frame.GetFileLineNumber();
                    if (!seen.Add(file + ":" + line)) continue;
                    string[]? lines = LinesOf(frame.GetMethod()?.Module, file);
                    if (lines == null || line > lines.Length) continue;
                    var pre = new List<string>();
                    for (int n = Math.Max(1, line - Around); n < line; n++) pre.Add(Cut(lines[n - 1]));
                    var post = new List<string>();
                    for (int n = line + 1; n <= Math.Min(lines.Length, line + Around); n++) post.Add(Cut(lines[n - 1]));
                    output.Add("{\"file\":" + Json.Quote(file) + ",\"line\":" + line + ",\"pre\":" + Json.Array(pre) +
                               ",\"code\":" + Json.Quote(Cut(lines[line - 1])) + ",\"post\":" + Json.Array(post) + "}");
                }
                catch
                {
                    // No code for this frame.
                }
            }
            while (output.Count > 0)
            {
                string text = "[" + string.Join(",", output) + "]";
                if (Encoding.UTF8.GetByteCount(text) <= MaxBytes) return text;
                output.RemoveAt(output.Count - 1);
            }
            return null;
        }

        /// <summary>The ID of the PDB of the innermost app frame's module: the key of its build's source bundle.</summary>
        public static string? BuildIdOf(IEnumerable<StackFrame> frames)
        {
            foreach (StackFrame frame in frames)
            {
                try
                {
                    if (!Stacks.IsAppFrame(frame)) continue;
                    Module? module = frame.GetMethod()?.Module;
                    return module == null ? null : Sources(module)?.BuildId;
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        static string Cut(string text)
        {
            text = text.TrimEnd('\r', '\n');
            return text.Length <= MaxLine ? text : text.Substring(0, MaxLine - 1) + "…";
        }

        static string[]? LinesOf(Module? module, string file)
        {
            string? text = module == null ? null : Sources(module)?.Text(file);
            if (text == null && File.Exists(file))
            {
                try { text = File.ReadAllText(file); } catch { text = null; }
            }
            return text?.Replace("\r\n", "\n").Split('\n');
        }

        static ModuleSources? Sources(Module module)
        {
            string? path;
            try { path = module.Assembly.Location; } catch { path = null; }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return Modules.GetOrAdd(path!, p => ModuleSources.Open(p));
        }

        /// <summary>A module's PDB: its ID and the sources embedded in it, read on demand.</summary>
        sealed class ModuleSources
        {
            readonly MetadataReader? reader;
            readonly Dictionary<string, DocumentHandle> documents = new Dictionary<string, DocumentHandle>(StringComparer.OrdinalIgnoreCase);
            readonly ConcurrentDictionary<string, string?> texts = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            readonly object gate = new object();
            public string? BuildId { get; }

            // Kept open for the life of the process: they back `reader`.
            readonly PEReader? pe;
            readonly MetadataReaderProvider? provider;

            ModuleSources(PEReader pe, MetadataReaderProvider? provider, string? buildId)
            {
                this.pe = pe;
                this.provider = provider;
                BuildId = buildId;
                reader = provider?.GetMetadataReader();
                if (reader == null) return;
                foreach (DocumentHandle handle in reader.Documents)
                {
                    Document doc = reader.GetDocument(handle);
                    documents[reader.GetString(doc.Name)] = handle;
                }
            }

            public static ModuleSources? Open(string path)
            {
                try
                {
                    var pe = new PEReader(File.OpenRead(path));
                    string? buildId = null;
                    foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
                    {
                        if (entry.Type != DebugDirectoryEntryType.CodeView) continue;
                        buildId = pe.ReadCodeViewDebugDirectoryData(entry).Guid.ToString("D");
                        break;
                    }
                    MetadataReaderProvider? provider = null;
                    try
                    {
                        pe.TryOpenAssociatedPortablePdb(path, p => File.Exists(p) ? File.OpenRead(p) : null, out provider, out _);
                    }
                    catch
                    {
                        provider = null; // A Windows PDB, or none.
                    }
                    return new ModuleSources(pe, provider, buildId);
                }
                catch
                {
                    return null;
                }
            }

            public string? Text(string file) => texts.GetOrAdd(file, Read);

            string? Read(string file)
            {
                if (reader == null || !documents.TryGetValue(file, out DocumentHandle handle)) return null;
                lock (gate)
                {
                    foreach (CustomDebugInformationHandle h in reader.GetCustomDebugInformation(handle))
                    {
                        CustomDebugInformation info = reader.GetCustomDebugInformation(h);
                        if (reader.GetGuid(info.Kind) != EmbeddedSourceKind) continue;
                        return Decode(reader.GetBlobBytes(info.Value));
                    }
                }
                return null;
            }

            /// <summary>An int32 format (0: raw; otherwise the uncompressed size, deflated), then the bytes.</summary>
            static string Decode(byte[] blob)
            {
                int format = BitConverter.ToInt32(blob, 0);
                byte[] bytes;
                if (format == 0)
                {
                    bytes = new byte[blob.Length - 4];
                    Array.Copy(blob, 4, bytes, 0, bytes.Length);
                }
                else
                {
                    using var input = new MemoryStream(blob, 4, blob.Length - 4);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream(format);
                    deflate.CopyTo(output);
                    bytes = output.ToArray();
                }
                using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
        }
    }

    /// <summary>Just enough JSON writing for the SDK's own attributes, on every target.</summary>
    internal static class Json
    {
        public static string Quote(string? value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string Array(IEnumerable<string> values)
        {
            var parts = new List<string>();
            foreach (string v in values) parts.Add(Quote(v));
            return "[" + string.Join(",", parts) + "]";
        }
    }
}
