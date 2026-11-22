using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Zharp.Build
{
    /// <summary>
    /// Uploads the project's sources as a <c>source-bundle</c> artifact, keyed by the ID of the
    /// PDB this build wrote: gzipped JSON <c>{"files": {"&lt;Project&gt;/&lt;path&gt;": "&lt;text&gt;"}}</c>, the
    /// format the Android, iOS and Flutter build steps upload. Paths start with the project's
    /// folder, so Zharp matches them to frames by their trailing folders wherever the build ran.
    /// </summary>
    public sealed class UploadSourceBundle : Task
    {
        const long MaxFileBytes = 2_000_000;
        const int MaxFiles = 20_000;

        [Required] public string Assembly { get; set; } = "";
        public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();
        [Required] public string ProjectDirectory { get; set; } = "";
        public string? Release { get; set; }
        public string? AppId { get; set; }
        public string? AuthToken { get; set; }
        public string Url { get; set; } = "https://api.zharp.io";

        /// <summary>The build ID sent (for tests and other targets).</summary>
        [Output] public string? BuildId { get; set; }

        public override bool Execute()
        {
            try
            {
                BuildId = SourceBundles.PdbId(Assembly);
                if (BuildId == null)
                {
                    Log.LogMessage(MessageImportance.High, "Zharp: no PDB ID in {0} (DebugType none?): no source bundle.", Assembly);
                    return true;
                }
                Dictionary<string, string> files = Collect();
                if (string.IsNullOrWhiteSpace(AuthToken))
                {
                    Log.LogMessage(MessageImportance.High, "Zharp: dry run (no ZHARP_AUTH_TOKEN / ZharpAuthToken): would upload {0} source file(s) as kind=source-bundle debugId={1}", files.Count, BuildId);
                    return true;
                }
                if (string.IsNullOrWhiteSpace(AppId))
                {
                    Log.LogWarning("Zharp: set ZHARP_APP_ID (or the ZharpAppId property) to upload the source bundle; a token is set but no application ID.");
                    return true;
                }
                byte[] bundle = SourceBundles.Bundle(files);
                Upload(bundle);
                Log.LogMessage(MessageImportance.High, "Zharp: uploaded {0} source file(s) for build {1}.", files.Count, BuildId);
                return true;
            }
            catch (Exception e)
            {
                // A failed upload never fails the build: the app still works, its errors just show no code.
                Log.LogWarning("Zharp: source bundle not uploaded: {0}", e.Message);
                return true;
            }
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

        void Upload(byte[] bundle)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("source-bundle"), "kind");
            form.Add(new StringContent(BuildId!), "debugId");
            if (!string.IsNullOrWhiteSpace(Release)) form.Add(new StringContent(Release!), "release");
            form.Add(new StringContent("sources.json.gz"), "name");
            var file = new ByteArrayContent(bundle);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            form.Add(file, "file", "sources.json.gz");
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

    /// <summary>The bundle's format and the build's ID, apart from MSBuild (tests load this without it).</summary>
    public static class SourceBundles
    {
        /// <summary>The PDB's ID, as the runtime reads it from the module (CodeView GUID).</summary>
        public static string? PdbId(string assembly)
        {
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.CodeView) return pe.ReadCodeViewDebugDirectoryData(entry).Guid.ToString("D");
            }
            return null;
        }

        public static byte[] Bundle(Dictionary<string, string> files)
        {
            var json = new StringBuilder("{\"files\":{");
            bool first = true;
            foreach (var kv in files.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!first) json.Append(',');
                first = false;
                json.Append(Quote(kv.Key)).Append(':').Append(Quote(kv.Value));
            }
            json.Append("}}");
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json.ToString());
                gzip.Write(bytes, 0, bytes.Length);
            }
            return output.ToArray();
        }

        static string Quote(string value)
        {
            var sb = new StringBuilder(value.Length + 16).Append('"');
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
    }
}
