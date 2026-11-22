using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Zharp.Internal
{
    /// <summary>A failed query: where it ran and what the database said.</summary>
    internal sealed class DbFailure
    {
        public string? System;
        public string? Statement;
        public string? Database;
        public string? Code;
        public Exception? Error;
        public Activity? Span;
        /// <summary>Reported (or merged into a report) already.</summary>
        public bool Done;

        public IEnumerable<KeyValuePair<string, object?>> Attributes()
        {
            if (System != null) yield return new KeyValuePair<string, object?>("db.system", System);
            if (Statement != null) yield return new KeyValuePair<string, object?>("db.statement", Statement.Length > 4000 ? Statement.Substring(0, 4000) : Statement);
            if (Database != null) yield return new KeyValuePair<string, object?>("db.name", Database);
            if (Code != null) yield return new KeyValuePair<string, object?>("db.error.code", Code);
        }

        /// <summary>The type the failure is grouped under: named from the code, as in Node.</summary>
        public string TypeName() => DbFailures.ErrorType(System, Code, Error != null ? Stacks.TypeName(Error) : null);
    }

    /// <summary>
    /// Failed queries, seen from the drivers' own spans (Npgsql, MySqlConnector) and OpenTelemetry's
    /// SqlClient instrumentation. A driver's exception is matched to the query that threw it when
    /// it is thrown (a first-chance exception while the query's span is current), so wherever the
    /// app later catches or rethrows it — wrapped by EF Core or not — the report says which
    /// statement failed and why.
    /// </summary>
    internal static class DbFailures
    {
        static readonly ConditionalWeakTable<Exception, DbFailure> ByException = new ConditionalWeakTable<Exception, DbFailure>();
        static readonly ConditionalWeakTable<Activity, DbFailure> BySpan = new ConditionalWeakTable<Activity, DbFailure>();
        static int hooked;

        public static void Hook()
        {
            if (System.Threading.Interlocked.Exchange(ref hooked, 1) == 1) return;
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => OnThrown(e.Exception);
        }

        const string EfCommandError = "Microsoft.EntityFrameworkCore.Database.Command.CommandError";

        /// <summary>
        /// EF Core's own report of a failed command, for any provider: the statement and the
        /// exception together. It covers what a driver's span can't — SQL Server's errors that
        /// arrive while EF reads a batch's results, after SqlClient's span ended, and SQLite.
        /// </summary>
        public static void HookEfCore(ZharpCore core)
        {
            DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
            {
                if (listener.Name != "Microsoft.EntityFrameworkCore") return;
                listener.Subscribe(new Observer<KeyValuePair<string, object?>>(e => OnEfEvent(core, e)), (name, _, _) => name == EfCommandError);
            }));
        }

        static void OnEfEvent(ZharpCore core, KeyValuePair<string, object?> e)
        {
            if (e.Key != EfCommandError || e.Value == null || !core.Settings.CaptureQueryErrors) return;
            try
            {
                Type type = e.Value.GetType();
                var command = type.GetProperty("Command")?.GetValue(e.Value) as System.Data.Common.DbCommand;
                var error = type.GetProperty("Exception")?.GetValue(e.Value) as Exception;
                if (error == null || OfException(error) != null) return;
                var failure = new DbFailure
                {
                    System = SystemOfCommand(command),
                    Statement = command?.CommandText,
                    Database = SafeDatabase(command),
                    Code = CodeOf(error),
                    Error = error,
                    Span = Activity.Current,
                };
                ByException.Add(error, failure);
                core.Errors.AddQuery(failure, Activity.Current);
            }
            catch (Exception ex)
            {
                core.DebugLog("ef event: " + ex.Message);
            }
        }

        static string? SafeDatabase(System.Data.Common.DbCommand? command)
        {
            try { return command?.Connection?.Database; }
            catch { return null; }
        }

        static string? SystemOfCommand(System.Data.Common.DbCommand? command)
        {
            string name = command?.GetType().Name ?? "";
            if (name.StartsWith("Npgsql", StringComparison.Ordinal)) return "postgresql";
            if (name == "SqlCommand") return "mssql";
            if (name.StartsWith("Sqlite", StringComparison.Ordinal)) return "sqlite";
            if (name.StartsWith("MySql", StringComparison.Ordinal)) return "mysql";
            if (name.StartsWith("Oracle", StringComparison.Ordinal)) return "oracle";
            return null;
        }

        static void OnThrown(Exception error)
        {
            try
            {
                Activity? span = Activity.Current;
                if (span == null || !IsDbSpan(span)) return;
                if (ByException.TryGetValue(error, out _)) return;
                DbFailure failure = Describe(span, error);
                ByException.Add(error, failure);
                BySpan.Remove(span);
                BySpan.Add(span, failure);
            }
            catch
            {
                // Never in the app's way.
            }
        }

        public static bool IsDbSpan(Activity span) => SystemOf(span) != null;

        static string? Tag(Activity span, string name) => span.GetTagItem(name) as string;

        static string? SystemOf(Activity span)
        {
            string? system = Tag(span, "db.system") ?? Tag(span, "db.system.name");
            if (system == null) return null;
            switch (system)
            {
                case "microsoft.sql_server": return "mssql";
                default: return system;
            }
        }

        public static void Remember(Exception error, DbFailure failure)
        {
            try
            {
                ByException.Remove(error);
                ByException.Add(error, failure);
            }
            catch
            {
                // Seen twice at once: either will do.
            }
        }

        /// <summary>A failure known only from the driver's exception.</summary>
        public static DbFailure FromException(Exception error)
        {
            string name = error.GetType().Name;
            string? system = name == "PostgresException" || name == "NpgsqlException" ? "postgresql"
                : name == "SqlException" ? "mssql"
                : name == "MySqlException" ? "mysql"
                : name == "SqliteException" ? "sqlite"
                : name.StartsWith("Oracle", StringComparison.Ordinal) ? "oracle" : null;
            return new DbFailure { System = system, Code = CodeOf(error), Error = error };
        }

        public static DbFailure Describe(Activity span, Exception? error) => new DbFailure
        {
            System = SystemOf(span),
            Statement = Tag(span, "db.statement") ?? Tag(span, "db.query.text"),
            Database = Tag(span, "db.name") ?? Tag(span, "db.namespace"),
            Code = error != null ? CodeOf(error) : Tag(span, "db.response.status_code"),
            Error = error,
            Span = span,
        };

        /// <summary>The failure of a query span that ended in error (null when it didn't).</summary>
        public static DbFailure? OfSpan(Activity span)
        {
            if (!IsDbSpan(span)) return null;
            bool failed = span.Status == ActivityStatusCode.Error || "ERROR".Equals(Tag(span, "otel.status_code"), StringComparison.OrdinalIgnoreCase)
                || Tag(span, "error.type") != null;
            if (!failed) return null;
            if (BySpan.TryGetValue(span, out DbFailure? known)) return known;
            DbFailure failure = Describe(span, null);
            BySpan.Add(span, failure);
            return failure;
        }

        /// <summary>The failed query an exception (or one it wraps) came from.</summary>
        public static DbFailure? OfException(Exception error)
        {
            int depth = 0;
            for (Exception? e = error; e != null && depth < 10; e = e.InnerException, depth++)
            {
                if (ByException.TryGetValue(e, out DbFailure? failure)) return failure;
            }
            return null;
        }

        /// <summary>The database's own error code: SQLSTATE, SQL Server's number, MySQL's or SQLite's code.</summary>
        public static string? CodeOf(Exception error)
        {
            Type type = error.GetType();
            string name = type.Name;
            object? Get(string property)
            {
                try { return type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(error); }
                catch { return null; }
            }
            if (name == "SqlException") return Get("Number")?.ToString();
            if (name == "MySqlException") return (Get("Number") ?? Get("ErrorCode"))?.ToString();
            if (name == "SqliteException") return (Get("SqliteExtendedErrorCode") ?? Get("SqliteErrorCode"))?.ToString();
            string? state = Get("SqlState") as string;
            return string.IsNullOrEmpty(state) ? null : state;
        }

        /// <summary>PostgreSQL's SQLSTATE codes people meet, by the names Postgres gives them.</summary>
        static readonly Dictionary<string, string> SqlState = new Dictionary<string, string>
        {
            ["23505"] = "UniqueViolation", ["23503"] = "ForeignKeyViolation", ["23502"] = "NotNullViolation", ["23514"] = "CheckViolation",
            ["23P01"] = "ExclusionViolation", ["22001"] = "StringDataRightTruncation", ["22003"] = "NumericValueOutOfRange",
            ["22P02"] = "InvalidTextRepresentation", ["22007"] = "InvalidDatetimeFormat", ["22008"] = "DatetimeFieldOverflow", ["22012"] = "DivisionByZero",
            ["42P01"] = "UndefinedTable", ["42703"] = "UndefinedColumn", ["42883"] = "UndefinedFunction", ["42601"] = "SyntaxError", ["42501"] = "InsufficientPrivilege",
            ["40001"] = "SerializationFailure", ["40P01"] = "DeadlockDetected", ["55P03"] = "LockNotAvailable", ["57014"] = "QueryCanceled",
            ["53300"] = "TooManyConnections", ["08006"] = "ConnectionFailure", ["08001"] = "ConnectionRefused", ["28P01"] = "InvalidPassword", ["3D000"] = "InvalidCatalogName",
        };

        /// <summary>SQL Server's error numbers people meet.</summary>
        static readonly Dictionary<string, string> SqlServer = new Dictionary<string, string>
        {
            ["2627"] = "UniqueKeyViolation", ["2601"] = "DuplicateKeyViolation", ["547"] = "ConstraintViolation", ["515"] = "NotNullViolation",
            ["208"] = "InvalidObjectName", ["207"] = "InvalidColumnName", ["102"] = "SyntaxError", ["156"] = "SyntaxError", ["1205"] = "DeadlockVictim",
            ["8152"] = "StringDataRightTruncation", ["2628"] = "StringDataRightTruncation", ["245"] = "ConversionFailed", ["8134"] = "DivideByZero",
            ["-2"] = "Timeout", ["18456"] = "LoginFailed", ["4060"] = "InvalidDatabase", ["229"] = "PermissionDenied",
        };

        /// <summary>MySQL's error numbers, by their ER_ names.</summary>
        static readonly Dictionary<string, string> MySql = new Dictionary<string, string>
        {
            ["1062"] = "ER_DUP_ENTRY", ["1452"] = "ER_NO_REFERENCED_ROW_2", ["1451"] = "ER_ROW_IS_REFERENCED_2", ["1048"] = "ER_BAD_NULL_ERROR",
            ["1146"] = "ER_NO_SUCH_TABLE", ["1054"] = "ER_BAD_FIELD_ERROR", ["1064"] = "ER_PARSE_ERROR", ["1213"] = "ER_LOCK_DEADLOCK",
            ["1205"] = "ER_LOCK_WAIT_TIMEOUT", ["1406"] = "ER_DATA_TOO_LONG", ["1045"] = "ER_ACCESS_DENIED_ERROR",
        };

        static readonly Dictionary<string, string> Systems = new Dictionary<string, string>
        {
            ["postgresql"] = "Postgres", ["mysql"] = "MySQL", ["mariadb"] = "MariaDB", ["mssql"] = "SqlServer", ["oracle"] = "Oracle", ["sqlite"] = "Sqlite",
        };

        public static string ErrorType(string? system, string? code, string? exceptionType)
        {
            if (code != null)
            {
                if ((system == "postgresql" || system == null) && SqlState.TryGetValue(code, out string? pg)) return pg;
                if (system == "mssql" && SqlServer.TryGetValue(code, out string? ms)) return ms;
                if ((system == "mysql" || system == "mariadb") && MySql.TryGetValue(code, out string? my)) return my;
                if (system == "sqlite" && (code == "2067" || code == "1555")) return "UniqueViolation";
                if (system == "sqlite" && code == "787") return "ForeignKeyViolation";
                if (system == "sqlite" && code == "1299") return "NotNullViolation";
            }
            if (!string.IsNullOrEmpty(exceptionType)) return exceptionType!;
            return (system != null && Systems.TryGetValue(system, out string? s) ? s : "Database") + "Error";
        }
    }

    /// <summary>An IObserver from a callback.</summary>
    internal sealed class Observer<T> : IObserver<T>
    {
        readonly Action<T> next;
        public Observer(Action<T> next) { this.next = next; }
        public void OnNext(T value)
        {
            try { next(value); } catch { /* never into the app */ }
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
