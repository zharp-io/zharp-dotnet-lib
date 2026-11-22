using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zharp.Internal;
using Zharp.Logging;
using Xunit;

namespace SdkTests
{
    public class StackTests
    {
        static Exception Thrown(Action action)
        {
            try { action(); } catch (Exception e) { return e; }
            throw new InvalidOperationException("did not throw");
        }

        static void Fail() => throw new InvalidOperationException("Cart is empty");

        [Fact]
        public void The_stack_is_written_as_dotnet_writes_it_with_inner_exceptions()
        {
            Exception error = Thrown(() =>
            {
                try { Fail(); }
                catch (Exception inner) { throw new ApplicationException("Checkout failed", inner); }
            });
            ExceptionInfo info = Stacks.Describe(error);
            Assert.Equal("System.ApplicationException", info.Type);
            Assert.Equal("Checkout failed", info.Message);
            Assert.StartsWith("System.ApplicationException: Checkout failed ---> System.InvalidOperationException: Cart is empty\n", info.Stacktrace);
            Assert.Contains("   --- End of inner exception stack trace ---", info.Stacktrace);
            Assert.Matches(@"   at SdkTests\.StackTests\.Fail\(\) in .*ErrorTests\.cs:line \d+", info.Stacktrace);
        }

        [Fact]
        public void The_stack_is_in_english_whatever_the_ui_culture()
        {
            CultureInfo saved = CultureInfo.CurrentUICulture;
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("de-DE");
            try
            {
                ExceptionInfo info = Stacks.Describe(Thrown(Fail));
                Assert.Contains("   at ", info.Stacktrace);
                Assert.Contains(":line ", info.Stacktrace);
                Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
            }
            finally
            {
                Thread.CurrentThread.CurrentUICulture = saved;
            }
        }

        [Fact]
        public async Task Async_frames_and_the_previous_location_marker()
        {
            Exception error;
            try { await ThrowLaterAsync(); error = new Exception(); }
            catch (Exception e) { error = e; }
            string stack = Stacks.Describe(error).Stacktrace;
            Assert.Contains("ThrowLaterAsync", stack);
        }

        static async Task ThrowLaterAsync()
        {
            await Task.Yield();
            throw new KeyNotFoundException("Order 3 not found");
        }

        [Fact]
        public void The_code_that_ran_is_read_from_the_pdbs_embedded_sources()
        {
            Exception error = Thrown(Fail);
            string? context = SourceContext.Of(Stacks.FramesOf(error));
            Assert.NotNull(context);
            Assert.Contains("\"code\":\"        static void Fail() => throw new InvalidOperationException(\\\"Cart is empty\\\");\"", context);
            Assert.Contains("ErrorTests.cs", context);
            Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-", SourceContext.BuildIdOf(Stacks.FramesOf(error)));
        }
    }

    public class OncePerFailureTests
    {
        [Fact]
        public void A_wrapper_and_what_it_wraps_are_one_failure()
        {
            var tracker = new ErrorTracker(null!);
            var inner = new InvalidOperationException("inner");
            var outer = new Exception("outer", inner);
            Assert.True(tracker.TryMark(inner));
            Assert.True(tracker.IsReported(outer));
            Assert.False(tracker.TryMark(outer));
            var agg = new AggregateException(new Exception("a"), new Exception("b", new ArgumentException("c")));
            Assert.True(tracker.TryMark(agg));
            Assert.True(tracker.IsReported(agg.InnerExceptions[1].InnerException!));
        }
    }

    public class DbErrorTests
    {
        [Theory]
        [InlineData("postgresql", "23505", "PostgresException", "UniqueViolation")]
        [InlineData("postgresql", "42P01", "PostgresException", "UndefinedTable")]
        [InlineData("mssql", "2627", "SqlException", "UniqueKeyViolation")]
        [InlineData("mssql", "208", "SqlException", "InvalidObjectName")]
        [InlineData("mysql", "1062", "MySqlException", "ER_DUP_ENTRY")]
        [InlineData("sqlite", "2067", "SqliteException", "UniqueViolation")]
        [InlineData("postgresql", "99999", "Npgsql.PostgresException", "Npgsql.PostgresException")]
        [InlineData("mssql", null, null, "SqlServerError")]
        public void Failures_are_named_from_the_code(string system, string? code, string? type, string expected) =>
            Assert.Equal(expected, DbFailures.ErrorType(system, code, type));
    }

    public class StampTests
    {
        const string Trace = "0af7651916cd43dd8448eb211c80319c";
        const string Span = "b7ad6b7169203331";

        [Fact]
        public void A_simple_line_ends_with_the_ids_and_keeps_its_line_ending() =>
            Assert.Equal($"info: Shop[0]\n      Saying hello trace_id={Trace} span_id={Span}\n",
                ZharpConsoleFormatter.Stamp("info: Shop[0]\n      Saying hello\n", Trace, Span));

        [Fact]
        public void A_json_line_gets_two_fields() =>
            Assert.Equal($"{{\"Message\":\"hi\",\"trace_id\":\"{Trace}\",\"span_id\":\"{Span}\"}}\n",
                ZharpConsoleFormatter.Stamp("{\"Message\":\"hi\"}\n", Trace, Span));
    }

    public class SourceBundleTests
    {
        [Fact]
        public void The_bundle_is_gzipped_json_of_files_by_path()
        {
            byte[] gz = Zharp.Build.SourceBundles.Bundle(new Dictionary<string, string> { ["Shop/Cart.cs"] = "class Cart {\n}\n" });
            using var reader = new StreamReader(new GZipStream(new MemoryStream(gz), CompressionMode.Decompress), Encoding.UTF8);
            Assert.Equal("{\"files\":{\"Shop/Cart.cs\":\"class Cart {\\n}\\n\"}}", reader.ReadToEnd());
        }

        [Fact]
        public void The_build_id_is_the_pdb_id_the_runtime_reads()
        {
            string assembly = typeof(SourceBundleTests).Assembly.Location;
            Exception error;
            try { throw new InvalidOperationException("x"); } catch (Exception e) { error = e; }
            Assert.Equal(SourceContext.BuildIdOf(Stacks.FramesOf(error)), Zharp.Build.SourceBundles.PdbId(assembly));
        }
    }
}
