using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;

namespace Zharp.Internal
{
    /// <summary>
    /// The last command each SQL Server connection ran, from SqlClient's own diagnostics
    /// (<c>WriteCommandAfter</c>, with the connection's ID). A SqlException carries the same ID
    /// (<c>ClientConnectionId</c>), so an error raised while a batch's results are read — after the
    /// command's span ended — is tied to its statement by connection, never by guesswork.
    /// </summary>
    internal static class SqlCommands
    {
        internal sealed class Command
        {
            public string? Text;
            public string? Database;
        }

        const int Max = 2000;
        static readonly ConcurrentDictionary<Guid, Command> Last = new ConcurrentDictionary<Guid, Command>();
        static int hooked;

        public static void Hook()
        {
            if (System.Threading.Interlocked.Exchange(ref hooked, 1) == 1) return;
            DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name != "SqlClientDiagnosticListener") return;
                listener.Subscribe(new Observer<KeyValuePair<string, object?>>(OnEvent), (name, _, _) => name.EndsWith("WriteCommandAfter", StringComparison.Ordinal));
            }));
        }

        static void OnEvent(KeyValuePair<string, object?> e)
        {
            if (e.Value == null || !e.Key.EndsWith("WriteCommandAfter", StringComparison.Ordinal)) return;
            Type type = e.Value.GetType();
            if (!(type.GetProperty("ConnectionId")?.GetValue(e.Value) is Guid connection) || connection == Guid.Empty) return;
            var command = type.GetProperty("Command")?.GetValue(e.Value) as DbCommand;
            if (command == null) return;
            if (Last.Count > Max) Last.Clear();
            string? database = null;
            try { database = command.Connection?.Database; } catch { /* closed */ }
            Last[connection] = new Command { Text = command.CommandText, Database = database };
        }

        /// <summary>The last command on the connection a SqlException came from, or null.</summary>
        public static Command? LastOn(Exception error)
        {
            if (error.GetType().Name != "SqlException") return null;
            try
            {
                if (error.GetType().GetProperty("ClientConnectionId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(error) is Guid id
                    && Last.TryGetValue(id, out Command? command)) return command;
            }
            catch
            {
                // No ID: no statement.
            }
            return null;
        }

        /// <summary>For tests.</summary>
        internal static void Record(Guid connection, string text, string? database) => Last[connection] = new Command { Text = text, Database = database };
    }
}
