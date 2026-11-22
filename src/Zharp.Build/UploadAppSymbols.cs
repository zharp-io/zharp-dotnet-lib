using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Zharp.Build
{
    /// <summary>
    /// For apps (MAUI, WPF, Windows Forms): each of the app's own assemblies' portable PDB, uploaded
    /// as a <c>portable-pdb</c> artifact under the assembly's MVID, so Zharp can find the lines of
    /// frames that have none (the PDBs don't ship with the app; iOS is AOT-compiled; trimmed builds
    /// are rewritten), from the method token and IL offset the SDK sends. The project's sources go
    /// up as a source bundle under the same MVID: apps never embed their sources, which would ship
    /// to every user.
    ///
    /// Trimming rewrites assemblies (new MVIDs, new tokens and offsets, new PDBs): the trimmed
    /// copies are uploaded too, by a second run after ILLink.
    /// </summary>
    public sealed class UploadAppSymbols : Task
    {
        const long MaxFileBytes = 2_000_000;
        const int MaxFiles = 20_000;

        [Required] public ITaskItem[] Assemblies { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();
        [Required] public string ProjectDirectory { get; set; } = "";
        public bool UploadSources { get; set; } = true;
        public string? Release { get; set; }
        public string? AppId { get; set; }
        public string? AuthToken { get; set; }
        public string Url { get; set; } = "https://api.zharp.io";

        /// <summary>The MVIDs uploaded for (for tests and other targets).</summary>
        [Output] public string[] BuildIds { get; set; } = Array.Empty<string>();

        public override bool Execute()
        {
            var done = new List<string>();
            Dictionary<string, string>? files = null;
            foreach (ITaskItem item in Assemblies)
            {
                string assembly = item.GetMetadata("FullPath");
                if (string.IsNullOrEmpty(assembly)) assembly = item.ItemSpec;
                try
                {
                    if (!File.Exists(assembly)) continue;
                    AppSymbols.Module? module = AppSymbols.Read(assembly);
                    if (module == null)
                    {
                        Log.LogMessage(MessageImportance.Normal, "Zharp: {0} is not a .NET assembly.", assembly);
                        continue;
                    }
                    if (done.Contains(module.Mvid)) continue;
                    done.Add(module.Mvid);
                    if (module.Pdb == null)
                    {
                        Log.LogMessage(MessageImportance.High, "Zharp: no portable PDB for {0} (DebugType none or full?): its frames will have no lines.", Path.GetFileName(assembly));
                        continue;
                    }
                    if (UploadSources) files ??= Collect();
                    if (string.IsNullOrWhiteSpace(AuthToken))
                    {
                        Log.LogMessage(MessageImportance.High, "Zharp: dry run (no ZHARP_AUTH_TOKEN / ZharpAuthToken): would upload {0} as kind=portable-pdb debugId={1}{2}",
                            Path.GetFileName(module.PdbName), module.Mvid, files != null ? $", and {files.Count} source file(s) as kind=source-bundle" : "");
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(AppId))
                    {
                        Log.LogWarning("Zharp: set ZHARP_APP_ID (or the ZharpAppId property) to upload the app's PDBs; a token is set but no application ID.");
                        return true;
                    }
                    Upload("portable-pdb", module.Mvid, module.PdbName, Gzip(module.Pdb));
                    if (files != null) Upload("source-bundle", module.Mvid, "sources.json.gz", SourceBundles.Bundle(files));
                    Log.LogMessage(MessageImportance.High, "Zharp: uploaded {0}{1} for build {2}.", module.PdbName, files != null ? $" and {files.Count} source file(s)" : "", module.Mvid);
                }
                catch (Exception e)
                {
                    // A failed upload never fails the build: the app still works, its stacks just have no lines.
                    Log.LogWarning("Zharp: symbols for {0} not uploaded: {1}", Path.GetFileName(assembly), e.Message);
                }
            }
            BuildIds = done.ToArray();
            return true;
        }

        Dictionary<string, string> Collect()
        {
            string root = Path.GetFullPath(ProjectDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string folder = Path.GetFileName(root);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ITaskItem item in Sources)
            {
                if (files.Count >= MaxFiles) break;
                string path = Path.GetFullPath(item.ItemSpec);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxFileBytes) continue;
                string relative = path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? path.Substring(root.Length + 1)
                    : Path.GetFileName(path);
                files[(folder + "/" + relative).Replace('\\', '/')] = File.ReadAllText(path);
            }
            return files;
        }

        static byte[] Gzip(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(bytes, 0, bytes.Length);
            return output.ToArray();
        }

        void Upload(string kind, string debugId, string name, byte[] gzipped)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(kind), "kind");
            form.Add(new StringContent(debugId), "debugId");
            if (!string.IsNullOrWhiteSpace(Release)) form.Add(new StringContent(Release!), "release");
            form.Add(new StringContent(name), "name");
            var file = new ByteArrayContent(gzipped);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            form.Add(file, "file", name + ".gz");
            string target = Url.TrimEnd('/') + "/api/v1/applications/" + Uri.EscapeDataString(AppId!.Trim()) + "/artifacts";
            using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AuthToken!.Trim());
            using HttpResponseMessage response = http.SendAsync(request).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException($"{(int)response.StatusCode} from {target}: {(body.Length > 300 ? body.Substring(0, 300) : body)}");
            }
        }
    }

    /// <summary>An app assembly's MVID and portable PDB, apart from MSBuild (tests load this without it).</summary>
    public static class AppSymbols
    {
        public sealed class Module
        {
            public string Mvid = "";
            public string PdbName = "";
            public byte[]? Pdb;
        }

        /// <summary>The module's MVID, as the runtime reports it (Module.ModuleVersionId).</summary>
        public static string? Mvid(string assembly) => Read(assembly)?.Mvid;

        public static Module? Read(string assembly)
        {
            byte[] image = File.ReadAllBytes(assembly);
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (!pe.HasMetadata) return null;
            MetadataReader metadata = pe.GetMetadataReader();
            var module = new Module
            {
                Mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D"),
                PdbName = Path.GetFileNameWithoutExtension(assembly) + ".pdb",
            };
            Guid? pdbGuid = null;
            foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    // <DebugType>embedded</DebugType>: the PDB is inside the assembly.
                    // Its data: "MPDB", the PDB's size, then the PDB deflated.
                    int read = Math.Max(0, Math.Min(entry.DataSize, image.Length - entry.DataPointer));
                    byte[] data = new byte[read];
                    Array.Copy(image, entry.DataPointer, data, 0, read);
                    if (read < 8 || data[0] != (byte)'M' || data[1] != (byte)'P' || data[2] != (byte)'D' || data[3] != (byte)'B') return module;
                    int size = BitConverter.ToInt32(data, 4);
                    using (var input = new DeflateStream(new MemoryStream(data, 8, read - 8), CompressionMode.Decompress))
                    using (var output = new MemoryStream(size))
                    {
                        input.CopyTo(output);
                        module.Pdb = output.ToArray();
                    }
                    return module;
                }
                if (entry.Type == DebugDirectoryEntryType.CodeView && pdbGuid == null)
                {
                    pdbGuid = pe.ReadCodeViewDebugDirectoryData(entry).Guid;
                }
            }
            if (pdbGuid == null) return module;
            string path = Path.ChangeExtension(assembly, ".pdb");
            if (!File.Exists(path)) return module;
            byte[] file = File.ReadAllBytes(path);
            if (!IsPortablePdbFor(file, pdbGuid.Value)) return module;
            module.Pdb = file;
            return module;
        }

        /// <summary>A portable PDB ("BSJB") whose ID starts with the assembly's CodeView GUID: the one this build wrote.</summary>
        static bool IsPortablePdbFor(byte[] file, Guid guid)
        {
            if (file.Length < 4 || file[0] != (byte)'B' || file[1] != (byte)'S' || file[2] != (byte)'J' || file[3] != (byte)'B') return false;
            try
            {
                using var provider = MetadataReaderProvider.FromPortablePdbImage(System.Collections.Immutable.ImmutableArray.Create(file));
                MetadataReader reader = provider.GetMetadataReader();
                BlobContentId id = new BlobContentId(reader.DebugMetadataHeader!.Id);
                return id.Guid == guid;
            }
            catch
            {
                return false;
            }
        }
    }
}
