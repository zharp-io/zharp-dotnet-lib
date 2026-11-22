// The ".NET/HTTP" setup: Zharp with tracing off, for logs and errors only.
using Microsoft.Extensions.Logging;
using Zharp;

using var zharp = ZharpSdk.Init(o => o.Tracing = false);
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).AddZharp());
var log = loggerFactory.CreateLogger("Sync");
using var http = new HttpClient();

log.LogInformation("Sync started");
try
{
    var response = await http.GetAsync(Environment.GetEnvironmentVariable("SYNC_URL"));
    response.EnsureSuccessStatusCode();
}
catch (HttpRequestException e)
{
    log.LogError(e, "Sync failed");
}
log.LogInformation("Sync finished");
