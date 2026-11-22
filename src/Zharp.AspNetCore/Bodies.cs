using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Zharp.Internal;

namespace Zharp.AspNetCore
{
    /// <summary>
    /// <c>ZHARP_CAPTURE_BODIES</c>: request and response bodies on the request's span, as
    /// <c>http.request.body</c> and <c>http.response.body</c>, like the Node and Python SDKs. Text
    /// only (JSON, forms, XML, plain text), capped at 32 KB, with the values of secret-looking
    /// fields replaced by <c>[Filtered]</c>. <c>errors</c>: requests answered 400 and above;
    /// <c>all</c>: every request.
    /// </summary>
    internal sealed class BodiesStartupFilter : IStartupFilter
    {
        const int MaxChars = 32 * 1024;
        readonly ZharpCore core;

        public BodiesStartupFilter(ZharpCore core)
        {
            this.core = core;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(Capture);
            next(app);
        };

        async Task Capture(HttpContext context, Func<Task> next)
        {
            string? requestBody = null;
            bool requestTruncated = false;
            if (IsText(context.Request.ContentType))
            {
                try
                {
                    context.Request.EnableBuffering();
                    (requestBody, requestTruncated) = await ReadCapped(context.Request.Body, context.RequestAborted).ConfigureAwait(false);
                    context.Request.Body.Position = 0;
                }
                catch
                {
                    requestBody = null;
                }
            }
            Stream original = context.Response.Body;
            var tee = new TeeStream(original, MaxChars * 4);
            context.Response.Body = tee;
            bool threw = false;
            try
            {
                await next().ConfigureAwait(false);
            }
            catch
            {
                threw = true;
                throw;
            }
            finally
            {
                context.Response.Body = original;
                try
                {
                    int status = threw ? 500 : context.Response.StatusCode;
                    Activity? span = context.Features.Get<IHttpActivityFeature>()?.Activity;
                    if (span != null && (core.Settings.CaptureBodies == "all" || status >= 400))
                    {
                        if (requestBody != null) Set(span, "request", Redact(requestBody, context.Request.ContentType), requestTruncated);
                        if (IsText(context.Response.ContentType) && tee.Captured.Length > 0)
                        {
                            string text = Encoding.UTF8.GetString(tee.Captured.ToArray());
                            bool cut = tee.Overflowed || text.Length > MaxChars;
                            if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
                            Set(span, "response", Redact(text, context.Response.ContentType), cut);
                        }
                    }
                }
                catch (Exception e)
                {
                    core.DebugLog("bodies: " + e.Message);
                }
            }
        }

        static void Set(Activity span, string side, string body, bool truncated)
        {
            span.SetTag("http." + side + ".body", body);
            if (truncated) span.SetTag("http." + side + ".body.truncated", true);
        }

        static bool IsText(string? contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return false;
            string ct = contentType!.ToLowerInvariant();
            return ct.Contains("json") || ct.Contains("xml") || ct.StartsWith("text/", StringComparison.Ordinal) || ct.Contains("x-www-form-urlencoded") || ct.Contains("graphql");
        }

        static async Task<(string, bool)> ReadCapped(Stream body, CancellationToken token)
        {
            var buffer = new char[MaxChars + 1];
            using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await reader.ReadAsync(buffer.AsMemory(read, buffer.Length - read), token).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            bool truncated = read > MaxChars;
            return (new string(buffer, 0, Math.Min(read, MaxChars)), truncated);
        }

        /// <summary>Secret-looking JSON fields and form values as [Filtered]; other text as it is.</summary>
        internal string Redact(string body, string? contentType)
        {
            string ct = (contentType ?? "").ToLowerInvariant();
            if (ct.Contains("x-www-form-urlencoded")) return Secrets.RedactQuery(body);
            if (!ct.Contains("json")) return body;
            try
            {
                JsonNode? node = JsonNode.Parse(body);
                if (node == null) return body;
                Walk(node, core.Settings.RedactKeys);
                return node.ToJsonString();
            }
            catch (JsonException)
            {
                return body; // Cut short, or not JSON after all.
            }
        }

        static void Walk(JsonNode node, IReadOnlyList<string> extra)
        {
            if (node is JsonObject obj)
            {
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    if (Secrets.IsSecretKey(key, extra)) obj[key] = Secrets.Filtered;
                    else if (obj[key] is JsonNode child) Walk(child, extra);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (JsonNode? child in array)
                {
                    if (child != null) Walk(child, extra);
                }
            }
        }

        /// <summary>Writes through to the response, keeping a copy of the first bytes.</summary>
        sealed class TeeStream : Stream
        {
            readonly Stream inner;
            readonly int cap;
            public readonly MemoryStream Captured = new MemoryStream();
            public bool Overflowed;

            public TeeStream(Stream inner, int cap)
            {
                this.inner = inner;
                this.cap = cap;
            }

            void Keep(ReadOnlySpan<byte> data)
            {
                int room = cap - (int)Captured.Length;
                if (room <= 0) { Overflowed |= data.Length > 0; return; }
                if (data.Length > room) { Overflowed = true; data = data.Slice(0, room); }
                Captured.Write(data);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Keep(buffer.AsSpan(offset, count));
                inner.Write(buffer, offset, count);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Keep(buffer.Span);
                await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
