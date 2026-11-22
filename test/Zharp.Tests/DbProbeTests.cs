using System;
using System.Data.Common;
using Zharp.Internal;
using Xunit;

namespace SdkTests
{
    /// <summary>Named like the provider's: EF Core's existence check catches "no such database".</summary>
    public class FakeDatabaseCreator
    {
        public Exception? Caught;

        public bool Exists()
        {
            try
            {
                Connect();
                return true;
            }
            catch (InvalidOperationException e)
            {
                Caught = e;
                return false;
            }
        }

        static void Connect() => throw new InvalidOperationException("3D000: database \"shop\" does not exist");
    }

    /// <summary>Stands in for Microsoft.Data.SqlClient's: the SDK knows it by name.</summary>
    public sealed class SqlException : DbException
    {
        public SqlException(Guid connection) : base("Violation of UNIQUE KEY constraint") { ClientConnectionId = connection; }
        public Guid ClientConnectionId { get; }
        public int Number => 2627;
    }

    public class DbProbeTests
    {
        [Fact]
        public void A_failure_caught_by_efs_database_existence_check_is_not_reported()
        {
            var creator = new FakeDatabaseCreator();
            Assert.False(creator.Exists());
            Assert.True(DbFailures.IsExistenceProbe(creator.Caught!));
        }

        [Fact]
        public void Other_failed_queries_are_still_reported()
        {
            Exception error;
            try { throw new InvalidOperationException("relation \"no_such_table\" does not exist"); }
            catch (Exception e) { error = e; }
            Assert.False(DbFailures.IsExistenceProbe(error));
        }

        [Fact]
        public void A_sql_server_error_gets_the_statement_of_the_last_command_on_its_own_connection_only()
        {
            var tracker = new ErrorTracker(null!);
            Guid connection = Guid.NewGuid();
            SqlCommands.Record(connection, "INSERT INTO [Users] ([Email]) VALUES (@p0);", "shop");
            SqlCommands.Record(Guid.NewGuid(), "SELECT 1", "other");

            DbFailure? tied = tracker.FromDriverException(new Exception("save failed", new SqlException(connection)));
            Assert.Equal("INSERT INTO [Users] ([Email]) VALUES (@p0);", tied!.Statement);
            Assert.Equal("shop", tied.Database);
            Assert.Equal("UniqueKeyViolation", tied.TypeName());

            DbFailure? unknown = tracker.FromDriverException(new SqlException(Guid.NewGuid()));
            Assert.Null(unknown!.Statement);
            Assert.Equal("mssql", unknown.System);
        }
    }
}
