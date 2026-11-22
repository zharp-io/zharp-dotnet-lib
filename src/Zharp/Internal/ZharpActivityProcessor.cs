using System;
using System.Collections.Generic;
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
        /// An event's tags are the collection it was created with, so they can be added to in place
        /// (an Activity's events can't be replaced).
        /// </summary>
        static void MarkExceptionEvents(Activity activity)
        {
            foreach (ActivityEvent e in activity.Events)
            {
                if (e.Name != "exception") continue;
                if (e.Tags is ActivityTagsCollection tags)
                {
                    tags["zharp.deferred"] = "true";
                }
            }
        }

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
