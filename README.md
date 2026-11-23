# Zharp for .NET

Errors with the code that ran, traces, failed database queries, request headers and (optionally)
logs from .NET apps, sent to [Zharp](https://zharp.io) over OpenTelemetry.

The quickest setup is `npm init zharp@latest` in the project folder: it adds the package, the one
line in `Program.cs`, the app's key and `<EmbedAllSources>`. By hand, pick your app below.

| Package | For |
|---|---|
| `Zharp.AspNetCore` | ASP.NET Core (.NET 8, 10, 11) |
| `Zharp` | any .NET app: Generic Host workers, console apps, .NET/HTTP (logs and errors only); .NET 8+, .NET Framework 4.6.2+, .NET Standard 2.0 |
| `Zharp.AspNet` | classic ASP.NET on .NET Framework (MVC 5, WebForms) |
| `Zharp.AspNet.WebApi` | ASP.NET Web API 2 on .NET Framework (with `Zharp.AspNet`) |
| `Zharp.AwsLambda` | .NET on AWS Lambda |
| `Zharp.GoogleCloudFunctions` | .NET on Google Cloud Run functions (Cloud Functions) |
| `Zharp.Build` | uploads a source bundle for builds that don't embed their sources (optional); for apps, the PDBs and sources that give release stacks their lines |
| `Zharp.Maui` | .NET MAUI on Android, iOS, Mac Catalyst and Windows (.NET 10) |
| `Zharp.Windows` | WPF and Windows Forms (.NET 8+, .NET Framework 4.6.2+) |
| `Zharp.Godot` | the C# side of Godot 4.5+ .NET games, with the [Zharp Godot addon](https://github.com/zharp-io/zharp-godot) |

## ASP.NET Core

```sh
dotnet add package Zharp.AspNetCore
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddZharp();
```

```json
// appsettings.json
"Zharp": { "Key": "zpk_…" }
```

Requests are traced and named by route (`GET /orders/{id}`); each failure is reported once — even
when `UseExceptionHandler` or the developer page handled it — with the code that ran; request and
response headers are recorded with secrets as `[Filtered]`; failed queries (EF Core, Npgsql,
SqlClient, MySqlConnector, SQLite) come with the statement, the database's code and a named type
(`UniqueViolation`, `UniqueKeyViolation`); the browser SDK's trace and user are continued.

## .NET (worker, console)

On the Generic Host, the same line with the `Zharp` package:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZharp();
```

In a plain `Main`:

```csharp
using var zharp = Zharp.ZharpSdk.Init(o => o.Key = "zpk_…");   // or ZHARP_KEY in the environment
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().AddZharp());

using (Zharp.ZharpSdk.StartSpan("import-orders"))
{
    // a job: its logs, queries and errors grouped as one trace
}
```

Unhandled exceptions are reported before the process ends; disposing `zharp` sends what's waiting.

## .NET/HTTP (logs and errors only)

```csharp
using var zharp = Zharp.ZharpSdk.Init(o => { o.Key = "zpk_…"; o.Tracing = false; o.Logs = true; });
```

## ASP.NET (classic, .NET Framework)

```powershell
Install-Package Zharp.AspNet
```

```xml
<!-- Web.config -->
<appSettings>
  <add key="Zharp:Key" value="zpk_…" />
</appSettings>
```

The package registers its HTTP modules itself (no `<modules>` edit). What reaches
`Application_Error` is reported once. .NET Framework 4.7.2+ reads the portable PDBs that carry the
sources; on 4.6.2–4.7.1 use `<DebugType>full</DebugType>`.

Web API 2 handles its controllers' exceptions itself, before `Application_Error`: use
`Zharp.AspNet.WebApi` (it brings `Zharp.AspNet`) and one line in `WebApiConfig.Register`:

```powershell
Install-Package Zharp.AspNet.WebApi
```

```csharp
public static void Register(HttpConfiguration config)
{
    config.AddZharp();
    …
}
```

## AWS Lambda (.NET)

```sh
dotnet add package Zharp.AwsLambda
```

```csharp
public class Function
{
    static readonly Func<Order, ILambdaContext, Task<Receipt>> Handle =
        Zharp.AwsLambda.ZharpLambda.Wrap<Order, Receipt>(PlaceAsync);

    public Task<Receipt> Handler(Order order, ILambdaContext context) => Handle(order, context);

    static async Task<Receipt> PlaceAsync(Order order, ILambdaContext context) { … }
}
```

Set `ZHARP_KEY` in the function's environment. Each invocation is a span (the first marked
`faas.coldstart`); what the handler throws is reported once; everything is sent before the handler
returns. ASP.NET Core on Lambda (`AddAWSLambdaHosting`) uses `Zharp.AspNetCore` and `AddZharp()`.

## Google Cloud Functions (.NET)

```sh
dotnet add package Zharp.GoogleCloudFunctions
```

```csharp
[assembly: FunctionsStartup(typeof(Zharp.GoogleCloudFunctions.ZharpStartup))]
```

Deploy with `--set-env-vars ZHARP_KEY=zpk_…`. Invocations are spans named by the function, and
everything is sent before the response.

## Apps: .NET MAUI, WPF, Windows Forms

```sh
dotnet add package Zharp.Maui        # or Zharp.Windows
dotnet add package Zharp.Build       # PrivateAssets="all": release stacks get their lines
```

```csharp
// MauiProgram.cs
builder.UseMauiApp<App>().UseZharp(o => o.Key = "zpk_…");

// WPF: first in App.OnStartup; Windows Forms: first in Main
Zharp.ZharpApp.Start(o => o.Key = "zpk_…");
```

As the Android, iOS and Flutter SDKs: crashes are written to disk and sent at once or on the next
launch; a UI thread blocked for 5 s is reported as `ApplicationNotResponding`; pages, windows and
forms are screens (`screen.name`, and a `screen …` span); `session.id` (a new one after 30 minutes
in the background, or on `ClearUser`) and `device.id` are on everything; what can't be sent waits
in a capped outbox on disk (200 batches, 8 MB, 3 days) and is retried.

Apps ship without PDBs, so their frames have no lines. For each line-less frame of the app's own
code the SDK sends the method token, IL offset and module MVID (`zharp.il_frames`); `Zharp.Build`
uploads each Release build's portable PDBs and sources under the MVID (`portable-pdb`,
`source-bundle`), again after ILLink for trimmed builds, and Zharp maps the offsets to lines.
Set `<ZharpAppId>` and `ZHARP_AUTH_TOKEN` in CI; without a token it is a dry run. Apps never embed
their sources.

Not yet: native crashes (iOS signals, the Android NDK, Windows SEH), and the UI thread's stack in a
hang report (.NET can't read another thread's stack). On iOS, AOT-compiled code reports no IL
offsets: release frames get their file and method, at the method's first line rather than the
exact one. `npm init zharp` edits C# projects; set up a Visual Basic app by hand (the same calls).

## Godot (C#)

Godot .NET games use the [Zharp Godot addon](https://github.com/zharp-io/zharp-godot) for script
and engine errors, crashes, hangs and scenes, and add `Zharp.Godot` for the C# side:

```sh
dotnet add package Zharp.Godot
```

```csharp
public partial class Main : Godot.Node
{
    public override void _Ready() => Zharp.Godot.ZharpGodot.Start(this);
}
```

It reads the addon's settings (`[zharp]` in `project.godot`) and reports exceptions that end the
game, on any thread (saved to disk first, sent at once or on the next launch), exceptions passed to
`ZharpGodot.CaptureException`, and with logs on `ILogger` lines — all with the addon's `session.id`,
`device.id`, scene (`screen.name`) and player. `ZharpGodot.SetUser` / `ClearUser` set both sides.
The addon watches the main thread for hangs, so the package doesn't. Godot loads a game into its own
load context, where the runtime's `HttpClient` diagnostics don't reach the SDK: trace requests with
`new HttpClient(ZharpGodot.HttpHandler())`.

## The code that ran

Add to the project file:

```xml
<EmbedAllSources>true</EmbedAllSources>
```

The build then carries its `.cs` files inside the PDB, and the SDK reads the lines around each
error from it on your own server and sends them (`zharp.source_context`): nothing to upload, and
always the exact code that ran. (It needs a PDB: not with `<DebugType>none</DebugType>`.) Builds
that can't embed sources reference `Zharp.Build`:

```xml
<PackageReference Include="Zharp.Build" Version="0.1.0" PrivateAssets="all" />
```

With `ZHARP_APP_ID` and `ZHARP_AUTH_TOKEN` (a workspace API key with `artifacts:write`) set in CI,
each Release build uploads the project's sources under its PDB ID, which errors carry as
`zharp.build_id`. Without a token the step only prints what it would upload.

## Troubleshooting

**A Release stack ends in `lambda_method…` with no frame of my code.** In Release builds the JIT
inlines small methods into their callers. A short controller action or minimal API handler can be
inlined into the delegate ASP.NET Core compiles to call it (`lambda_method12(Closure, Object,
Object[])`), so the stack has no frame of yours and no code to show. The error is still grouped
and counted as usual. A Debug build shows the method, and a linked repository (or source bundle)
still shows the code of any of your frames that remain. Nothing needs changing in your code.

**No code shown with errors.** Check that the project sets `<EmbedAllSources>true</EmbedAllSources>`
and ships its `.pdb` files (`dotnet publish` does by default). A build with
`<DebugType>none</DebugType>` has no PDB at all — no file names or line numbers in its stacks, and no
build ID for `Zharp.Build` either — so no code can be shown. Switch to `<DebugType>portable</DebugType>`
(or `embedded`, which keeps the PDB inside the DLL).

## Settings

Code (`AddZharp(o => …)`, `Init(o => …)`) wins, then the environment, then the `"Zharp"` section of
the configuration (`appsettings.json`, or `Zharp:` keys in `<appSettings>`).

| Setting | Environment | Default |
|---|---|---|
| `Key` | `ZHARP_KEY`, `ZHARP_API_KEY` | — (nothing is sent) |
| `Service` | `ZHARP_SERVICE`, `OTEL_SERVICE_NAME` | the entry assembly's name |
| `Environment` | `ZHARP_ENVIRONMENT`, `ZHARP_ENV`, `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT` | — |
| `Release` | `ZHARP_RELEASE` | the informational version (`1.4.0+3f2c9ab…`, commit included) |
| `Endpoint` | `ZHARP_ENDPOINT` | `https://api.zharp.io/api/v1/otlp` |
| `Logs` | `ZHARP_LOGS` | off: the Zharp agent collects your output |
| `Tracing` | `ZHARP_TRACING` | on |
| `LogCorrelation` | `ZHARP_LOG_CORRELATION` | on: ` trace_id=… span_id=…` on console lines |
| `CaptureHeaders`, `RedactHeaders` | `ZHARP_CAPTURE_HEADERS`, `ZHARP_CAPTURE_HEADERS_REDACT` | on |
| `CaptureBodies` | `ZHARP_CAPTURE_BODIES` (`errors`, `all`) | off |
| `CaptureQueryErrors` | `ZHARP_CAPTURE_QUERY_ERRORS` | on |
| `CaptureSourceContext` | `ZHARP_CAPTURE_SOURCE_CONTEXT` | on |
| `SampleRate` | `ZHARP_SAMPLE_RATE` | 1 (errors are always sent) |
| `Disabled` | `ZHARP_DISABLED` | off |

With logs off (the default), exceptions logged at Error are still reported as errors, and console
lines carry the request's ids so the agent links them. Serilog: add
` trace_id={TraceId} span_id={SpanId}` to the output template for the same stamp; with
`ZHARP_LOGS=true` and `UseSerilog(writeToProviders: true)` its events arrive through ILogger.

API: `ZharpSdk.CaptureException(e)`, `ZharpSdk.SetUser(id, email, name)`, `ZharpSdk.ClearUser()`,
`ZharpSdk.SetTag(key, value)`, `ZharpSdk.StartSpan(name)`, `ZharpSdk.Flush()`.

## Development

.NET doesn't need to be installed: everything builds and runs in Docker.

```sh
e2e/unit.sh                       # unit tests on .NET 8, 10 and 11
e2e/pack.sh                       # the packages, into artifacts/packages
ZHARP_E2E_TOKEN_FILE=… node e2e/run.mjs [scenario…]   # end to end against a local Zharp
```

Classic ASP.NET runs on Windows only: `.github/workflows/windows-aspnet-e2e.yml` (IIS Express).
