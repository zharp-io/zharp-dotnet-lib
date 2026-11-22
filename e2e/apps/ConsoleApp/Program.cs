// A plain console app (the ".NET" tile without a host): ZharpSdk.Init(), a logger factory with
// AddZharp(), a job traced as one span, a failed query the job survives, then a crash.
using Microsoft.Extensions.Logging;
using Npgsql;
using Zharp;

using var zharp = ZharpSdk.Init();
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).AddZharp());
var log = loggerFactory.CreateLogger("Import");

using (var job = ZharpSdk.StartSpan("import-orders"))
{
    log.LogInformation("Starting import");
    await using var db = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("DB_URL")!);
    try
    {
        await using var cmd = db.CreateCommand("SELECT id FROM orders_archive WHERE id = 1");
        await cmd.ExecuteScalarAsync();
    }
    catch (PostgresException e)
    {
        log.LogWarning("No archive table ({Code}), skipping", e.SqlState);
    }
    log.LogInformation("Imported {Count} orders", 3);
}

if (args.Contains("--crash"))
{
    Importer.Finish(0);
}

static class Importer
{
    public static void Finish(int count)
    {
        if (count == 0) throw new InvalidOperationException("Nothing was imported");
    }
}
