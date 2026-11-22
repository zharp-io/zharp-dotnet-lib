using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Zharp.Internal
{
    /// <summary>
    /// Values that never leave the process, with the same rules as the Node and browser SDKs
    /// (@zharp/internal bodies.ts and headers.ts): a key is secret when, compared without case,
    /// <c>-</c> or <c>_</c>, it contains one of <see cref="Contains"/> or is one of
    /// <see cref="Exact"/>; its value is sent as <c>[Filtered]</c>.
    /// </summary>
    internal static class Secrets
    {
        public const string Filtered = "[Filtered]";
        /// <summary>Per side, so a request stuffed with headers can't fill the span.</summary>
        public const int MaxHeaders = 40;
        public const int MaxHeaderValue = 512;

        static readonly string[] Contains =
        {
            "password", "passwd", "passphrase", "secret", "token", "apikey", "accesskey", "privatekey",
            "authorization", "cookie", "creditcard", "cardnumber", "cvv", "cvc", "ssn", "iban", "sessionid",
            "signature", "credential",
        };
        static readonly HashSet<string> Exact = new HashSet<string> { "auth", "pin", "pwd", "otp", "card", "pan", "jwt", "sid", "codeverifier" };
        /// <summary>Also secret in a query string, where they are API keys and signed-URL signatures.</summary>
        static readonly HashSet<string> UrlExact = new HashSet<string> { "key", "sig", "code", "state", "session" };
        /// <summary>Headers that are secret whatever the key rules say.</summary>
        static readonly HashSet<string> SecretHeaders = new HashSet<string>
        {
            "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "x-auth-token", "x-csrf-token", "x-xsrf-token",
        };

        public static string Normalize(string key)
        {
            var sb = new StringBuilder(key.Length);
            foreach (char c in key.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.ToString();
        }

        public static bool IsSecretKey(string key, IEnumerable<string>? extra = null)
        {
            string k = Normalize(key);
            if (k.Length == 0) return false;
            if (Exact.Contains(k)) return true;
            foreach (string c in Contains)
            {
                if (k.Contains(c)) return true;
            }
            return extra != null && extra.Any(e => Normalize(e) == k);
        }

        public static bool IsSecretHeader(string name, IEnumerable<string>? extra = null)
        {
            string lower = name.ToLowerInvariant();
            return SecretHeaders.Contains(lower) || IsSecretKey(lower, extra);
        }

        /// <summary>A header's value as recorded: secrets filtered, long values cut, repeats joined.</summary>
        public static string HeaderValue(string name, IEnumerable<string> values, IEnumerable<string>? extra)
        {
            if (IsSecretHeader(name, extra)) return Filtered;
            string text = string.Join(", ", values);
            return text.Length > MaxHeaderValue ? text.Substring(0, MaxHeaderValue) + "…" : text;
        }

        /// <summary>A query string (without <c>?</c>) with secret-looking values filtered.</summary>
        public static string RedactQuery(string query)
        {
            if (string.IsNullOrEmpty(query)) return query;
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                int eq = pair.IndexOf('=');
                if (eq <= 0 || eq == pair.Length - 1) continue;
                string key = pair.Substring(0, eq);
                try { key = Uri.UnescapeDataString(key); } catch { /* as written */ }
                if (IsSecretKey(key) || UrlExact.Contains(Normalize(key))) pairs[i] = pair.Substring(0, eq) + "=" + Filtered;
            }
            return string.Join("&", pairs);
        }

        static readonly Regex UserInfo = new Regex("^([a-zA-Z][a-zA-Z0-9+.-]*://)[^/?#@]*@", RegexOptions.Compiled);

        /// <summary>A URL with secret query values filtered and <c>user:password@</c> dropped.</summary>
        public static string RedactUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            string output = UserInfo.Replace(url, "$1");
            int q = output.IndexOf('?');
            if (q < 0) return output;
            int hash = output.IndexOf('#', q);
            string query = hash < 0 ? output.Substring(q + 1) : output.Substring(q + 1, hash - q - 1);
            string redacted = RedactQuery(query);
            if (redacted == query) return output;
            return output.Substring(0, q + 1) + redacted + (hash < 0 ? "" : output.Substring(hash));
        }
    }
}
