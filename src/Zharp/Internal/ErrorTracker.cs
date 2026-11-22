using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Zharp.Internal
{
    /// <summary>
    /// One report per failure.
    ///
    /// <list type="bullet">
    /// <item>An exception is known by identity, its inner exceptions included: a wrapper and what it
    /// wraps are one failure, reported once whoever sees it first (ASP.NET Core's diagnostics, a
    /// logger, the AppDomain).</item>
    /// <item>An exception logged during a request is held until the request ends: if the request
    /// failed with it, the request's report covers it; otherwise it was caught, and is reported as
    /// handled.</item>
    /// <item>A failed query is merged into the report of the exception it caused. A query that
    /// failed without one (the app caught it) is reported as handled when the request ends — or as
    /// the request's failure, when the request answered 5xx and nothing else was reported.</item>
    /// </list>
    /// </summary>
    internal sealed class ErrorTracker
    {
        /// <summary>How long a failure outside a request waits for an unhandled-exception handler to claim it.</summary>
        const int LoneWaitMs = 1500;
        const int MaxTraces = 2000;

        sealed class Reported { }
        static readonly Reported Mark = new Reported();
        readonly ConditionalWeakTable<Exception, Reported> reported = new ConditionalWeakTable<Exception, Reported>();

        sealed class Held
        {
            public Exception Error = null!;
            public string Mechanism = "logger";
            /// <summary>The host saw it escape: unhandled, unless the response says it was dealt with (below 500).</summary>
            public bool Uncaught;
            public Activity? Span;
        }

        sealed class TraceState
        {
            public readonly List<DbFailure> Queries = new List<DbFailure>();
            public readonly List<Held> Held = new List<Held>();
            public bool ReportedAny;
        }

        readonly ZharpCore core;
        readonly Dictionary<string, TraceState> traces = new Dictionary<string, TraceState>();
        readonly Queue<string> order = new Queue<string>();

        public ErrorTracker(ZharpCore core)
        {
            this.core = core;
        }

        // ── identity ────────────────────────────────────────────────────

        static IEnumerable<Exception> Chain(Exception error)
        {
            var seen = new HashSet<Exception>();
            var stack = new Stack<Exception>();
            stack.Push(error);
            while (stack.Count > 0 && seen.Count < 30)
            {
                Exception e = stack.Pop();
                if (!seen.Add(e)) continue;
                yield return e;
                if (e is AggregateException agg)
                {
                    foreach (Exception inner in agg.InnerExceptions) stack.Push(inner);
                }
                else if (e.InnerException != null)
                {
                    stack.Push(e.InnerException);
                }
            }
        }

        public bool IsReported(Exception error)
        {
            foreach (Exception e in Chain(error))
            {
                if (reported.TryGetValue(e, out _)) return true;
            }
            return false;
        }

        /// <summary>Marks the exception and everything it wraps. False when it was reported already.</summary>
        public bool TryMark(Exception error)
        {
            lock (reported)
            {
                if (IsReported(error)) return false;
                foreach (Exception e in Chain(error)) reported.Add(e, Mark);
                return true;
            }
        }

        // ── traces ──────────────────────────────────────────────────────

        public static Activity LocalRoot(Activity span)
        {
            Activity a = span;
            while (a.Parent != null) a = a.Parent;
            return a;
        }

        static bool IsStopped(Activity a) => a.Duration != TimeSpan.Zero;

        TraceState State(string traceId)
        {
            lock (traces)
            {
                if (!traces.TryGetValue(traceId, out TraceState? st))
                {
                    st = new TraceState();
                    traces[traceId] = st;
                    order.Enqueue(traceId);
                    while (order.Count > MaxTraces) traces.Remove(order.Dequeue());
                }
                return st;
            }
        }

        TraceState? Take(string traceId)
        {
            lock (traces)
            {
                if (traces.TryGetValue(traceId, out TraceState? st)) traces.Remove(traceId);
                return st;
            }
        }

        /// <summary>A report was made in this trace (so a 5xx answer has its explanation).</summary>
        public void NoteReported(Activity? span)
        {
            if (span == null) return;
            TraceState st = State(span.TraceId.ToHexString());
            lock (st) st.ReportedAny = true;
        }

        /// <summary>
        /// An exception seen during a request — written by an app logger at Error, or by the host
        /// as what escaped the request (<paramref name="uncaught"/>) — held until the request ends;
        /// outside a request, reported now.
        /// </summary>
        public void Hold(Exception error, string mechanism, bool uncaught, Activity? span)
        {
            if (IsReported(error)) return;
            Activity? root = span != null ? LocalRoot(span) : null;
            if (span != null && root != null && !IsStopped(root))
            {
                TraceState st = State(span.TraceId.ToHexString());
                lock (st) st.Held.Add(new Held { Error = error, Mechanism = mechanism, Uncaught = uncaught, Span = span });
                return;
            }
            core.Report(error, handled: !uncaught, mechanism: mechanism, span: span);
        }

        /// <summary>A span ended: collect a failed query; at the end of a root, report what's left.</summary>
        public void OnSpanEnd(Activity span)
        {
            try
            {
                DbFailure? query = core.Settings.CaptureQueryErrors ? DbFailures.OfSpan(span) : null;
                bool isRoot = span.Parent == null;
                if (!isRoot && DbFailures.IsDbSpan(span))
                {
                    lock (lastDb) lastDb[span.TraceId.ToHexString()] = span;
                }
                if (query != null && !query.Done)
                {
                    if (isRoot)
                    {
                        // Outside any request: an unhandled-exception handler may still claim it.
                        Lone(query);
                    }
                    else
                    {
                        TraceState st = State(span.TraceId.ToHexString());
                        lock (st) st.Queries.Add(query);
                    }
                }
                if (isRoot) Finish(span);
            }
            catch (Exception e)
            {
                core.DebugLog("span end: " + e.Message);
            }
        }

        /// <summary>A failed query seen outside a span's end (EF Core's report): pending like the others.</summary>
        public void AddQuery(DbFailure query, Activity? span)
        {
            Activity? root = span != null ? LocalRoot(span) : null;
            if (span != null && root != null && !IsStopped(root))
            {
                TraceState st = State(span.TraceId.ToHexString());
                lock (st)
                {
                    // The driver's span already failed with this statement, without its exception.
                    DbFailure? same = st.Queries.FirstOrDefault(q => q.Error == null && !q.Done && q.Statement == query.Statement);
                    if (same != null && query.Error != null)
                    {
                        same.Error = query.Error;
                        same.Code ??= query.Code;
                        DbFailures.Remember(query.Error, same);
                        return;
                    }
                    st.Queries.Add(query);
                }
                return;
            }
            Lone(query);
        }

        /// <summary>The last query span of each running trace, for failures no span saw.</summary>
        readonly Dictionary<string, Activity> lastDb = new Dictionary<string, Activity>();

        /// <summary>
        /// The query a database exception most likely came from, when no span or EF Core event
        /// tied them: SQL Server reports a failed batch while its results are read, after
        /// SqlClient's span ended without error. Its statement is the trace's last query.
        /// </summary>
        public DbFailure? Guess(Exception error, Activity? span)
        {
            System.Data.Common.DbException? db = null;
            for (Exception? e = error; e != null && db == null; e = e.InnerException) db = e as System.Data.Common.DbException;
            if (db == null) return null;
            var failure = DbFailures.FromException(db);
            if (span != null)
            {
                Activity? last;
                lock (lastDb) lastDb.TryGetValue(span.TraceId.ToHexString(), out last);
                DbFailure? fromSpan = last != null ? DbFailures.Describe(last, db) : null;
                if (fromSpan != null && (failure.System == null || fromSpan.System == failure.System))
                {
                    failure.Statement = fromSpan.Statement;
                    failure.Database = fromSpan.Database;
                    failure.System = fromSpan.System;
                }
            }
            DbFailures.Remember(db, failure);
            return failure;
        }

        void Lone(DbFailure query)
        {
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                try
                {
                    if (query.Done || (query.Error != null && IsReported(query.Error))) return;
                    core.ReportQuery(query, handled: true, mechanism: "db");
                }
                finally
                {
                    lock (Pending) Pending.Remove(timer!);
                    timer!.Dispose();
                }
            }, null, Timeout.Infinite, Timeout.Infinite);
            lock (Pending) Pending.Add(timer);
            timer.Change(LoneWaitMs, Timeout.Infinite);
        }

        /// <summary>Keeps lone-query timers alive until they fire.</summary>
        static readonly List<Timer> Pending = new List<Timer>();

        static int? Status(Activity span)
        {
            object? status = span.GetTagItem("http.response.status_code") ?? span.GetTagItem("http.status_code");
            if (status == null) return null;
            return int.TryParse(Convert.ToString(status, System.Globalization.CultureInfo.InvariantCulture), out int s) ? s : (int?)null;
        }

        void Finish(Activity root)
        {
            try
            {
                FinishTrace(root);
            }
            finally
            {
                lock (lastDb) lastDb.Remove(root.TraceId.ToHexString());
            }
        }

        void FinishTrace(Activity root)
        {
            TraceState? st = Take(root.TraceId.ToHexString());
            if (st == null) return;
            int? status = Status(root);
            bool failed = status.HasValue ? status.Value >= 500 : root.Status == ActivityStatusCode.Error;
            lock (st)
            {
                // What the host saw escape first: a library may have logged the same failure
                // (EF Core logs a failed save at Error) before it reached the host.
                foreach (Held h in st.Held.Where(h => h.Uncaught).Concat(st.Held.Where(h => !h.Uncaught)))
                {
                    if (IsReported(h.Error)) continue;
                    // What escaped into the host's error handling failed the request, unless the
                    // handler turned it into an answer below 500.
                    bool unhandled = h.Uncaught && (failed || !status.HasValue);
                    core.Report(h.Error, handled: !unhandled, mechanism: h.Mechanism, span: h.Span);
                    st.ReportedAny = true;
                }
                if (failed && !st.ReportedAny)
                {
                    // A request answered 5xx with only a failed query in it: the app's own error
                    // handling turned the query's failure into the response.
                    for (int i = st.Queries.Count - 1; i >= 0; i--)
                    {
                        DbFailure q = st.Queries[i];
                        if (q.Done || (q.Error != null && IsReported(q.Error))) continue;
                        core.ReportQuery(q, handled: false, mechanism: MechanismOf(root));
                        break;
                    }
                }
                foreach (DbFailure q in st.Queries)
                {
                    if (q.Done || (q.Error != null && IsReported(q.Error))) continue;
                    core.ReportQuery(q, handled: true, mechanism: "db");
                }
            }
        }

        static string MechanismOf(Activity root)
        {
            string name = root.Source.Name;
            if (name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)) return "aspnetcore";
            if (name.StartsWith("OpenTelemetry.Instrumentation.AspNet", StringComparison.Ordinal)) return "aspnet";
            return name.Length > 0 ? name : "unknown";
        }
    }
}
