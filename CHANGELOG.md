# Changelog

## 0.2.0 (apps)

- `Zharp.Godot`: `ZharpGodot.Start(this)` for Godot 4.5+ .NET games, joined to the Zharp Godot
  addon's session, device, scene and player; `ZharpGodot.HttpHandler()` traces HttpClient (Godot's
  load context hides the runtime's own HttpClient diagnostics). An app host can now supply the
  device ID, session and screen another SDK in the same app owns.
- `Zharp.Maui`: `builder.UseZharp()` on Android, iOS, Mac Catalyst and Windows.
- `Zharp.Windows`: `ZharpApp.Start()` for WPF and Windows Forms, on .NET 8+ and .NET Framework 4.6.2+.
- Apps: crashes written to disk first and sent on the next launch; hangs (UI thread blocked 5 s);
  pages, windows and forms as screens; sessions and a device ID; an on-disk outbox with retries.
- Release stacks without lines: `zharp.il_frames` (method token, IL offset, MVID) for the app's
  own frames; `Zharp.Build` uploads apps' portable PDBs and sources under each assembly's MVID,
  as built and after ILLink.
- `ZHARP_DEBUG` also prints OpenTelemetry's own warnings.

Known limits: on iOS (AOT-compiled) the runtime reports no IL offsets, so release frames get their
file and method but the method's first line, not the exact line. Native crashes (iOS signals, the
Android NDK, Windows SEH) aren't captured, and a hang report has no UI-thread stack. `npm init
zharp` sets up C# projects; Visual Basic apps are set up by hand.

## 0.1.0 (servers)

First release, for servers. Apps (MAUI, WPF, Windows Forms) follow in a later release.

- `Zharp`: `AddZharp()` on any Generic Host, `ZharpSdk.Init()` anywhere else (.NET 8+, .NET
  Framework 4.6.2+, .NET Standard 2.0). Errors reported once each — unhandled, unobserved, logged
  at Error, or passed to `CaptureException` — with stacks always in English and the code that ran,
  read from sources embedded in the PDB (`zharp.source_context`). HttpClient and SqlClient traced;
  Npgsql and MySqlConnector trace themselves. Failed queries (any EF Core provider, Npgsql,
  SqlClient, MySqlConnector, SQLite) with statement, database, code and a named type; EF Core's
  checks for whether the database exists yet are not reported. The
  `trace_id=… span_id=…` stamp on console lines; logs sent only with `ZHARP_LOGS=true`. User and
  session from the browser SDK's baggage or `SetUser`. Tracing off for logs-and-errors-only setups.
- `Zharp.AspNetCore`: requests named by route, failures from ASP.NET Core's diagnostics (seen
  even when an exception handler answered), request and response headers with secrets filtered,
  request and response bodies with `ZHARP_CAPTURE_BODIES`.
- `Zharp.AspNet`: classic ASP.NET, installed by referencing the package; key in `<appSettings>`.
- `Zharp.AspNet.WebApi`: `config.AddZharp()` for Web API 2 controllers.
- `Zharp.AwsLambda`: `ZharpLambda.Wrap(handler)`: invocation spans, cold starts, errors, sent
  before the function freezes.
- `Zharp.GoogleCloudFunctions`: one `FunctionsStartup` attribute.
- `Zharp.Build`: source bundle upload after Release builds, under the PDB's ID (`zharp.build_id`).
