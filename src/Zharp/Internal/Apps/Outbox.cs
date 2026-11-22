using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Zharp.Internal.Apps
{
    /// <summary>
    /// Batches that couldn't be sent, kept on disk (the OTLP bytes exactly as they would have been
    /// POSTed) so they survive going offline, the app being closed, and crashes. Sent oldest first
    /// once sending works again. Capped by count, total size and age; when full, the oldest go.
    /// The same outbox as the Android and iOS SDKs.
    /// </summary>
    internal sealed class Outbox
    {
        readonly string dir;
        readonly int maxFiles;
        readonly long maxBytes;
        readonly TimeSpan maxAge;
        readonly Func<DateTime> clock;
        readonly object gate = new object();
        long sequence;

        public Outbox(string dir, int maxFiles = 200, long maxBytes = 8L * 1024 * 1024, TimeSpan? maxAge = null, Func<DateTime>? clock = null)
        {
            this.dir = dir;
            this.maxFiles = maxFiles;
            this.maxBytes = maxBytes;
            this.maxAge = maxAge ?? TimeSpan.FromDays(3);
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public string Directory => dir;

        /// <summary>Save a batch for <paramref name="signal"/> ("traces" or "logs"); its file, or null.</summary>
        public string? Save(string signal, byte[] body)
        {
            lock (gate)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                    long ms = (long)(clock() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                    string name = ms.ToString("D13", CultureInfo.InvariantCulture) + "-" + (Interlocked.Increment(ref sequence) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture) + "." + signal;
                    string path = Path.Combine(dir, name);
                    string temp = path + ".tmp";
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(body, 0, body.Length);
                        stream.Flush(true);
                    }
                    File.Move(temp, path);
                    Trim();
                    return path;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>What's waiting, oldest first, with anything over the caps already deleted.</summary>
        public List<string> Pending()
        {
            lock (gate)
            {
                try
                {
                    Trim();
                    return Files();
                }
                catch
                {
                    return new List<string>();
                }
            }
        }

        public static string? SignalOf(string path)
        {
            string ext = Path.GetExtension(path).TrimStart('.');
            return ext == "traces" || ext == "logs" ? ext : null;
        }

        public void Delete(string path)
        {
            try { File.Delete(path); } catch { /* gone already */ }
        }

        List<string> Files()
        {
            if (!System.IO.Directory.Exists(dir)) return new List<string>();
            return System.IO.Directory.GetFiles(dir)
                .Where(f => SignalOf(f) != null)
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
        }

        void Trim()
        {
            if (!System.IO.Directory.Exists(dir)) return;
            DateTime now = clock();
            // Leftovers of a write the process didn't live to finish.
            foreach (string temp in System.IO.Directory.GetFiles(dir, "*.tmp"))
            {
                try
                {
                    if (now - File.GetLastWriteTimeUtc(temp) > TimeSpan.FromMinutes(1)) File.Delete(temp);
                }
                catch
                {
                    // In use.
                }
            }
            var all = Files();
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = all.Count - 1; i >= 0; i--)
            {
                string name = Path.GetFileName(all[i]);
                int dash = name.IndexOf('-');
                if (dash > 0 && long.TryParse(name.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out long ms) && now - epoch.AddMilliseconds(ms) > maxAge)
                {
                    Delete(all[i]);
                    all.RemoveAt(i);
                }
            }
            long total = all.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            while (all.Count > 0 && (all.Count > maxFiles || total > maxBytes))
            {
                try { total -= new FileInfo(all[0]).Length; } catch { /* gone */ }
                Delete(all[0]);
                all.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// What OpenTelemetry's OTLP exporters send through, on apps: a batch that can't be sent now (no
    /// network, a 5xx, a 429) goes to the <see cref="Outbox"/> and is reported to the exporter as
    /// sent, so nothing waits in memory; after the next batch that goes through, the outbox is sent
    /// too. While the app crashes, each batch is written to disk before it is sent, so the crash is
    /// there on the next launch if the process dies first.
    ///
    /// It also answers OpenTelemetry's synchronous <c>Send</c> on Android and iOS, whose handlers
    /// only send asynchronously.
    /// </summary>
    internal sealed class OutboxHandler : HttpMessageHandler
    {
        readonly HttpMessageInvoker inner;
        readonly Outbox outbox;
        readonly Func<string> endpoint;
        readonly Func<string?> key;
        readonly Action<string> debug;
        int draining;

        /// <summary>Write each batch to disk before sending it: the process is crashing.</summary>
        public volatile bool PersistFirst;

        public OutboxHandler(Outbox outbox, Func<string> endpoint, Func<string?> key, Action<string> debug, HttpMessageHandler? network = null)
        {
            this.outbox = outbox;
            this.endpoint = endpoint;
            this.key = key;
            this.debug = debug;
            inner = new HttpMessageInvoker(network ?? new HttpClientHandler(), disposeHandler: true);
        }

#if NET
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.Run(() => SendAsync(request, cancellationToken)).GetAwaiter().GetResult();
#endif

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string signal = request.RequestUri != null && request.RequestUri.AbsolutePath.EndsWith("/v1/logs", StringComparison.Ordinal) ? "logs" : "traces";
            byte[] body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            string? saved = PersistFirst ? outbox.Save(signal, body) : null;
            try
            {
                using HttpRequestMessage copy = Copy(request, body);
                using HttpResponseMessage response = await inner.SendAsync(copy, cancellationToken).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status >= 200 && status < 300)
                {
                    debug("sent " + signal + " (" + body.Length + " bytes)");
                    if (saved != null) outbox.Delete(saved);
                    DrainLater();
                    return Sent();
                }
                if (Retryable(status))
                {
                    if (saved == null) outbox.Save(signal, body);
                    debug("kept for later (" + status + ")");
                    RetryLater();
                    return Sent();
                }
                // Refused for good (a bad key, a malformed batch): sending it again won't help.
                if (saved != null) outbox.Delete(saved);
                debug("refused: " + status);
                return new HttpResponseMessage(response.StatusCode);
            }
            catch (Exception e)
            {
                if (saved == null) outbox.Save(signal, body);
                debug("offline, kept for later: " + e.Message + (e.InnerException != null ? " (" + e.InnerException.Message + ")" : ""));
                RetryLater();
                return Sent();
            }
        }

        static bool Retryable(int status) => status == 408 || status == 429 || status >= 500;

        static HttpResponseMessage Sent() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };

        static HttpRequestMessage Copy(HttpRequestMessage request, byte[] body)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Content = new ByteArrayContent(body) };
            foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (request.Content != null)
            {
                foreach (var h in request.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            return copy;
        }

        Timer? retry;
        int retrySeconds = 15;

        /// <summary>A batch is waiting on disk: try again a little later (15 s, doubling to 5 minutes), not only after the next batch.</summary>
        void RetryLater()
        {
            lock (outbox)
            {
                if (retry != null) return;
                int seconds = retrySeconds;
                retrySeconds = Math.Min(300, retrySeconds * 2);
                retry = new Timer(_ =>
                {
                    lock (outbox)
                    {
                        retry?.Dispose();
                        retry = null;
                    }
                    DrainLater();
                }, null, seconds * 1000, Timeout.Infinite);
            }
        }

        /// <summary>Send what's in the outbox, in the background, once at a time.</summary>
        public void DrainLater()
        {
            if (Interlocked.CompareExchange(ref draining, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                try
                {
                    if (await Drain(CancellationToken.None).ConfigureAwait(false)) retrySeconds = 15;
                    else RetryLater();
                }
                catch (Exception e) { debug("outbox: " + e.Message); }
                finally { Interlocked.Exchange(ref draining, 0); }
            });
        }

        /// <summary>Send the outbox, oldest first, until one fails. True when it's empty.</summary>
        public async Task<bool> Drain(CancellationToken cancellationToken)
        {
            string? token = key();
            if (string.IsNullOrEmpty(token)) return false;
            foreach (string file in outbox.Pending())
            {
                string? signal = Outbox.SignalOf(file);
                if (signal == null) continue;
                byte[] body;
                try { body = File.ReadAllBytes(file); }
                catch { continue; }
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint() + "/v1/" + signal) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                try
                {
                    using HttpResponseMessage response = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    int status = (int)response.StatusCode;
                    if (Retryable(status))
                    {
                        debug("outbox: still not sending (" + status + ")");
                        return false;
                    }
                    outbox.Delete(file);
                    debug("sent from the outbox: " + Path.GetFileName(file) + " (" + status + ")");
                }
                catch (Exception e)
                {
                    debug("outbox: still offline: " + e.Message);
                    return false;
                }
            }
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
