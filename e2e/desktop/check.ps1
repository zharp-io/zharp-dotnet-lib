# Runs a desktop sample app through its scenarios against the stand-in Zharp (e2e/mock_zharp.py)
# and checks what arrived. Usage: check.ps1 -Exe <path> -Name <WpfApp|FormsApp> -Out <dir> -UiError <mode>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Name,
    [Parameter(Mandatory)] [string] $Out,
    # The scenario with an exception on the UI thread that the app survives: "handled" (WPF), "ui-error" (Windows Forms).
    [string] $UiError = "handled"
)
$ErrorActionPreference = "Stop"
$env:ZHARP_KEY = "zpk_desktop_e2e"
$env:ZHARP_ENDPOINT = "http://127.0.0.1:4318/otlp"
$script:fail = @()
function Expect($ok, $what) { if ($ok) { "PASS  $Name $what" } else { "FAIL  $Name $what"; $script:fail += "$Name $what" } }

function Start-Mock {
    $script:mock = Start-Process python -ArgumentList "e2e/mock_zharp.py", $Out -PassThru -RedirectStandardOutput "$Out-mock.log" -RedirectStandardError "$Out-mock.err"
    for ($i = 0; $i -lt 20; $i++) {
        try { Invoke-WebRequest http://127.0.0.1:4318/ping -Method Post -Body "" -UseBasicParsing | Out-Null; return } catch { Start-Sleep -Milliseconds 300 }
    }
    throw "the stand-in endpoint did not start"
}
function Stop-Mock { if ($script:mock) { Stop-Process -Id $script:mock.Id -Force -ErrorAction SilentlyContinue; $script:mock = $null; Start-Sleep 1 } }

function Run-App([string] $mode, [int] $seconds = 30) {
    $p = Start-Process $Exe -ArgumentList $mode -PassThru -RedirectStandardError "$Out-$Name-$mode.err"
    if (-not $p.WaitForExit($seconds * 1000)) { Stop-Process -Id $p.Id -Force; "  (${mode}: still running after $seconds s, stopped)" }
    "  $Name $mode exited with $($p.ExitCode)"
    Get-Content "$Out-$Name-$mode.err" -ErrorAction SilentlyContinue | Select-Object -Last 15 | ForEach-Object { "    $_" }
    return $p.ExitCode
}

function Sent() {
    $files = Get-ChildItem $Out -File -Filter "otlp_v1_*" -ErrorAction SilentlyContinue
    return ($files | ForEach-Object { [System.Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($_.FullName)) }) -join "`n"
}
function Clear-Sent { Get-ChildItem $Out -File -Filter "otlp_v1_*" -ErrorAction SilentlyContinue | Remove-Item -Force }

New-Item -ItemType Directory -Force $Out | Out-Null
# A clean slate: no outbox, session or device ID from an earlier job.
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Zharp\$Name" -ErrorAction SilentlyContinue
Start-Mock

# 1. A caught error: lines found later from the PDB, the session, device, screen and OS on it.
Clear-Sent
Run-App "error" | Write-Output
$sent = Sent
Expect ($sent -match "Desktop cart is empty") "caught error sent"
Expect ($sent -match "zharp.il_frames" -and $sent -match '"token":\d+,"il_offset":\d+') "line-less frames carry token and IL offset"
Expect ($sent -match "at Shop\.Desktop\.Checkout\.Total\(Int32\[\] prices\)") "the stack reads as .NET writes it"
Expect ($sent -match "session\.id" -and $sent -match "device\.id") "session and device"
Expect ($sent -match "screen\.name" -and ($sent -match "MainWindow" -or $sent -match "MainForm")) "the window or form is the screen"
Expect ($sent -match "screen MainWindow" -or $sent -match "screen MainForm") "a screen span"
Expect ($sent -match "os\.name" -and $sent -match "Windows") "os.name Windows"
$mvid = [regex]::Match($sent, '"mvid":"([0-9a-f-]{36})"').Groups[1].Value
Expect ($mvid -and (Test-Path "$Out/artifacts/portable-pdb-$mvid-$Name.pdb")) "the frames' MVID is the one Zharp.Build uploaded the PDB under ($mvid)"
Expect ($mvid -and (Test-Path "$Out/artifacts/source-bundle-$mvid-sources.json.gz")) "the source bundle is under the same MVID"
Expect ($sent -match "zharp\.build_id" -and $sent -match $mvid) "zharp.build_id is the MVID"
Copy-Item -Recurse "$Out" "$Out-error-snapshot" -Force

# 2. An exception on the UI thread the app survives.
Clear-Sent
Run-App $UiError | Write-Output
$sent = Sent
Expect ($sent -match "Desktop cart is empty") "UI-thread exception reported ($UiError)"
if ($UiError -eq "handled") { Expect ($sent -match "zharp.mechanism.{0,6}wpf") "mechanism wpf" } else { Expect ($sent -match "zharp.mechanism.{0,6}winforms") "mechanism winforms" }

# 3. A crash while offline: written to disk, sent on the next launch.
Clear-Sent
Stop-Mock
Run-App "crash" | Write-Output
$outbox = Get-ChildItem "$env:LOCALAPPDATA\Zharp\$Name\outbox" -File -ErrorAction SilentlyContinue
"  outbox after the crash: $(($outbox | ForEach-Object Name) -join ', ')"
Expect ($outbox.Count -ge 1) "the crash was written to disk while offline"
Start-Mock
Run-App "quit" | Write-Output
Start-Sleep 2
$sent = Sent
Expect ($sent -match "Desktop cart is empty" -and $sent -match "zharp.error.kind" -and $sent -match "crash") "the crash was sent on the next launch"
Expect (-not (Get-ChildItem "$env:LOCALAPPDATA\Zharp\$Name\outbox" -File -ErrorAction SilentlyContinue)) "the outbox is empty after sending"
Copy-Item -Recurse "$Out" "$Out-crash-snapshot" -Force

# 4. The UI thread blocked for longer than the threshold (2 s here).
Clear-Sent
Run-App "hang" | Write-Output
$sent = Sent
Expect ($sent -match "ApplicationNotResponding" -and $sent -match "The UI thread was blocked") "the hang reported"

Stop-Mock
if ($script:fail.Count) { throw "failed: $($script:fail -join '; ')" }
