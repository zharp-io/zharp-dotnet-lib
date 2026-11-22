using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;

namespace Zharp.Internal
{
    /// <summary>
    /// Request and response headers on HTTP spans as <c>http.request.header.&lt;name&gt;</c> and
    /// <c>http.response.header.&lt;name&gt;</c>, the names the Node SDK uses: a failed call shows
    /// what was sent and what came back. Secret headers are recorded as <c>[Filtered]</c>.
    /// </summary>
    internal static class Headers
    {
        /// <summary>Record headers on a span: at most 40 per side, values capped, secrets filtered.</summary>
        public static void Record(ZharpCore core, Activity activity, string side, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
        {
            if (!core.Settings.CaptureHeaders) return;
            try
            {
                int count = 0;
                foreach (var header in headers)
                {
                    if (count++ >= Secrets.MaxHeaders) break;
                    string name = header.Key.ToLowerInvariant();
                    activity.SetTag("http." + side + ".header." + name, Secrets.HeaderValue(name, header.Value, core.Settings.RedactHeaders));
                }
            }
            catch (Exception e)
            {
                core.DebugLog("headers: " + e.Message);
            }
        }

        public static void OnClientRequest(ZharpCore core, Activity activity, HttpRequestMessage request)
        {
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> all = request.Headers;
            if (request.Content != null) all = all.Concat(request.Content.Headers);
            Record(core, activity, "request", all);
        }

        public static void OnClientResponse(ZharpCore core, Activity activity, HttpResponseMessage response)
        {
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> all = response.Headers;
            if (response.Content != null) all = all.Concat(response.Content.Headers);
            Record(core, activity, "response", all);
        }
    }
}
