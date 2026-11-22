using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;
using OpenTelemetry;

namespace Zharp.Internal
{
    /// <summary>
    /// Every span as it ends, before it is exported:
    /// <list type="bullet">
    /// <item>each <c>exception</c> event stays for the trace view, marked <c>zharp.deferred</c>:
    /// Zharp reports each failure once itself, as an error log, and the server would otherwise make
    /// an error of every event too;</item>
    /// <item>secrets in URLs (<c>?token=…</c>, <c>user:password@</c>) are filtered;</item>
    /// <item>user, session and tags go on the request's root span;</item>
    /// <item>the error tracker hears of failed queries and of requests that ended.</item>
    /// </list>
    /// </summary>
    internal sealed class ZharpActivityProcessor : BaseProcessor<Activity>
    {
        static readonly string[] UrlTags = { "url.full", "http.url", "http.target" };
        readonly ZharpCore core;

        public ZharpActivityProcessor(ZharpCore core)
        {
            this.core = core;
        }

        public override void OnEnd(Activity activity)
        {
            try
            {
                MarkExceptionEvents(activity);
                RedactUrls(activity);
                if (activity.Parent == null)
                {
                    foreach (var kv in core.Users.AttributesFor(activity))
                    {
                        if (activity.GetTagItem(kv.Key) == null) activity.SetTag(kv.Key, kv.Value);
                    }
                }
            }
            catch (Exception e)
            {
                core.DebugLog("span: " + e.Message);
            }
            core.Errors.OnSpanEnd(activity);
        }

        /// <summary>
        /// An Activity's events can't be replaced, but an event's tags are a list it holds by
        /// reference, so they can be added to in place: an <c>ActivityTagsCollection</c> on older
        /// DiagnosticSource versions, an internal linked list (with a public <c>Add</c>) on newer ones.
        /// </summary>
        static void MarkExceptionEvents(Activity activity)
        {
            var deferred = new KeyValuePair<string, object?>("zharp.deferred", "true");
            foreach (ActivityEvent e in activity.Events)
            {
                if (e.Name != "exception") continue;
                IEnumerable<KeyValuePair<string, object?>> tags = e.Tags;
                bool marked = false;
                foreach (var kv in tags)
                {
                    if (kv.Key == "zharp.deferred") { marked = true; break; }
                }
                if (marked) continue;
                if (tags is ActivityTagsCollection collection)
                {
                    collection["zharp.deferred"] = "true";
                    continue;
                }
                MethodInfo? add = AddMethods.GetOrAdd(tags.GetType(), t => t.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(KeyValuePair<string, object?>) }, null));
                add?.Invoke(tags, new object[] { deferred });
            }
        }

        static readonly ConcurrentDictionary<Type, MethodInfo?> AddMethods = new ConcurrentDictionary<Type, MethodInfo?>();

        static void RedactUrls(Activity activity)
        {
            foreach (string name in UrlTags)
            {
                if (activity.GetTagItem(name) is string value)
                {
                    string redacted = Secrets.RedactUrl(value);
                    if (redacted != value) activity.SetTag(name, redacted);
                }
            }
            if (activity.GetTagItem("url.query") is string query)
            {
                string q = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
                string redacted = Secrets.RedactQuery(q);
                if (redacted != q) activity.SetTag("url.query", (q.Length != query.Length ? "?" : "") + redacted);
            }
        }
    }
}
