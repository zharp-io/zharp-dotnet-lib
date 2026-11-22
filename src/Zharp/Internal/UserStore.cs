using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenTelemetry;

namespace Zharp.Internal
{
    /// <summary>
    /// Who the user is and the app's tags. Per request when set during one (on the request's root
    /// span), else for the whole process (a worker, a console app). The browser SDK's
    /// <c>baggage</c> (<c>enduser.id</c>, <c>session.id</c>) is read for requests it sent.
    /// </summary>
    internal sealed class UserStore
    {
        readonly ConditionalWeakTable<Activity, Dictionary<string, string>> perRoot = new ConditionalWeakTable<Activity, Dictionary<string, string>>();
        readonly ConcurrentDictionary<string, string> global = new ConcurrentDictionary<string, string>();
        readonly ConcurrentDictionary<string, string> tags = new ConcurrentDictionary<string, string>();

        public static readonly string[] BaggageKeys = { "enduser.id", "enduser.email", "enduser.name", "session.id" };

        public void SetUser(string? id, string? email, string? name)
        {
            var values = new Dictionary<string, string?> { ["enduser.id"] = id, ["enduser.email"] = email, ["enduser.name"] = name };
            Activity? span = Activity.Current;
            if (span != null)
            {
                Activity root = ErrorTracker.LocalRoot(span);
                Dictionary<string, string> map = perRoot.GetValue(root, _ => new Dictionary<string, string>());
                lock (map)
                {
                    foreach (var kv in values)
                    {
                        if (kv.Value == null) map.Remove(kv.Key);
                        else map[kv.Key] = kv.Value;
                        root.SetTag(kv.Key, kv.Value);
                    }
                }
                return;
            }
            foreach (var kv in values)
            {
                if (kv.Value == null) global.TryRemove(kv.Key, out _);
                else global[kv.Key] = kv.Value;
            }
        }

        public void SetTag(string key, string? value)
        {
            if (value == null) tags.TryRemove(key, out _);
            else tags[key] = value;
        }

        public IEnumerable<KeyValuePair<string, string>> Tags => tags;

        /// <summary>User, session and tags for a report made in <paramref name="span"/>'s request.</summary>
        public IEnumerable<KeyValuePair<string, object?>> AttributesFor(Activity? span)
        {
            var output = new Dictionary<string, string>();
            foreach (var kv in tags) output[kv.Key] = kv.Value;
            foreach (var kv in global) output[kv.Key] = kv.Value;
            if (span != null)
            {
                Activity root = ErrorTracker.LocalRoot(span);
                foreach (string key in BaggageKeys)
                {
                    string? value = root.GetTagItem(key) as string ?? root.GetBaggageItem(key);
                    if (!string.IsNullOrEmpty(value)) output[key] = value!;
                }
                if (perRoot.TryGetValue(root, out Dictionary<string, string>? map))
                {
                    lock (map)
                    {
                        foreach (var kv in map) output[kv.Key] = kv.Value;
                    }
                }
            }
            foreach (string key in BaggageKeys)
            {
                if (output.ContainsKey(key)) continue;
                string? value = Baggage.Current.GetBaggage(key);
                if (!string.IsNullOrEmpty(value)) output[key] = value!;
            }
            foreach (var kv in output) yield return new KeyValuePair<string, object?>(kv.Key, kv.Value);
        }
    }
}
