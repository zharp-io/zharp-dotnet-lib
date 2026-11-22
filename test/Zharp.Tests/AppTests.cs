using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Shop.Checkout;
using Xunit;
using Zharp.Internal;
using Zharp.Internal.Apps;

namespace SdkTests
{
    public class AppStackTests
    {
        static Exception Thrown(Action action)
        {
            try { action(); } catch (Exception e) { return e; }
            throw new InvalidOperationException("did not throw");
        }

        static async Task<Exception> ThrownAsync(Func<Task> action)
        {
            try { await action(); } catch (Exception e) { return e; }
            throw new InvalidOperationException("did not throw");
        }

        static string[] FrameLines(string stack) =>
            stack.Split('\n').Where(l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal)).Select(l => l.Trim()).ToArray();

        static void Fail() => throw new InvalidOperationException("Cart is empty");

        [Fact]
        public void The_stack_reads_as_dotnet_writes_it()
        {
            Exception error = Thrown(() =>
            {
                try { Fail(); }
                catch (Exception inner) { throw new ApplicationException("Checkout failed", inner); }
            });
            AppStack app = IlFrames.Describe(error);
            string dotnet = Stacks.Describe(error).Stacktrace;
            Assert.StartsWith("System.ApplicationException: Checkout failed ---> System.InvalidOperationException: Cart is empty\n", app.Stacktrace);
            Assert.Contains("   --- End of inner exception stack trace ---", app.Stacktrace);
            Assert.Equal(FrameLines(dotnet), FrameLines(app.Stacktrace));
            // Every app frame here has its line (the tests' PDB is there): nothing to look up.
            Assert.Null(app.IlFrames);
        }

        [Fact]
        public async Task Async_frames_read_as_the_method_written_with_the_previous_location_marker()
        {
            Exception error = await ThrownAsync(async () =>
            {
                await Task.Yield();
                await ThrowLaterAsync();
            });
            AppStack app = IlFrames.Describe(error);
            string dotnet = Stacks.Describe(error).Stacktrace;
            Assert.Equal(FrameLines(dotnet), FrameLines(app.Stacktrace));
            Assert.Contains("--- End of stack trace from previous location ---", app.Stacktrace);
        }

        static async Task ThrowLaterAsync()
        {
            await Task.Yield();
            throw new KeyNotFoundException("Order 3 not found");
        }

        [Fact]
        public void Frames_without_lines_carry_their_token_il_offset_and_mvid()
        {
            Exception error = Thrown(() => Cart.Pay(new int[0]));
            AppStack app = IlFrames.Describe(error);
            Assert.NotNull(app.IlFrames);
            string[] lines = FrameLines(app.Stacktrace);
            Assert.Equal("at Shop.Checkout.Cart.Total(Int32[] prices)", lines[0]);
            Assert.Equal("at Shop.Checkout.Cart.Pay(Int32[] prices)", lines[1]);

            string mvid = typeof(Cart).Module.ModuleVersionId.ToString("D");
            int totalToken = typeof(Cart).GetMethod("Total")!.MetadataToken;
            int payToken = typeof(Cart).GetMethod("Pay")!.MetadataToken;
            Assert.Contains("{\"frame\":0,\"module\":\"NoPdbSample.dll\",\"mvid\":\"" + mvid + "\",\"token\":" + totalToken + ",\"il_offset\":", app.IlFrames);
            Assert.Contains("{\"frame\":1,\"module\":\"NoPdbSample.dll\",\"mvid\":\"" + mvid + "\",\"token\":" + payToken + ",\"il_offset\":", app.IlFrames);
            Assert.Matches("\"il_offset\":[1-9][0-9]*", app.IlFrames);
            // The test's own frame has its line: no entry for it.
            Assert.DoesNotContain("\"frame\":2,", app.IlFrames);
            // The build's key on apps: the innermost app frame's module.
            Assert.Equal(mvid, app.BuildId);
        }

        [Fact]
        public async Task An_async_frame_without_a_line_is_looked_up_in_its_state_machine()
        {
            Exception error = await ThrownAsync(() => Cart.PayLaterAsync(new int[0]));
            AppStack app = IlFrames.Describe(error);
            Assert.Contains("at Shop.Checkout.Cart.PayLaterAsync(Int32[] prices)", app.Stacktrace);
            // The state machine's MoveNext runs the code: its token is the one with sequence points.
            Type machine = typeof(Cart).GetNestedTypes(System.Reflection.BindingFlags.NonPublic).Single(t => t.Name.Contains("PayLaterAsync"));
            int moveNext = machine.GetMethod("MoveNext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.MetadataToken;
            Assert.Contains("\"token\":" + moveNext + ",", app.IlFrames);
        }

        [Fact]
        public void Zharp_Build_reads_the_mvid_and_the_pdb_the_runtime_needs()
        {
            string dir = Path.GetDirectoryName(typeof(Cart).Assembly.Location)!;
            string copy = Path.Combine(Path.GetTempPath(), "zharp-sym-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(copy);
            try
            {
                File.Copy(typeof(Cart).Assembly.Location, Path.Combine(copy, "NoPdbSample.dll"));
                File.Copy(Path.Combine(dir, "symbols", "NoPdbSample.pdb"), Path.Combine(copy, "NoPdbSample.pdb"));
                Zharp.Build.AppSymbols.Module module = Zharp.Build.AppSymbols.Read(Path.Combine(copy, "NoPdbSample.dll"))!;
                Assert.Equal(typeof(Cart).Module.ModuleVersionId.ToString("D"), module.Mvid);
                Assert.Equal("NoPdbSample.pdb", module.PdbName);
                Assert.NotNull(module.Pdb);
                Assert.Equal("BSJB", Encoding.ASCII.GetString(module.Pdb!, 0, 4));
            }
            finally
            {
                Directory.Delete(copy, true);
            }
        }

        [Fact]
        public void Without_its_pdb_an_assembly_has_an_mvid_and_no_symbols()
        {
            Zharp.Build.AppSymbols.Module module = Zharp.Build.AppSymbols.Read(typeof(Cart).Assembly.Location)!;
            Assert.Equal(typeof(Cart).Module.ModuleVersionId.ToString("D"), module.Mvid);
            Assert.Null(module.Pdb);
        }
    }

    public class SessionTests
    {
        [Fact]
        public void A_new_session_after_30_minutes_in_the_background()
        {
            DateTime now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
            var session = new SessionManager(null, () => now);
            string first = session.Current();
            session.OnBackground();
            now = now.AddMinutes(29);
            session.OnForeground();
            Assert.Equal(first, session.Current());
            session.OnBackground();
            now = now.AddMinutes(31);
            session.OnForeground();
            Assert.NotEqual(first, session.Current());
        }

        [Fact]
        public void An_app_started_again_soon_keeps_its_session_and_later_gets_a_new_one()
        {
            string path = Path.Combine(Path.GetTempPath(), "zharp-session-" + Guid.NewGuid().ToString("N"));
            try
            {
                DateTime now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
                string first = new SessionManager(path, () => now).Current();
                now = now.AddMinutes(10);
                Assert.Equal(first, new SessionManager(path, () => now).Current());
                now = now.AddMinutes(45);
                Assert.NotEqual(first, new SessionManager(path, () => now).Current());
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Rotate_starts_a_new_session()
        {
            var session = new SessionManager(null);
            string first = session.Current();
            session.Rotate();
            Assert.NotEqual(first, session.Current());
            Assert.Matches("^[0-9a-f]{32}$", session.Current());
        }
    }

    public class OutboxTests
    {
        static string NewDir() => Path.Combine(Path.GetTempPath(), "zharp-outbox-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Oldest_first_and_capped_by_count_and_size()
        {
            string dir = NewDir();
            try
            {
                var outbox = new Outbox(dir, maxFiles: 3, maxBytes: 1000);
                for (int i = 0; i < 5; i++) outbox.Save(i % 2 == 0 ? "logs" : "traces", new byte[] { (byte)i });
                List<string> pending = outbox.Pending();
                Assert.Equal(3, pending.Count);
                Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(pending[0]));
                Assert.Equal("logs", Outbox.SignalOf(pending[0]));
                outbox.Save("logs", new byte[1000]);
                Assert.Single(outbox.Pending());
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Old_batches_are_dropped()
        {
            string dir = NewDir();
            try
            {
                DateTime now = DateTime.UtcNow;
                var outbox = new Outbox(dir, clock: () => now);
                outbox.Save("traces", new byte[] { 1 });
                now = now.AddDays(4);
                Assert.Empty(outbox.Pending());
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        sealed class Network : HttpMessageHandler
        {
            public bool Online;
            public HttpStatusCode Status = HttpStatusCode.OK;
            public readonly List<(string Path, byte[] Body, string? Auth)> Sent = new List<(string, byte[], string?)>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (!Online) throw new HttpRequestException("offline");
                byte[] body = await request.Content!.ReadAsByteArrayAsync();
                lock (Sent) Sent.Add((request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
                return new HttpResponseMessage(Status);
            }
        }

        static HttpClient Client(OutboxHandler handler) => new HttpClient(handler, disposeHandler: false);

        static HttpRequestMessage Batch(string signal, byte value) =>
            new HttpRequestMessage(HttpMethod.Post, "https://api.zharp.io/api/v1/otlp/v1/" + signal) { Content = new ByteArrayContent(new[] { value }) };

        [Fact]
        public async Task Offline_batches_wait_on_disk_and_go_out_once_sending_works()
        {
            string dir = NewDir();
            try
            {
                var outbox = new Outbox(dir);
                var network = new Network();
                var log = new List<string>();
                var handler = new OutboxHandler(outbox, () => "https://api.zharp.io/api/v1/otlp", () => "zpk_test", m => { lock (log) log.Add(m); }, network);
                using HttpClient client = Client(handler);

                HttpResponseMessage offline = await client.SendAsync(Batch("logs", 1));
                Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
                Assert.Single(outbox.Pending());

                network.Status = HttpStatusCode.ServiceUnavailable;
                network.Online = true;
                await client.SendAsync(Batch("traces", 2));
                Assert.Equal(2, outbox.Pending().Count);

                network.Status = HttpStatusCode.OK;
                await client.SendAsync(Batch("traces", 3));
                Assert.True(await handler.Drain(CancellationToken.None), string.Join("; ", log));
                Assert.Empty(outbox.Pending());
                string[] order = network.Sent.Select(s => s.Path + ":" + s.Body[0]).ToArray();
                Assert.Contains("/api/v1/otlp/v1/logs:1", order);
                Assert.Contains("/api/v1/otlp/v1/traces:2", order);
                Assert.True(Array.IndexOf(order, "/api/v1/otlp/v1/logs:1") < Array.LastIndexOf(order, "/api/v1/otlp/v1/traces:2"), "oldest first (the first traces:2 was refused with a 503)");
                // What the outbox sends carries the key (logs:1 only ever went out from it).
                Assert.All(network.Sent.Where(s => s.Body[0] == 1), s => Assert.Equal("Bearer zpk_test", s.Auth));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task While_crashing_a_batch_is_on_disk_before_it_is_sent()
        {
            string dir = NewDir();
            try
            {
                var outbox = new Outbox(dir);
                int onDiskWhileSending = -1;
                var network = new Callback(() => onDiskWhileSending = outbox.Pending().Count);
                var handler = new OutboxHandler(outbox, () => "https://x/otlp", () => "k", _ => { }, network) { PersistFirst = true };
                using HttpClient client = Client(handler);
                await client.SendAsync(Batch("logs", 7));
                Assert.Equal(1, onDiskWhileSending);
                Assert.Empty(outbox.Pending());
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task A_refused_batch_is_not_kept()
        {
            string dir = NewDir();
            try
            {
                var outbox = new Outbox(dir);
                var network = new Network { Online = true, Status = HttpStatusCode.Unauthorized };
                var handler = new OutboxHandler(outbox, () => "https://x/otlp", () => "k", _ => { }, network);
                using HttpClient client = Client(handler);
                HttpResponseMessage response = await client.SendAsync(Batch("logs", 1));
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Empty(outbox.Pending());
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

#if NET
        [Fact]
        public void The_synchronous_send_works_on_handlers_that_only_send_asynchronously()
        {
            string dir = NewDir();
            try
            {
                var network = new Network { Online = true };
                var handler = new OutboxHandler(new Outbox(dir), () => "https://x/otlp", () => "k", _ => { }, network);
                using HttpClient client = Client(handler);
                using HttpResponseMessage response = client.Send(Batch("traces", 4));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Single(network.Sent);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
#endif

        sealed class Callback : HttpMessageHandler
        {
            readonly Action onSend;
            public Callback(Action onSend) { this.onSend = onSend; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                onSend();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
