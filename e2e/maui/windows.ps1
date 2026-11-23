# MAUI on Windows (WinUI, unpackaged): the ShopApp sample against the stand-in Zharp
# (e2e/mock_zharp.py) through a caught error, a crash on the UI thread while offline (sent on the
# next launch) and a hang. Usage: windows.ps1 -Exe <ShopApp.exe> -Out <dir> -Build <build dir>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Out,
    [Parameter(Mandatory)] [string] $Build
)
$ErrorActionPreference = "Stop"
$script:fail = @()
function Expect($ok, $what) { if ($ok) { "PASS  maui-windows $what" } else { "FAIL  maui-windows $what"; $script:fail += $what } }

function Start-Mock {
    $script:mock = Start-Process python -ArgumentList "e2e/mock_zharp.py", $Out -PassThru -RedirectStandardOutput "$Out-mock.log" -RedirectStandardError "$Out-mock.err"
    for ($i = 0; $i -lt 20; $i++) {
        if ($script:mock.HasExited) { throw "the stand-in endpoint exited (port 4318 taken?)" }
        try { Invoke-WebRequest http://127.0.0.1:4318/ping -Method Post -Body "" -UseBasicParsing | Out-Null; return } catch { Start-Sleep -Milliseconds 300 }
    }
    throw "the stand-in endpoint did not start"
}
function Stop-Mock { if ($script:mock) { Stop-Process -Id $script:mock.Id -Force -ErrorAction SilentlyContinue; $script:mock = $null; Start-Sleep 1 } }

# The app never quits by itself: it runs for $seconds, then is stopped (unless it crashed first).
function Run-App([string] $mode, [int] $seconds = 20) {
    $env:ZHARP_E2E_MODE = $mode
    $p = Start-Process $Exe -ArgumentList $mode -PassThru -RedirectStandardError "$Out-$mode.err" -RedirectStandardOutput "$Out-$mode.log"
    if (-not $p.WaitForExit($seconds * 1000)) { Stop-Process -Id $p.Id -Force; "  (${mode}: stopped after $seconds s)" } else { "  $mode exited with $($p.ExitCode)" }
    Get-Content "$Out-$mode.err", "$Out-$mode.log" -ErrorAction SilentlyContinue | Select-Object -Last 15 | ForEach-Object { "    $_" }
}

function Sent() {
    $files = Get-ChildItem $Out -File -Filter "otlp_v1_*" -ErrorAction SilentlyContinue
    return ($files | ForEach-Object { [System.Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($_.FullName)) }) -join "`n"
}
function Clear-Sent { Get-ChildItem $Out -File -Filter "otlp_v1_*" -ErrorAction SilentlyContinue | Remove-Item -Force }
function Outbox { Get-ChildItem $env:LOCALAPPDATA -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '[\\/]zharp[\\/]outbox[\\/]' } }

New-Item -ItemType Directory -Force $Out | Out-Null
Start-Mock

# 1. A caught error: what finds its lines, the session, device, screen and OS on it.
Clear-Sent
Run-App "error" | Write-Output
$sent = Sent
Expect ($sent -match "Mobile cart is empty") "caught error sent"
Expect ($sent -match "zharp.il_frames" -and $sent -match '"token":\d+,"il_offset":-?\d+') "line-less frames carry token and IL offset"
Expect ($sent -match "at Shop\.Mobile\.Checkout\.Total\(Int32\[\] prices\)") "the stack reads as .NET writes it"
Expect ($sent -match "session\.id" -and $sent -match "device\.id") "session and device"
Expect ($sent -match "screen\.name" -and $sent -match "MainPage") "the page is the screen"
Expect ($sent -match "os\.name" -and $sent -match "Windows") "os.name Windows"
$mvid = [regex]::Match($sent, '"mvid":"([0-9a-f-]{36})"').Groups[1].Value
"  frames' MVID: $mvid; uploads: $((Get-ChildItem "$Build/artifacts" -Name) -join ' ')"
Expect ($mvid -and (Test-Path "$Build/artifacts/portable-pdb-$mvid-ShopApp.pdb")) "the frames' MVID is the one Zharp.Build uploaded the PDB under"
Expect ($mvid -and (Test-Path "$Build/artifacts/source-bundle-$mvid-sources.json.gz")) "the source bundle is under the same MVID"
Copy-Item -Recurse "$Out" "$Out-error-snapshot" -Force

# 2. A crash on the UI thread while offline: written to disk, sent on the next launch.
Clear-Sent
Stop-Mock
Run-App "crash" | Write-Output
"  outbox after the crash: $((Outbox | ForEach-Object Name) -join ', ')"
Expect ((Outbox).Count -ge 1) "the crash was written to disk while offline"
Start-Mock
Run-App "quit" | Write-Output
$sent = Sent
Expect ($sent -match "Mobile cart is empty" -and $sent -match "zharp.error.kind" -and $sent -match "crash") "the crash was sent on the next launch"
Expect ($sent -match "zharp\.mechanism[\s\S]{0,8}winui") "mechanism winui"
Copy-Item -Recurse "$Out" "$Out-crash-snapshot" -Force

# 3. The UI thread blocked for longer than the threshold (2 s here).
Clear-Sent
Run-App "hang" | Write-Output
$sent = Sent
Expect ($sent -match "ApplicationNotResponding" -and $sent -match "The UI thread was blocked") "the hang reported"

Stop-Mock
if ($script:fail.Count) { throw "failed: $($script:fail -join '; ')" }
