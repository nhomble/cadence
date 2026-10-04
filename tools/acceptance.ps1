#requires -version 5.1
<#
.SYNOPSIS
  End-to-end acceptance of Cadence on this machine (the build, test and acceptance plan).

.DESCRIPTION
  Drives the REAL bin\Cadence.exe (CLI, and the live window through UI Automation) against the REAL Windows Task Scheduler,
  always with --folder CadenceTest / CADENCE_FOLDER=CadenceTest. It is safe to re-run: it removes everything it made.

  What it touches (and nothing else):
    * tasks named "Cadence Acceptance*" in \CadenceTest\ (all dated 2099 or disabled, except the one "app closed" task, which is
      cron-pinned to one specific minute 1-2 minutes ahead and deleted right after it ran),
    * %LOCALAPPDATA%\CadenceTest (run records, scratch files),
    * HKCU\Software\CadenceTest (only if the UI created it).
  It never creates \Cadence, %LOCALAPPDATA%\Cadence or HKCU\Software\Cadence, and it diffs every task outside \CadenceTest (name and
  XML hash) before and after.

  Antivirus rule (README "Safety notes"): if Windows blocks a launch, the step prints KNOWN-ISSUE, checks that Cadence reports the run
  as "Didn't report" with the blocked message, skips that job's dependent checks and goes on. Nothing is renamed, retried under another
  name, excluded or tuned.

  The agent journeys A1-A13 start the real MCP server (bin\Cadence.exe --mcp --folder CadenceTest) through
  tools\McpClient.psm1 (System.Diagnostics.Process, CreateNoWindow, UTF-8 bytes, a timeout on every read) and act as an AI agent:
  preview, create, retry, edit, run a harmless job, enable/disable, suspend/resume, locked and foreign tasks, hostile input, two
  writers, delete, clean shutdown. Names "Cadence Acceptance Agent*", all in \CadenceTest, dated in the past year (next run next
  year) and the one real run is `cmd /c echo`. The first FAIL stops the remaining agent journeys; cleanup always runs.

  Exit code: 0 when no FAIL; 1 when any FAIL; 2 when it could not start (preconditions). KNOWN-ISSUE lines do not fail the run.

.PARAMETER SkipUi
  Skip the two UI Automation journeys (useful on a machine without an interactive desktop).

.PARAMETER SkipClosedApp
  Skip the "close the app and jobs still run" journey (it waits 1-2 minutes for a real trigger).

.PARAMETER SkipAgents
  Skip the agent journeys A1-A13 (an AI agent working through bin\Cadence.exe --mcp).

.PARAMETER AgentsOnly
  Run only the agent journeys A1-A13 (and the final invariants).
#>
[CmdletBinding()]
param([switch]$SkipUi, [switch]$SkipClosedApp, [switch]$AgentsOnly, [switch]$SkipAgents)

$ErrorActionPreference = 'Stop'
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path -LiteralPath $userDotnet) {
    $env:PATH = $userDotnet + ';' + $env:PATH
    if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = $userDotnet }   # a per-user runtime is not found by the apphost otherwise
}
$env:DOTNET_DISABLE_GUI_ERRORS = '1'
$env:CADENCE_FOLDER = 'CadenceTest'
$env:CADENCE_TEST_QUIET = '1'   # the real window opens off-screen, unfocused and without a taskbar button (test folder only), so this run never flashes on your desktop

$Repo = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'McpClient.psm1') -Force -DisableNameChecking
$Exe = Join-Path $Repo 'bin\Cadence.exe'
# The dev-only screenshot tool (tools\Cadence.Render, built by build.cmd / dotnet build into obj\artifacts); the product exe has no --render.
$RenderExe = Join-Path $Repo 'obj\artifacts\bin\Cadence.Render\release\Cadence.Render.exe'
$FolderName = 'CadenceTest'
$FolderPath = '\CadenceTest'
$LocalData = Join-Path $env:LOCALAPPDATA $FolderName
$Work = Join-Path $LocalData 'acc'
$Prefix = 'Cadence Acceptance'
$Blocked = -2147024891   # 0x80070005 as a signed int (the Windows "refused to start" code, README "Safety notes")
$script:Pass = 0; $script:Fail = 0; $script:Known = 0; $script:Seq = 0
$script:UiApp = $null
$script:Created = @{ Reg = $false }

# ---------------------------------------------------------------- output
function Say([string]$m) { Write-Host $m }
function Pass([string]$m) { $script:Pass++; Write-Host ("PASS  " + $m) -ForegroundColor Green }
function Fail([string]$m) { $script:Fail++; Write-Host ("FAIL  " + $m) -ForegroundColor Red }
function Known([string]$m) { $script:Known++; Write-Host ("KNOWN-ISSUE  " + $m) -ForegroundColor Yellow }
function Info([string]$m) { Write-Host ("      " + $m) -ForegroundColor DarkGray }
function Check([string]$label, $ok, [string]$detail = '') {
    if ($ok) { Pass $label } elseif ($detail) { Fail ($label + '  [' + $detail + ']') } else { Fail $label }
}
function Journey([string]$title, [scriptblock]$body) {
    if ($AgentsOnly -and $title -notmatch '^(A\d|Final)') { return }
    if ($SkipAgents -and $title -match '^A\d') { return }
    Say ''
    Say ('== ' + $title)
    try { & $body } catch { Fail ($title + ' aborted: ' + $_.Exception.Message); Info ($_.ScriptStackTrace -replace "`r?`n", ' | ') }
}

# ---------------------------------------------------------------- the CLI
function Quote-Arg([string]$a) { if ($a -match '[\s"]') { '"' + $a.Replace('"', '\"') + '"' } else { $a } }

function Invoke-Cad([string[]]$CadArgs, [int]$TimeoutSec = 90, [switch]$NoFolder, [string]$ExePath = $Exe) {
    $script:Seq++
    $o = Join-Path $Work ('cli' + $script:Seq + '.out.txt')
    $e = Join-Path $Work ('cli' + $script:Seq + '.err.txt')
    $all = @($CadArgs)
    if (-not $NoFolder) { $all += @('--folder', $FolderName) }
    $line = ($all | ForEach-Object { Quote-Arg $_ }) -join ' '
    $p = Start-Process -FilePath $ExePath -ArgumentList $line -PassThru -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError $e
    [void]$p.Handle
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { Stop-Process -Id $p.Id -Force } catch { }
        throw ((Split-Path -Leaf $ExePath) + ' ' + $line + ' did not finish in ' + $TimeoutSec + ' s')
    }
    $p.WaitForExit()
    $out = ''; $err = ''
    if (Test-Path -LiteralPath $o) { $out = [string](Get-Content -LiteralPath $o -Raw) }
    if (Test-Path -LiteralPath $e) { $err = [string](Get-Content -LiteralPath $e -Raw) }
    [pscustomobject]@{ Code = $p.ExitCode; Out = $out; Err = $err; Line = $line }
}
function Get-Probe([switch]$All) {
    $a = @('--probe'); if ($All) { $a += '--all' }
    $r = Invoke-Cad $a
    if ($r.Code -ne 0) { throw ('--probe failed: ' + $r.Out + $r.Err) }
    $r.Out | ConvertFrom-Json
}
function Get-ProbeJob([string]$id) { @((Get-Probe).jobs | Where-Object { $_.id -eq $id })[0] }
function Get-History([string]$id) {
    $r = Invoke-Cad @('--history', $id, '--json')
    if ($r.Code -ne 0) { throw ('--history failed: ' + $r.Out + $r.Err) }
    $r.Out | ConvertFrom-Json
}

# ---------------------------------------------------------------- Task Scheduler (COM), restricted to \CadenceTest
$script:Svc = New-Object -ComObject 'Schedule.Service'
$script:Svc.Connect()
function Assert-Ours([string]$name) {
    if (-not $name.StartsWith($Prefix, [StringComparison]::Ordinal) -or $name.Contains('\')) { throw "Refusing to touch task '$name': not a '$Prefix*' task name." }
}
function Test-FolderExists([string]$path) {
    try { [void]$script:Svc.GetFolder($path); $true } catch { $false }
}
function Get-TestFolder { $script:Svc.GetFolder($FolderPath) }
function Get-TestTask([string]$name) { try { (Get-TestFolder).GetTask($name) } catch { $null } }
function Get-TestTaskNames { if (-not (Test-FolderExists $FolderPath)) { return @() }; @((Get-TestFolder).GetTasks(1) | ForEach-Object { $_.Name } | Sort-Object) }
function Get-Sha([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-', '') } finally { $sha.Dispose() }
}
function Edit-TriggerHour([string]$name, [int]$hours) {
    Assert-Ours $name
    $folder = Get-TestFolder
    $t = $folder.GetTask($name)
    $def = $t.Definition
    $trg = $def.Triggers.Item(1)
    $sb = [string]$trg.StartBoundary
    $dt = [datetime]::ParseExact($sb.Substring(0, 19), "yyyy-MM-dd'T'HH:mm:ss", [Globalization.CultureInfo]::InvariantCulture)
    $trg.StartBoundary = $dt.AddHours($hours).ToString("yyyy-MM-dd'T'HH:mm:ss", [Globalization.CultureInfo]::InvariantCulture) + $sb.Substring(19)
    # 4 = TASK_UPDATE, logon type 3 = interactive token
    [void]$folder.RegisterTaskDefinition($name, $def, 4, $null, $null, 3, $null)
}
function Edit-TaskXml([string]$name, [scriptblock]$edit) {
    Assert-Ours $name
    $folder = Get-TestFolder
    $xml = [string](& $edit ($folder.GetTask($name).Xml))
    [void]$folder.RegisterTask($name, $xml, 4, $null, $null, 3)
}
function Remove-OurTask([string]$name) {
    Assert-Ours $name
    $folder = Get-TestFolder
    try { $t = $folder.GetTask($name); if ($t.State -eq 4) { $t.Stop(0) } } catch { }
    try { $folder.DeleteTask($name, 0) } catch { }
}

# ---------------------------------------------------------------- run records (read straight from the files, no Cadence.exe needed)
function Get-RecordCount([string]$id) {
    $f = Join-Path $LocalData ('runs\' + $id + '\runs.jsonl')
    if (-not (Test-Path -LiteralPath $f)) { return 0 }
    try { @(Get-Content -LiteralPath $f).Count } catch { -1 }
}
function Get-UnknownCount { Get-RecordCount '_unknown' }
function Get-WrapperProcs([string]$taskName) {
    @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains('\CadenceTest\' + $taskName) })
}
<#
  Waits until job $id has at least $want records. Returns 'record', 'blocked' (Windows refused to start the wrapper: 0x80070005,
  no record, no wrapper process; the known Q6 case) or 'timeout'.
#>
function Wait-Records([string]$id, [string]$taskName, [int]$want, [int]$timeoutSec, $lastRunBefore, [int]$unknownBefore) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((Get-RecordCount $id) -ge $want) { return 'record' }
        $t = Get-TestTask $taskName
        if ($t -and $t.State -ne 4 -and $t.LastTaskResult -eq $Blocked -and $t.LastRunTime -ne $lastRunBefore) {
            Start-Sleep -Seconds 3   # a wrapper that did start writes its record within 1-2 s
            $t = Get-TestTask $taskName
            if ((Get-RecordCount $id) -lt $want -and (Get-UnknownCount) -eq $unknownBefore -and (Get-WrapperProcs $taskName).Count -eq 0 -and $t -and $t.State -ne 4 -and $t.LastTaskResult -eq $Blocked) { return 'blocked' }
        }
        Start-Sleep -Milliseconds 700
    }
    if ((Get-RecordCount $id) -ge $want) { 'record' } else { 'timeout' }
}
function Confirm-BlockedShown([string]$id, [string]$label) {
    Known ($label + ': Task Scheduler refused to start the job (0x80070005) and no wrapper ran. The code does not say why; see the README "Safety notes". Nothing was renamed, retried or excluded.')
    $h = Get-History $id
    $row = @($h.recent)[0]
    Check ($label + ': Cadence shows the blocked launch as "notReported" with the plain message') ($row -and $row.status -eq 'notReported' -and ([string]$row.message) -match 'refused to start' -and ([string]$row.message) -match '0x80070005') ('got ' + ($row | ConvertTo-Json -Compress))
}

# ---------------------------------------------------------------- job files
function New-JobJson([string]$id, [string]$name, [string]$cron, [string]$anchor, [string]$exe, [string]$cmdArgs, $extraRun = @{}, [string]$skip = '') {
    $schedule = [ordered]@{ cron = @($cron); anchor = $anchor }
    if ($skip) { $schedule.skipHolidays = $skip }
    $run = [ordered]@{ kind = 'runProgram'; exe = $exe; args = $cmdArgs; hidden = $true }
    foreach ($k in $extraRun.Keys) { $run[$k] = $extraRun[$k] }
    $job = [ordered]@{ id = $id; name = $name; enabled = $true; schedule = $schedule; run = $run }
    $path = Join-Path $Work ($id + '.json')
    [IO.File]::WriteAllText($path, ($job | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    $path
}
function Apply-Job([string]$jobFile) { Invoke-Cad @('--apply', $jobFile, '--yes') }

# ---------------------------------------------------------------- UI Automation helpers
$script:UiLoaded = $false
function Load-Ui {
    if ($script:UiLoaded) { return }
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $script:AE = [System.Windows.Automation.AutomationElement]
    $script:TS = [System.Windows.Automation.TreeScope]
    $script:PC = [System.Windows.Automation.PropertyCondition]
    $script:UiLoaded = $true
}
function Ui-Start {
    $p = Start-Process -FilePath $Exe -PassThru
    $script:UiApp = $p
    $end = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $end) {
        $w = $script:AE::RootElement.FindFirst($script:TS::Children, (New-Object $script:PC($script:AE::ProcessIdProperty, $p.Id)))
        if ($w) { return $w }
        Start-Sleep -Milliseconds 300
    }
    throw 'The Cadence window did not appear in 25 s.'
}
function Ui-Find($root, [string]$name, [int]$timeoutSec = 15) {
    $end = (Get-Date).AddSeconds($timeoutSec)
    do {
        $e = $root.FindFirst($script:TS::Descendants, (New-Object $script:PC($script:AE::NameProperty, $name)))
        if ($e) { return $e }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $end)
    $null
}
function Ui-FindId($root, [string]$id, [int]$timeoutSec = 15) {
    $end = (Get-Date).AddSeconds($timeoutSec)
    do {
        $e = $root.FindFirst($script:TS::Descendants, (New-Object $script:PC($script:AE::AutomationIdProperty, $id)))
        if ($e) { return $e }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $end)
    $null
}
function Ui-Need($root, [string]$name, [int]$timeoutSec = 15) {
    $e = Ui-Find $root $name $timeoutSec
    if (-not $e) { throw "UI element '$name' not found" }
    $e
}
function Ui-Press($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Ui-Select($el) { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Ui-IsSelected($el) { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
function Ui-Value($el) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Ui-Close {
    $p = $script:UiApp
    if (-not $p) { return }
    try {
        $p.Refresh()
        if (-not $p.HasExited) { [void]$p.CloseMainWindow(); [void]$p.WaitForExit(8000) }
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; [void]$p.WaitForExit(5000) }
    } catch { }
    $script:UiApp = $null
}
function Ui-BackOut($win) {
    # Back to the list; if the editor asks "Discard changes?", discard.
    Ui-Press (Ui-Need $win 'Back' 5)
    $d = Ui-Find $win 'Discard and close' 2
    if (-not $d) { $d = Ui-Find $win 'Discard' 1 }
    if ($d) { Ui-Press $d }
    Start-Sleep -Milliseconds 600
}

# ---------------------------------------------------------------- snapshot of every task outside \CadenceTest
function Add-Snapshot($folder, $acc) {
    foreach ($t in @($folder.GetTasks(1))) {
        $h = 'unreadable'
        try { $h = Get-Sha ([string]$t.Xml) } catch { }
        $acc[$t.Path] = $h
    }
    foreach ($f in @($folder.GetFolders(0))) {
        if ($f.Path -ieq $FolderPath) { continue }
        Add-Snapshot $f $acc
    }
}
function Get-Snapshot { $acc = @{}; Add-Snapshot ($script:Svc.GetFolder('\')) $acc; $acc }

# ================================================================= main
$started = Get-Date
$before = $null
$pre = @{
    CadenceFolder = (Test-FolderExists '\Cadence')
    CadenceData   = (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Cadence'))
    RegCadence    = (Test-Path 'HKCU:\Software\Cadence')
    RegTest       = (Test-Path 'HKCU:\Software\CadenceTest')
}

Say 'Cadence end-to-end acceptance (folder \CadenceTest only)'
Say ('Started ' + $started.ToString('yyyy-MM-dd HH:mm:ss') + ' on ' + $env:COMPUTERNAME + ' as ' + $env:USERNAME)

# ---- preconditions that stop the run before anything is touched
if (-not (Test-Path -LiteralPath $Exe)) { Say ('ERROR: ' + $Exe + ' not found. Run build.cmd first.'); exit 2 }
if (Get-Process -Name Cadence -ErrorAction SilentlyContinue) { Say 'ERROR: Cadence.exe is running. Close it first (the "app closed" journey needs no Cadence process). Nothing was touched.'; exit 2 }
if ($pre.RegTest) { Say 'ERROR: HKCU\Software\CadenceTest already exists (left by an earlier UI run?). Remove it and run again. Nothing was touched.'; exit 2 }

try {
    # clean any leftovers of an earlier interrupted run (the throwaway folder only), then start fresh
    $cleaner = Join-Path $PSScriptRoot 'clean-test-tasks.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $cleaner | Out-Null
    [void](New-Item -ItemType Directory -Force -Path $Work)
    $before = Get-Snapshot
    Say ('Snapshot: ' + $before.Count + ' tasks outside \CadenceTest (names and XML hashes)')

    # ---------------------------------------------------------------- 1
    Journey '1. Preconditions' {
        Check 'bin\Cadence.exe exists' (Test-Path -LiteralPath $Exe)
        $r = Invoke-Cad @('--selftest') -NoFolder
        Check ('--selftest passes (exit ' + $r.Code + ')') ($r.Code -eq 0 -and $r.Out -notmatch '(?m)^FAIL') ($r.Out + $r.Err)
        Check 'no Cadence.exe process is running' (-not (Get-Process -Name Cadence -ErrorAction SilentlyContinue))
        Check 'folder \CadenceTest starts empty' ((Get-TestTaskNames).Count -eq 0)
        $p = Get-Probe
        Check '--probe (empty folder) lists no jobs' (@($p.jobs).Count -eq 0 -and $p.folder -eq 'CadenceTest')
    }

    # ---------------------------------------------------------------- 2
    $foreignName = $Prefix + ' Foreign'
    $foreignHash = $null
    Journey '2. Foreign task is hidden by default and never touched' {
        $xml = '<?xml version="1.0" encoding="UTF-16"?><Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><RegistrationInfo><Description>Acceptance test: a task Cadence did not make (no marker).</Description></RegistrationInfo><Triggers><TimeTrigger><StartBoundary>2099-01-01T00:00:00</StartBoundary><Enabled>false</Enabled></TimeTrigger></Triggers><Principals><Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Settings><Enabled>false</Enabled></Settings><Actions Context="Author"><Exec><Command>cmd.exe</Command><Arguments>/d /c exit 0</Arguments></Exec></Actions></Task>'
        if (-not (Test-FolderExists $FolderPath)) { [void]$script:Svc.GetFolder('\').CreateFolder('CadenceTest') }
        [void](Get-TestFolder).RegisterTask($foreignName, $xml, 6, $null, $null, 3)
        $script:foreignHash = Get-Sha ([string](Get-TestTask $foreignName).Xml)
        Check 'foreign task registered in \CadenceTest (disabled, 2099)' ($null -ne (Get-TestTask $foreignName))
        $p = Get-Probe
        Check '--probe (default) does not list the foreign task' (@($p.jobs).Count -eq 0 -and -not ($p.PSObject.Properties.Name -contains 'foreign'))
        $pa = Get-Probe -All
        Check '--probe --all lists it under "foreign"' (@($pa.foreign | Where-Object { $_.name -eq $foreignName }).Count -eq 1)
    }

    # ---------------------------------------------------------------- 3 (UI: the empty state, then the New schedule sheet)
    if (-not $SkipUi) {
        Journey '3. UI: empty table, New schedule sheet with template cards, editor with the drop zone, Simple/Cron toggle (foreign hidden)' {
            Load-Ui
            $win = Ui-Start
            try {
                $titles = 'Good morning', 'End of day', 'Weekdays at 6pm', 'On the hour', 'Every 15 minutes', 'Nightly at 2am', 'Weekly, Monday 9am', 'At login', 'Start from scratch'
                # Home page empty state: a quiet empty table (headers, one line, a "New schedule" button), no template cards.
                Check 'empty state shows the empty table with the "New schedule" button' (($null -ne (Ui-FindId $win 'ScheduleTable' 15)) -and ($null -ne (Ui-FindId $win 'EmptyNewButton' 15)) -and ($null -ne (Ui-Find $win 'No schedules yet.' 5)))
                $onHome = @($titles | Where-Object { Ui-Find $win $_ 1 })
                Check 'the home page shows no template cards' ($onHome.Count -eq 0) ('found: ' + ($onHome -join ', '))
                Check 'the foreign task is not shown in the app' ($null -eq (Ui-Find $win $foreignName 2))
                # The template cards live in the sheet opened by the toolbar's "New schedule" button.
                Ui-Press (Ui-Need $win 'New schedule' 5)
                $missing = @($titles | Where-Object { -not (Ui-Find $win $_ 6) })
                Check 'New schedule opens the sheet with the nine template cards' ($missing.Count -eq 0) ('missing: ' + ($missing -join ', '))
                Ui-Press (Ui-Need $win 'Every 15 minutes' 5)
                Check 'picking a card opens the editor with the "drop a program" zone' ($null -ne (Ui-FindId $win 'DropZone' 10))
                Check 'the editor offers Browse...' ($null -ne (Ui-Find $win 'Browse' 3))
                Check 'Save cannot succeed without a program (summary says so)' ((Ui-FindId $win 'ChangeSummary' 5).Current.Name -like '*Choose a program*')
                $cron = Ui-Need $win 'Cron view' 5
                Ui-Select $cron
                $box = Ui-Need $win 'Cron schedule' 5
                Check 'Cron view shows the template as cron: */15 * * * *' ((Ui-Value $box) -eq '*/15 * * * *') ('value ' + (Ui-Value $box))
                Ui-Select (Ui-Need $win 'Simple view' 5)
                Check 'back in Simple view (the same schedule)' (Ui-IsSelected (Ui-Need $win 'Simple view' 5))
                Ui-BackOut $win
                Check 'Back returns to the empty table (no sheet, no cards)' (($null -ne (Ui-FindId $win 'EmptyNewButton' 5)) -and ($null -eq (Ui-Find $win 'Start from scratch' 2)))
            } finally { Ui-Close }
            Check 'nothing was saved by browsing' (@((Get-Probe).jobs).Count -eq 0)
        }
    }

    # ---------------------------------------------------------------- 4
    $okId = 'accok001'; $okName = $Prefix + ' every 15 minutes'
    $okFile = $null
    Journey '4. Create from the "Every 15 minutes" template with a dropped program (CLI job JSON); Task Scheduler agrees' {
        $script:okFile = New-JobJson $okId $okName '*/15 * * * *' '2099-01-01' 'C:\Windows\System32\cmd.exe' '/d /c "echo acceptance-ok"'
        $r = Apply-Job $script:okFile
        Check ('--apply creates the task (exit ' + $r.Code + ')') ($r.Code -eq 0 -and $r.Out -match 'Created') ($r.Out + $r.Err)
        $t = Get-TestTask $okName
        Check 'the task exists in \CadenceTest' ($null -ne $t)
        $def = $t.Definition
        Check 'Source = Cadence' ([string]$def.RegistrationInfo.Source -eq 'Cadence') ([string]$def.RegistrationInfo.Source)
        Check 'Documentation starts with cadence/1' (([string]$def.RegistrationInfo.Documentation).StartsWith('cadence/1'))
        $act = $def.Actions.Item(1)
        Check 'action is conhost.exe' (([string]$act.Path) -match 'conhost\.exe$') ([string]$act.Path)
        Check 'action runs powershell.exe with --headless' (([string]$act.Arguments) -match '--headless' -and ([string]$act.Arguments) -match 'powershell\.exe')
        Check 'one trigger, repeating every 15 minutes (Task Scheduler COM)' ($def.Triggers.Count -eq 1 -and [string]$def.Triggers.Item(1).Repetition.Interval -eq 'PT15M') ('interval ' + [string]$def.Triggers.Item(1).Repetition.Interval)
        Check 'next run is in 2099 (it cannot fire during the test)' ($t.NextRunTime.Year -eq 2099) ([string]$t.NextRunTime)
        $st = Get-ScheduledTask -TaskPath '\CadenceTest\' -TaskName $okName
        Check 'Get-ScheduledTask agrees: name, path, enabled' ($st.TaskName -eq $okName -and $st.TaskPath -eq '\CadenceTest\' -and $st.State -ne 'Disabled')
        Check 'Get-ScheduledTask agrees: trigger repeats every 15 minutes' ($st.Triggers.Count -eq 1 -and $st.Triggers[0].Repetition.Interval -eq 'PT15M') ('interval ' + $st.Triggers[0].Repetition.Interval)
        Check 'Get-ScheduledTask agrees: conhost + powershell wrapper' ($st.Actions[0].Execute -match 'conhost\.exe' -and $st.Actions[0].Arguments -match 'powershell\.exe' -and $st.Actions[0].Arguments -match '--headless')
        $j = Get-ProbeJob $okId
        Check '--probe: managed, inSync, cron */15 * * * *' ($j -and $j.ownership -eq 'managed' -and $j.sync -eq 'inSync' -and @($j.cron)[0] -eq '*/15 * * * *') ($j | ConvertTo-Json -Compress)
        Check '--probe: plain-English schedule mentions 15 minutes' ($j.schedule -match '15 minutes') ([string]$j.schedule)
    }

    # ---------------------------------------------------------------- 5
    Journey '5. Run now: the record shows up in --history' {
        $t = Get-TestTask $okName
        $lastBefore = $t.LastRunTime
        $unk = Get-UnknownCount
        $r = Invoke-Cad @('--run', $okId)
        Check ('--run starts the task (exit ' + $r.Code + ')') ($r.Code -eq 0) ($r.Out + $r.Err)
        $w = Wait-Records $okId $okName 1 60 $lastBefore $unk
        if ($w -eq 'blocked') { Confirm-BlockedShown $okId 'run now'; return }
        Check 'a run record appeared within 60 s' ($w -eq 'record') $w
        $h = Get-History $okId
        $row = @($h.recent)[0]
        Check '--history: newest run is ok, exit 0' ($row.status -eq 'ok' -and $row.exitCode -eq 0) ($row | ConvertTo-Json -Compress)
        Check '--history: summary success rate is 1 and last result ok' ($h.summary.successRate -eq 1 -and $h.summary.lastResult -eq 'ok') ($h.summary | ConvertTo-Json -Compress)
        $outFile = Join-Path $LocalData ('runs\' + $okId + '\' + $row.runId + '.out.txt')
        Check 'the captured output holds "acceptance-ok"' ((Test-Path -LiteralPath $outFile) -and (Get-Content -LiteralPath $outFile -Raw) -match 'acceptance-ok') $outFile
    }

    # ---------------------------------------------------------------- 6
    $failId = 'accfail1'; $failName = $Prefix + ' failing job'
    Journey '6. A failing job (exit 3) shows as failed' {
        $f = New-JobJson $failId $failName '0 3 1 1 *' '2099-01-01' 'C:\Windows\System32\cmd.exe' '/d /c "echo boom 1>&2 & exit /b 3"'
        $r = Apply-Job $f
        Check ('apply failing job (exit ' + $r.Code + ')') ($r.Code -eq 0) ($r.Out + $r.Err)
        $lastBefore = (Get-TestTask $failName).LastRunTime
        $unk = Get-UnknownCount
        [void](Invoke-Cad @('--run', $failId))
        $w = Wait-Records $failId $failName 1 60 $lastBefore $unk
        if ($w -eq 'blocked') { Confirm-BlockedShown $failId 'failing job'; return }
        Check 'a run record appeared within 60 s' ($w -eq 'record') $w
        $h = Get-History $failId
        $row = @($h.recent)[0]
        Check '--history: Failed with exit code 3' ($row.status -eq 'failed' -and $row.exitCode -eq 3) ($row | ConvertTo-Json -Compress)
        $errFile = Join-Path $LocalData ('runs\' + $failId + '\' + $row.runId + '.err.txt')
        Check 'stderr holds "boom"' ((Test-Path -LiteralPath $errFile) -and (Get-Content -LiteralPath $errFile -Raw) -match 'boom') $errFile
        $j = Get-ProbeJob $failId
        Check '--probe: lastResult is failed (the list badge source)' ($j.lastResult -eq 'failed' -and $j.badge -eq 'failed') ('lastResult=' + $j.lastResult + ' badge=' + $j.badge)
        Check '--probe: success rate 0' ($j.successRate -eq 0) ([string]$j.successRate)
    }

    # ---------------------------------------------------------------- 7
    $toId = 'acctimeo'; $toName = $Prefix + ' timeout'
    Journey '7. A job that runs too long is stopped and recorded as a timeout' {
        $f = New-JobJson $toId $toName '0 3 1 1 *' '2099-01-01' 'C:\Windows\System32\ping.exe' '-n 30 127.0.0.1' @{ timeoutSec = 3 }
        $r = Apply-Job $f
        Check ('apply timeout job (exit ' + $r.Code + ')') ($r.Code -eq 0) ($r.Out + $r.Err)
        $lastBefore = (Get-TestTask $toName).LastRunTime
        $unk = Get-UnknownCount
        [void](Invoke-Cad @('--run', $toId))
        $w = Wait-Records $toId $toName 1 60 $lastBefore $unk
        if ($w -eq 'blocked') { Confirm-BlockedShown $toId 'timeout job'; return }
        Check 'a run record appeared within 60 s' ($w -eq 'record') $w
        $h = Get-History $toId
        $row = @($h.recent)[0]
        Check '--history: timedOut' ($row.status -eq 'timedOut') ($row | ConvertTo-Json -Compress)
        Check 'the run was stopped after about 3 s (not 29 s)' ($row.durationMs -lt 15000) ([string]$row.durationMs)
        Start-Sleep -Seconds 2
        $pings = @(Get-CimInstance Win32_Process -Filter "Name='PING.EXE'" | Where-Object { $_.CommandLine -match '-n 30 127\.0\.0\.1' })
        Check 'the ping it started is gone' ($pings.Count -eq 0) ($pings.Count.ToString() + ' left')
    }

    # ---------------------------------------------------------------- 8
    $holId = 'acchol01'; $holName = $Prefix + ' holiday skip'
    Journey '8. Business-day skip: a holiday run is recorded as skipped, not failed, and the program does not run' {
        $holDir = Join-Path $Work 'hol'
        [void](New-Item -ItemType Directory -Force -Path $holDir)
        $marker = Join-Path $holDir 'hol.txt'
        if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
        $f = New-JobJson $holId $holName '0 18 * * 1-5' '2099-01-01' 'C:\Windows\System32\cmd.exe' '/d /c "echo ran>hol.txt"' @{ cwd = $holDir } 'nyse'
        $r = Apply-Job $f
        Check ('apply holiday job (exit ' + $r.Code + ')') ($r.Code -eq 0) ($r.Out + $r.Err)
        $today = (Get-Date).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        $natural = ([string](Get-TestTask $holName).Definition.RegistrationInfo.Documentation).Contains('"' + $today + '":')
        if (-not $natural) {
            # simulate a holiday: add today's date to the stored holiday list (what a real holiday would be)
            Edit-TaskXml $holName { param($x) $x.Replace('"dates":{', '"dates":{"' + $today + '":"Acceptance test holiday",') }
        } else { Info "today ($today) is already in the NYSE list; no patch needed" }
        Check 'today is in the job''s skip list' (([string](Get-TestTask $holName).Definition.RegistrationInfo.Documentation).Contains('"' + $today + '":'))
        $lastBefore = (Get-TestTask $holName).LastRunTime
        $unk = Get-UnknownCount
        $rr = Invoke-Cad @('--run', $holId)
        Check ('--run starts the task (exit ' + $rr.Code + ')') ($rr.Code -eq 0) ($rr.Out + $rr.Err)
        $w = Wait-Records $holId $holName 1 60 $lastBefore $unk
        if ($w -eq 'blocked') { Confirm-BlockedShown $holId 'holiday job'; return }
        Check 'a run record appeared within 60 s' ($w -eq 'record') $w
        $h = Get-History $holId
        $row = @($h.recent)[0]
        Check '--history: skipped, message "holiday: ..."' ($row.status -eq 'skipped' -and ([string]$row.message) -like 'holiday:*') ($row | ConvertTo-Json -Compress)
        Check 'the program did not run (no marker file)' (-not (Test-Path -LiteralPath $marker))
        $j = Get-ProbeJob $holId
        Check '--probe: lastResult is not failed' ($j.lastResult -ne 'failed') ([string]$j.lastResult)
        Check 'success rate is not hurt (null or 1)' ($null -eq $h.summary.successRate -or $h.summary.successRate -eq 1) ([string]$h.summary.successRate)
        if (-not $natural) {
            # control: restore the job (re-apply the original) and run again; now the program must run
            $r2 = Apply-Job $f
            Check ('control: --apply restores the stored holiday list (exit ' + $r2.Code + ')') ($r2.Code -eq 0) ($r2.Out + $r2.Err)
            $lastBefore = (Get-TestTask $holName).LastRunTime
            $unk = Get-UnknownCount
            [void](Invoke-Cad @('--run', $holId))
            $w = Wait-Records $holId $holName 2 60 $lastBefore $unk
            if ($w -eq 'blocked') { Confirm-BlockedShown $holId 'holiday job (control run)'; return }
            Check 'control: a second record appeared' ($w -eq 'record') $w
            $row2 = @((Get-History $holId).recent)[0]
            Check 'control: without the date the same job runs (ok)' ($row2.status -eq 'ok') ($row2 | ConvertTo-Json -Compress)
            Check 'control: the program wrote its marker file' (Test-Path -LiteralPath $marker)
        }
    }

    # ---------------------------------------------------------------- 9
    Journey '9. Drift: an edit in Task Scheduler is detected; Re-apply fixes it' {
        $t = Get-TestTask $okName
        $origBoundary = [string]$t.Definition.Triggers.Item(1).StartBoundary
        Edit-TriggerHour $okName 1
        $nowBoundary = [string](Get-TestTask $okName).Definition.Triggers.Item(1).StartBoundary
        Check 'the trigger was changed through COM' ($nowBoundary -ne $origBoundary) ($origBoundary + ' -> ' + $nowBoundary)
        $j = Get-ProbeJob $okId
        Check '--probe: sync = drifted with a drift entry' ($j.sync -eq 'drifted' -and @($j.drift).Count -ge 1) ($j | ConvertTo-Json -Compress -Depth 5)
        Info ('drift: ' + ((@($j.drift) | ForEach-Object { $_.field + ': ' + $_.before + ' -> ' + $_.after }) -join '; '))
        $r = Invoke-Cad @('--reapply', $okId, '--yes')
        Check ('--reapply exit 0 (' + $r.Code + ')') ($r.Code -eq 0) ($r.Out + $r.Err)
        $j = Get-ProbeJob $okId
        Check '--probe: sync = inSync again' ($j.sync -eq 'inSync' -and @($j.drift).Count -eq 0) ($j | ConvertTo-Json -Compress -Depth 5)
        Check 'the trigger is back to the original' ([string](Get-TestTask $okName).Definition.Triggers.Item(1).StartBoundary -eq $origBoundary)
    }

    # ---------------------------------------------------------------- 10
    $untId = 'accuntr1'; $untName = $Prefix + ' untrack'
    Journey '10. Drift, then Untrack: the task stays and runs the program directly' {
        $f = New-JobJson $untId $untName '0 3 1 1 *' '2099-01-01' 'C:\Windows\System32\cmd.exe' '/d /c exit 0' @{} 'usFederal'
        $r = Apply-Job $f
        Check ('apply the untrack job (exit ' + $r.Code + ')') ($r.Code -eq 0) ($r.Out + $r.Err)
        Edit-TriggerHour $untName 1
        Check '--probe: drifted' ((Get-ProbeJob $untId).sync -eq 'drifted')
        $r = Invoke-Cad @('--untrack', $untId, '--yes')
        Check ('--untrack exit 0 (' + $r.Code + ')') ($r.Code -eq 0) ($r.Out + $r.Err)
        $t = Get-TestTask $untName
        Check 'the task still exists in Task Scheduler' ($null -ne $t)
        Check '--probe (default) no longer lists it as a job' ($null -eq (Get-ProbeJob $untId))
        $pa = Get-Probe -All
        Check '--probe --all lists it as foreign' (@($pa.foreign | Where-Object { $_.name -eq $untName }).Count -eq 1)
        $def = $t.Definition
        Check 'its action is cmd.exe directly (no conhost wrapper)' (([string]$def.Actions.Item(1).Path) -match 'cmd\.exe$' -and ([string]$def.Actions.Item(1).Arguments) -notmatch 'powershell') ([string]$def.Actions.Item(1).Path + ' ' + [string]$def.Actions.Item(1).Arguments)
        $doc = [string]$def.RegistrationInfo.Documentation
        $desc = [string]$def.RegistrationInfo.Description
        Check 'the Cadence marker is gone from Documentation' ($doc -notmatch 'cadence/')
        Check 'Description no longer says "Managed by Cadence - edit it in the Cadence app" nor mentions holidays' ($desc -notmatch 'Managed by Cadence - edit' -and $desc -notmatch '(?i)holiday') $desc
        $runBefore = [datetime]$t.LastRunTime
        $t.Run($null) | Out-Null
        $deadline = (Get-Date).AddSeconds(30)
        $ran = $false
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $t2 = Get-TestTask $untName
            if ([datetime]$t2.LastRunTime -gt $runBefore -and $t2.State -ne 4) { $ran = $true; break }
        }
        $t2 = Get-TestTask $untName
        Check 'Run() makes the untracked task run (LastRunTime advances)' $ran ([string]$t2.LastRunTime)
        Check 'LastTaskResult is the real exit code 0 (no wrapper any more)' ($t2.LastTaskResult -eq 0) ('0x{0:X8}' -f [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$t2.LastTaskResult), 0))
    }

    # ---------------------------------------------------------------- 11 (UI: jobs exist)
    if (-not $SkipUi) {
        Journey '11. UI: cron toggle on a saved job, Run now shows in the editor''s run list, a failure shows in the editor' {
            Load-Ui
            $win = Ui-Start
            try {
                Check 'the list shows the acceptance jobs' ($null -ne (Ui-Find $win $okName 15))
                Check 'the foreign task is still hidden' ($null -eq (Ui-Find $win $foreignName 1))
                Check 'the untracked task is hidden (it is foreign now)' ($null -eq (Ui-Find $win $untName 1))
                Ui-Press (Ui-Need $win $okName 5)
                Check 'editor opened (Run now is there)' ($null -ne (Ui-Find $win 'Run now' 10))
                Ui-Select (Ui-Need $win 'Cron view' 5)
                $box = Ui-Need $win 'Cron schedule' 5
                Check 'Cron view shows */15 * * * *' ((Ui-Value $box) -eq '*/15 * * * *') (Ui-Value $box)
                Ui-Select (Ui-Need $win 'Simple view' 5)
                Check 'Simple view shows the same schedule (Interval box present)' ($null -ne (Ui-Find $win 'Interval' 5))
                $runsBefore = Get-RecordCount $okId
                $unk = Get-UnknownCount
                $lastBefore = (Get-TestTask $okName).LastRunTime
                Ui-Press (Ui-Need $win 'Run now' 5)
                $w = Wait-Records $okId $okName ($runsBefore + 1) 60 $lastBefore $unk
                if ($w -eq 'blocked') { Known 'UI Run now: Windows refused to start the job (0x80070005, see the README "Safety notes"); the editor shows the could-not-start notice instead of a run row.'; Check 'the editor says Windows refused to start the job' ($null -ne (Ui-Find $win 'Windows refused to start this job' 5)) }
                else {
                    Check 'a new run record exists after Run now' ($w -eq 'record') $w
                    $fb = Ui-FindId $win 'RunFeedback' 20
                    $end = (Get-Date).AddSeconds(20)
                    while ($fb -and $fb.Current.Name -notlike 'Finished*' -and (Get-Date) -lt $end) { Start-Sleep -Milliseconds 500; $fb = Ui-FindId $win 'RunFeedback' 2 }
                    Check 'the editor reports "Finished: Ok"' ($fb -and $fb.Current.Name -like 'Finished: Ok*') ($(if ($fb) { $fb.Current.Name } else { 'no feedback line' }))
                    Check 'the run appears in the editor''s run list (row "Ok")' ($null -ne (Ui-Find $win 'Ok' 10))
                }
                Ui-BackOut $win
                # a failure shows in the editor
                Ui-Press (Ui-Need $win $failName 10)
                $failRow = $null
                $end = (Get-Date).AddSeconds(10)
                while (-not $failRow -and (Get-Date) -lt $end) {
                    foreach ($e in $win.FindAll($script:TS::Descendants, (New-Object $script:PC($script:AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
                        if ($e.Current.Name -like 'Failed*') { $failRow = $e; break }
                    }
                    if (-not $failRow) { Start-Sleep -Milliseconds 300 }
                }
                Check 'the failing job''s editor lists the failed run' ($null -ne $failRow)
                Ui-BackOut $win
            } finally { Ui-Close }
        }
    }

    # ---------------------------------------------------------------- 12
    if (-not $SkipClosedApp) {
        Journey '12. Closing the app does not stop jobs: a scheduled run fires while no Cadence.exe exists' {
            $cloId = 'accclo01'; $cloName = $Prefix + ' app closed'
            $now = Get-Date
            $target = $now.AddSeconds(120)
            $target = $target.Date.AddHours($target.Hour).AddMinutes($target.Minute)   # a whole minute 61-120 s ahead
            # cron-pinned to one date: "m h d M *", so a leftover cannot fire again for a year
            $cron = '{0} {1} {2} {3} *' -f $target.Minute, $target.Hour, $target.Day, $target.Month
            $f = New-JobJson $cloId $cloName $cron $target.ToString('yyyy-MM-dd') 'C:\Windows\System32\cmd.exe' '/d /c "echo app-closed"'
            $applied = $false
            try {
                $r = Apply-Job $f
                $applied = ($r.Code -eq 0)
                Check ('apply the near-future job (cron "' + $cron + '", exit ' + $r.Code + ')') $applied ($r.Out + $r.Err)
                $j = Get-ProbeJob $cloId
                Check 'Task Scheduler will start it at the pinned minute' ($j -and ([math]::Abs(([datetime]$j.taskScheduler.nextRunTime - $target).TotalSeconds) -lt 1)) ([string]$j.taskScheduler.nextRunTime)
                Check 'no Cadence.exe process exists now' (-not (Get-Process -Name Cadence -ErrorAction SilentlyContinue))
                $sawApp = $false
                $unk = Get-UnknownCount
                $lastBefore = (Get-TestTask $cloName).LastRunTime
                $deadline = $target.AddSeconds(100)
                $w = 'timeout'
                Say ('      waiting for the trigger at ' + $target.ToString('HH:mm:ss') + ' (no Cadence.exe is started by this script meanwhile)')
                while ((Get-Date) -lt $deadline) {
                    if (Get-Process -Name Cadence -ErrorAction SilentlyContinue) { $sawApp = $true }
                    if ((Get-RecordCount $cloId) -ge 1) { $w = 'record'; break }
                    $t = Get-TestTask $cloName
                    if ($t -and $t.State -ne 4 -and $t.LastTaskResult -eq $Blocked -and $t.LastRunTime -ne $lastBefore) {
                        Start-Sleep -Seconds 3
                        if ((Get-RecordCount $cloId) -lt 1 -and (Get-UnknownCount) -eq $unk -and (Get-WrapperProcs $cloName).Count -eq 0) { $w = 'blocked'; break }
                    }
                    Start-Sleep -Seconds 1
                }
                Check 'no Cadence.exe process existed while the job fired' (-not $sawApp)
                if ($w -eq 'blocked') { Confirm-BlockedShown $cloId 'app-closed job' }
                else {
                    Check 'the scheduled run left a record without the app' ($w -eq 'record') $w
                    $h = Get-History $cloId
                    $row = @($h.recent)[0]
                    Check '--history: ok, exit 0' ($row.status -eq 'ok' -and $row.exitCode -eq 0) ($row | ConvertTo-Json -Compress)
                    $when = [datetime]$row.when
                    Check 'it started at the scheduled minute (-5 s .. +75 s)' ((($when - $target).TotalSeconds -ge -5) -and (($when - $target).TotalSeconds -le 75)) ($when.ToString('HH:mm:ss') + ' vs ' + $target.ToString('HH:mm:ss'))
                }
            } finally {
                # delete right away, whatever happened (it is cron-pinned, but a leftover must not stay)
                if ($applied -or (Get-TestTask $cloName)) { try { [void](Invoke-Cad @('--delete', $cloId, '--yes')) } catch { }; if (Get-TestTask $cloName) { Remove-OurTask $cloName } }
            }
            Check 'the near-future task is deleted' ($null -eq (Get-TestTask $cloName))
        }
    }

    # ---------------------------------------------------------------- 13
    Journey '13. Delete removes only that task and its history' {
        $ids = [ordered]@{ $okId = $okName; $failId = $failName; $toId = $toName; $holId = $holName }
        foreach ($id in $ids.Keys) {
            $name = $ids[$id]
            if (-not (Get-TestTask $name)) { Info ("$name does not exist (an earlier step failed); skipped"); continue }
            $namesBefore = Get-TestTaskNames
            $r = Invoke-Cad @('--delete', $id, '--yes')
            $namesAfter = Get-TestTaskNames
            $gone = @($namesBefore | Where-Object { $namesAfter -notcontains $_ })
            Check ("--delete $id removes exactly '$name' (exit $($r.Code))") ($r.Code -eq 0 -and $gone.Count -eq 1 -and $gone[0] -eq $name -and $namesAfter.Count -eq $namesBefore.Count - 1) ('gone: ' + ($gone -join ', '))
            Check ("run history folder of $id is gone") (-not (Test-Path -LiteralPath (Join-Path $LocalData ('runs\' + $id))))
        }
        # the untracked task is a plain task now (foreign to Cadence), so the CLI cannot delete it; the script made it, so it removes it
        if (Get-TestTask $untName) { Remove-OurTask $untName }
        Check 'the untracked (plain) task was removed by the script' ($null -eq (Get-TestTask $untName))
        Check 'only the foreign task is left in \CadenceTest' (((Get-TestTaskNames) -join '|') -eq $foreignName) ((Get-TestTaskNames) -join '|')
    }

    # ---------------------------------------------------------------- 14
    Journey '14. Foreign task untouched' {
        $t = Get-TestTask $foreignName
        Check 'the foreign task still exists' ($null -ne $t)
        Check 'its XML hash is identical to before everything' ($t -and (Get-Sha ([string]$t.Xml)) -eq $foreignHash) ($(if ($t) { Get-Sha ([string]$t.Xml) } else { 'missing' }) + ' vs ' + $foreignHash)
        $pa = Get-Probe -All
        Check '--probe --all still lists it under foreign' (@($pa.foreign | Where-Object { $_.name -eq $foreignName }).Count -eq 1)
    }

    # ================================================================= AGENT JOURNEYS A1-A13
    # Through tools\McpClient.psm1 against bin\Cadence.exe --mcp --folder CadenceTest. Names "Cadence Acceptance Agent*".
    # A FAIL stops the rest of the agent journeys (they depend on each other); cleanup runs in the finally block below.
    $script:AgentFailBase = $script:Fail
    $script:AgentStopped = $false
    $script:Sess = @{}
    $script:StopResults = New-Object System.Collections.Generic.List[object]
    $agentTitle = 'Cadence Acceptance Client'
    $agentSecret = 'acc-secret-' + [guid]::NewGuid().ToString('N')   # an environment value that must never appear in any log or reply
    $agentCmd = 'C:\Windows\System32\cmd.exe'
    $agentOkName = $Prefix + ' Agent OK'
    $agentForeignName = $Prefix + ' Agent Foreign'
    $script:AgentJob = $null          # jobId of "Cadence Acceptance Agent OK"
    $script:AgentAllText = New-Object System.Collections.Generic.List[string]
    $agentTools = @('cadence_get_status', 'cadence_list_jobs', 'cadence_get_job', 'cadence_get_run_output', 'cadence_preview_schedule', 'cadence_list_templates',
                    'cadence_save_job', 'cadence_set_job_enabled', 'cadence_run_job_now', 'cadence_delete_job', 'cadence_suspend_agents', 'cadence_resume_agents')

    function AJourney([string]$title, [scriptblock]$body) {
        if ($script:Fail -gt $script:AgentFailBase) {
            if (-not $script:AgentStopped) { $script:AgentStopped = $true }
            Say ''; Say ('== ' + $title + '  (skipped: an earlier agent journey failed)'); return
        }
        Journey $title $body
    }
    function Tool($s, [string]$name, $toolArgs = @{}, [switch]$AllowError, [int]$TimeoutSec = 25) {
        $r = Invoke-CadenceTool -Session $s -Name $name -Arguments $toolArgs -AllowError:$AllowError -TimeoutSec $TimeoutSec
        $script:AgentAllText.Add((ConvertTo-Json -InputObject $r -Compress -Depth 20))
        $r
    }
    function ErrCode($r) { if ($r -and ($r.PSObject.Properties.Name -contains 'error') -and $r.error) { [string]$r.error.code } else { '' } }
    function ErrMsg($r) { if ($r -and ($r.PSObject.Properties.Name -contains 'error') -and $r.error) { [string]$r.error.message } else { '' } }
    function Json($o) { ConvertTo-Json -InputObject $o -Compress -Depth 8 }
    function Get-CtHashes { $acc = @{}; if (Test-FolderExists $FolderPath) { foreach ($t in @((Get-TestFolder).GetTasks(1))) { $acc[$t.Name] = Get-Sha ([string]$t.Xml) } }; $acc }
    function Same-Hashes($a, $b) { if ($a.Count -ne $b.Count) { return $false }; foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k) -or $a[$k] -ne $b[$k]) { return $false } }; $true }
    function Open-Agent([string]$title = $agentTitle, [string]$proto = '2025-11-25', [string]$clientName = 'cadence-acceptance') {
        Start-CadenceMcp -Exe $Exe -Folder $FolderName -ClientName $clientName -ClientTitle $title -ProtocolVersion $proto
    }
    function Close-Agent($s, [string]$label) {
        $x = Stop-CadenceMcp -Session $s
        $script:StopResults.Add([pscustomobject]@{ Label = $label; Exited = $x.Exited; Code = $x.ExitCode; Killed = $x.Killed; Gone = $x.Gone; Stderr = $x.Stderr })
        $script:AgentAllText.Add($x.Stderr)
        $x
    }
    function Get-AgentDay { (Get-Date).AddDays(-1) }
    function Pinned-Cron([int]$minute, [int]$hour) { $d = Get-AgentDay; '{0} {1} {2} {3} *' -f $minute, $hour, $d.Day, $d.Month }
    function Read-AuditLines {
        $l = New-Object System.Collections.Generic.List[string]
        foreach ($n in 'audit.1.jsonl', 'audit.jsonl', 'audit-owner.jsonl') {
            $f = Join-Path $LocalData ('agents\' + $n)
            if (Test-Path -LiteralPath $f) {
                try { $fs = [IO.File]::Open($f, 'Open', 'Read', 'ReadWrite,Delete'); $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8); $t = $sr.ReadToEnd(); $sr.Close(); foreach ($x in ($t -split "`n")) { if ($x.Trim().Length -gt 0) { $l.Add($x.TrimEnd("`r")) } } } catch { }
            }
        }
        $l.ToArray()
    }
    function Parse-Audit([string]$line) { try { $line | ConvertFrom-Json } catch { $null } }

    $agentBefore = Get-CtHashes
    $S1 = $null

    AJourney 'A1. Connect: protocol versions, 12 tools with annotations, no window' {
        $script:S1 = Open-Agent
        $s = $script:S1
        Check 'initialize with 2025-11-25 is echoed' ($s.InitializeResult.protocolVersion -eq '2025-11-25') (Json $s.InitializeResult)
        Check 'serverInfo name is cadence and instructions are present' ($s.InitializeResult.serverInfo.name -eq 'cadence' -and ([string]$s.InitializeResult.instructions).Length -gt 100)
        $s2 = Open-Agent 'Cadence Acceptance Client (old protocol)' '2025-06-18' 'cadence-acceptance-old'
        Check 'a second session with 2025-06-18 gets 2025-06-18 back' ($s2.InitializeResult.protocolVersion -eq '2025-06-18') (Json $s2.InitializeResult)
        $tl = Invoke-CadenceMcp -Session $s -Method 'tools/list'
        $tools = @($tl.result.tools)
        $names = @($tools | ForEach-Object { $_.name })
        Check 'tools/list offers exactly the 12 tools' ($names.Count -eq 12 -and @($agentTools | Where-Object { $names -notcontains $_ }).Count -eq 0) ($names -join ',')
        $ro = @($tools | Where-Object { $_.annotations.readOnlyHint -eq $true } | ForEach-Object { $_.name })
        Check 'six tools are read-only, the other six are not' ($ro.Count -eq 6 -and @($tools | Where-Object { $_.annotations.readOnlyHint -eq $false }).Count -eq 6) ($ro -join ',')
        $allAnn = @($tools | Where-Object { $null -eq $_.annotations -or $null -eq $_.annotations.destructiveHint -or $null -eq $_.annotations.idempotentHint -or $null -eq $_.annotations.openWorldHint -or $_.annotations.openWorldHint -ne $false }).Count
        Check 'every tool carries the full annotation set, none is open-world' ($allAnn -eq 0)
        Check 'the two switch tools are separate (suspend and resume)' (($names -contains 'cadence_suspend_agents') -and ($names -contains 'cadence_resume_agents'))
        $dest = @($tools | Where-Object { $_.name -in 'cadence_save_job', 'cadence_run_job_now', 'cadence_delete_job' } | Where-Object { $_.annotations.destructiveHint -eq $true }).Count
        Check 'save, run-now and delete are flagged destructive' ($dest -eq 3)
        Check 'the server has no window (MainWindowHandle 0)' ([int64]$s.Process.MainWindowHandle -eq 0 -and [int64]$s2.Process.MainWindowHandle -eq 0)
        $x = Close-Agent $s2 'A1 second session'
        Check 'the second session ends with exit 0 when stdin closes' ($x.Exited -and $x.ExitCode -eq 0 -and -not $x.Killed) (Json $x)
        Check 'stderr has the startup line (version, folder, pid, protocols)' ($x.Stderr -match 'cadence-mcp: v\S* folder \\CadenceTest pid \d+ protocol') $x.Stderr
        Check 'stderr names the client' ($x.Stderr -match 'cadence-mcp: client Cadence Acceptance Client \(old protocol\)') $x.Stderr
    }

    AJourney 'A2. Preview a schedule (read-only)' {
        $s = $script:S1
        $h0 = Get-CtHashes
        $p = Tool $s 'cadence_preview_schedule' @{ cron = @('0 18 * * 1-5'); skipHolidays = 'nyse' }
        Check 'weekday 18:00 + NYSE: plain sentence' ($p.valid -eq $true -and $p.description -eq 'Every weekday at 18:00, except NYSE market holidays') ([string]$p.description)
        Check 'it shows 8 next runs' (@($p.nextRuns).Count -eq 8) ([string]@($p.nextRuns).Count)
        Check 'agents may save it' ($p.agentsMaySave -eq $true)
        $p = Tool $s 'cadence_preview_schedule' @{ cron = @('*/90 * * * *') }
        Check '*/90 * * * * is not valid, with an explanation' ($p.valid -eq $false -and @($p.diagnostics).Count -ge 1 -and ([string]$p.diagnostics[0].message) -match '90') (Json $p.diagnostics)
        $p = Tool $s 'cadence_preview_schedule' @{ cron = @('* * * * *') }
        Check '* * * * * is valid but agentsMaySave is false (shortest allowed gap is 5 minutes)' ($p.valid -eq $true -and $p.agentsMaySave -eq $false -and ([string]$p.agentsMaySaveReason) -match '5 minutes') ([string]$p.agentsMaySaveReason)
        $t = Tool $s 'cadence_list_templates' @{}
        Check 'cadence_list_templates answers' ($null -ne $t)
        Check 'the folder is unchanged by previews (task hashes)' (Same-Hashes $h0 (Get-CtHashes))
    }

    AJourney 'A3. An agent creates a job from a plain-English request (preview, then save)' {
        $s = $script:S1
        $cron = Pinned-Cron 30 7
        $pv = Tool $s 'cadence_preview_schedule' @{ cron = @($cron) }
        Check 'the agent previews the schedule first (valid, says when)' ($pv.valid -eq $true -and $pv.agentsMaySave -eq $true -and [string]$pv.description -ne '') ([string]$pv.description)
        $script:AgentArgs = @{ name = $agentOkName; schedule = @{ cron = @($cron) }; program = @{ path = $agentCmd; arguments = '/d /c "echo acceptance-agent-ok"'; environment = @{ CADENCE_ACC_ENV = $agentSecret } } }
        $r = Tool $s 'cadence_save_job' $script:AgentArgs
        Check 'cadence_save_job outcome is "created"' ($r.outcome -eq 'created') (Json $r)
        $script:AgentJob = [string]$r.job.jobId
        $script:AgentRev = [string]$r.job.revision
        Check 'the answer has a job id and a revision' ($script:AgentJob -match '^[a-z0-9]{8}$' -and $script:AgentRev -match '^[0-9a-f]{12}$')
        $t = Get-TestTask $agentOkName
        Check 'the task exists in \CadenceTest' ($null -ne $t)
        $def = $t.Definition
        Check 'Source = Cadence' ([string]$def.RegistrationInfo.Source -eq 'Cadence')
        $doc = [string]$def.RegistrationInfo.Documentation
        Check 'Documentation starts with cadence/1' ($doc.StartsWith('cadence/1'))
        Check 'metadata createdBy and updatedBy are agent:Cadence Acceptance Client' ($doc -match '"createdBy":"agent:Cadence Acceptance Client"' -and $doc -match '"updatedBy":"agent:Cadence Acceptance Client"') ($doc.Substring(0, [Math]::Min(400, $doc.Length)))
        Check 'the task is disabled-by-date: its next run is a year away (pinned to yesterday)' ($t.NextRunTime.Year -ge 2027) ([string]$t.NextRunTime)
        $j = Get-ProbeJob $script:AgentJob
        Check '--probe: managed, inSync' ($j -and $j.ownership -eq 'managed' -and $j.sync -eq 'inSync') (Json $j)
        Check '--probe shows updatedBy = agent:Cadence Acceptance Client' ([string]$j.updatedBy -eq 'agent:Cadence Acceptance Client') (Json $j)
        $g = Tool $s 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 3 }
        Check 'cadence_get_job reads back "updated by" as an agent' ($g.updated.byKind -eq 'agent' -and $g.updated.by -eq $agentTitle -and $g.created.byKind -eq 'agent') (Json $g.updated)
        Check 'environment values are never shown (names only)' ((Json $g) -notmatch [regex]::Escape($agentSecret) -and @($g.program.environmentNames) -contains 'CADENCE_ACC_ENV')
        $script:AgentRev = [string]$g.revision
    }

    AJourney 'A4. Retries are safe (idempotent); a name clash is explained' {
        $s = $script:S1
        $r = Tool $s 'cadence_save_job' $script:AgentArgs
        Check 'the same save again is "unchanged"' ($r.outcome -eq 'unchanged') (Json $r)
        Check 'the revision did not change' ([string]$r.job.revision -eq $script:AgentRev) ([string]$r.job.revision + ' vs ' + $script:AgentRev)
        Check 'still one job in the folder for this name' (@((Tool $s 'cadence_list_jobs' @{ nameContains = $agentOkName }).jobs).Count -eq 1)
        $other = @{ name = $agentOkName; schedule = $script:AgentArgs.schedule; program = @{ path = $agentCmd; arguments = '/d /c exit 1' } }
        $r = Tool $s 'cadence_save_job' $other -AllowError
        Check 'same name, different program: name_taken naming the job id' ((ErrCode $r) -eq 'name_taken' -and (ErrMsg $r) -match $script:AgentJob) (Json $r)
        Check 'nothing changed by the clash' ([string](Tool $s 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 0 }).revision -eq $script:AgentRev)
    }

    AJourney 'A5. Edit it with the right revision; an old revision is refused' {
        $s = $script:S1
        $g = Tool $s 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 0 }
        $old = [string]$g.revision
        $r = Tool $s 'cadence_save_job' @{ jobId = $script:AgentJob; expectedRevision = $old; schedule = @{ cron = @((Pinned-Cron 45 9)) } }
        Check 'save with the current revision: "updated"' ($r.outcome -eq 'updated') (Json $r)
        Check '"changes" mentions the new time' (([string]$r.changes) -match '9:45|09:45') ([string]$r.changes)
        Check 'the revision moved on' ([string]$r.job.revision -ne $old)
        $r2 = Tool $s 'cadence_save_job' @{ jobId = $script:AgentJob; expectedRevision = $old; schedule = @{ cron = @((Pinned-Cron 15 11)) } } -AllowError
        Check 'save with the OLD revision: stale_revision' ((ErrCode $r2) -eq 'stale_revision') (Json $r2)
        $j = Get-ProbeJob $script:AgentJob
        Check '--probe: the task holds 9:45 and is inSync' ($j.sync -eq 'inSync' -and @($j.cron)[0] -match '^45 9 ') (Json $j)
        $script:AgentRev = [string]$r.job.revision
    }

    AJourney 'A6. Run it now and read its history (a real, harmless run)' {
        $s = $script:S1
        $t = Get-TestTask $agentOkName
        $lastBefore = $t.LastRunTime
        $unk = Get-UnknownCount
        $r = Tool $s 'cadence_run_job_now' @{ jobId = $script:AgentJob }
        Check 'cadence_run_job_now: started' ($r.started -eq $true) (Json $r)
        $w = Wait-Records $script:AgentJob $agentOkName 1 30 $lastBefore $unk
        if ($w -eq 'blocked') {
            Known 'agent run now: Task Scheduler refused to start the job (0x80070005) and no wrapper ran. The code does not say why; see the README "Safety notes". Nothing was renamed, retried or excluded.'
            $g = Tool $s 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 3 }
            $row = @($g.recentRuns)[0]
            Check 'cadence_get_job shows the blocked launch as a run that did not report, with the blocked message' ($row -and ([string]$row.status -match '(?i)notReported|didNotReport') -and ([string]$row.message -match '(?i)blocked')) (Json $row)
            return
        }
        Check 'a run record appeared within 30 s' ($w -eq 'record') $w
        $g = Tool $s 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 3 }
        $row = @($g.recentRuns)[0]
        Check 'cadence_get_job: the newest run is ok, exit 0' ($row.status -eq 'ok' -and $row.exitCode -eq 0) (Json $row)
        Check 'lastResult and stats reflect the run' ($g.lastResult.status -eq 'ok' -and $g.stats.sampleSize -ge 1) (Json $g.lastResult)
        $o = Tool $s 'cadence_get_run_output' @{ jobId = $script:AgentJob; runId = [string]$row.runId }
        Check 'cadence_get_run_output contains acceptance-agent-ok' ((Json $o) -match 'acceptance-agent-ok') (Json $o)
        $script:RunRecordedId = [string]$row.runId
    }

    AJourney 'A6b. Enable and disable' {
        $s = $script:S1
        # no revision given and nobody racing: it just works (the revision is optional here)
        $r = Tool $s 'cadence_set_job_enabled' @{ jobId = $script:AgentJob; enabled = $false }
        Check 'disable without a revision: "updated"' (([string]$r.outcome) -eq 'updated' -and $r.job.enabled -eq $false) (Json $r)
        Check 'Task Scheduler agrees: the task is disabled' ((Get-TestTask $agentOkName).Enabled -eq $false)
        $r = Tool $s 'cadence_set_job_enabled' @{ jobId = $script:AgentJob; enabled = $false }
        Check 'disabling again is harmless (unchanged)' (([string]$r.outcome) -eq 'unchanged') (Json $r)
        $g = Tool $s 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 0 }
        $r = Tool $s 'cadence_set_job_enabled' @{ jobId = $script:AgentJob; enabled = $true; expectedRevision = [string]$g.revision }
        Check 'enable with the revision: "updated"' (([string]$r.outcome) -eq 'updated' -and $r.job.enabled -eq $true) (Json $r)
        Check 'Task Scheduler agrees: the task is enabled' ((Get-TestTask $agentOkName).Enabled -eq $true)
        $script:AgentRev = [string]$r.job.revision
    }

    AJourney 'A7. Suspension blocks writes at once; the agent resumes through its own tool' {
        $s = $script:S1
        $g0 = Tool $s 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 0 }
        $rev0 = [string]$g0.revision
        $c = Invoke-Cad @('--agents', 'suspend')
        Check ('--agents suspend (exit ' + $c.Code + ')') ($c.Code -eq 0) ($c.Out + $c.Err)
        $r = Tool $s 'cadence_save_job' @{ jobId = $script:AgentJob; expectedRevision = $rev0; schedule = @{ cron = @((Pinned-Cron 20 10)) } } -AllowError
        Check 'the very next save in the same session is refused: suspended' ((ErrCode $r) -eq 'suspended') (Json $r)
        $r = Tool $s 'cadence_run_job_now' @{ jobId = $script:AgentJob } -AllowError
        Check 'run-now is refused too' ((ErrCode $r) -eq 'suspended') (Json $r)
        $g1 = Tool $s 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 0 }
        Check 'reading still works and nothing changed (revision equal)' ([string]$g1.revision -eq $rev0)
        Check 'cadence_get_status says suspended' ((Tool $s 'cadence_get_status' @{}).agents.suspended -eq $true)
        $c = Invoke-Cad @('--agents', 'status', '--json')
        Check '--agents status --json: suspended, by cli' ($c.Code -eq 0 -and $c.Out -match '"suspended":\s*true') ($c.Out + $c.Err)
        # the agent turns itself back on through its own tool, with a reason
        $r = Tool $s 'cadence_resume_agents' @{ reason = 'acceptance' }
        Check 'cadence_resume_agents: suspended is false' ($r.suspended -eq $false -and $r.changed -eq $true) (Json $r)
        Check 'the change is attributed to agent:Cadence Acceptance Client with its reason' ($r.changedBy -eq 'agent:Cadence Acceptance Client' -and $r.reason -eq 'acceptance') (Json $r)
        $c = Invoke-Cad @('--agents', 'status', '--json')
        Check '--agents status --json shows agents allowed and who resumed them' ($c.Code -eq 0 -and $c.Out -match '"suspended":\s*false' -and $c.Out -match 'agent:Cadence Acceptance Client') ($c.Out + $c.Err)
        $aud = @(Read-AuditLines | Where-Object { $_ -match 'resumed' -and $_ -match 'acceptance' })
        Check 'the audit log has a "resumed" line with the reason' ($aud.Count -ge 1) 'no line'
        $r = Tool $s 'cadence_save_job' @{ jobId = $script:AgentJob; expectedRevision = $rev0; schedule = @{ cron = @((Pinned-Cron 20 10)) } }
        Check 'after the agent resumed itself the save works' ($r.outcome -eq 'updated') (Json $r)
        $script:AgentRev = [string]$r.job.revision
        # the agent suspends itself, the owner resumes from the CLI (no --yes needed)
        $r = Tool $s 'cadence_suspend_agents' @{ reason = 'acceptance self-suspend' }
        Check 'cadence_suspend_agents: suspended' ($r.suspended -eq $true -and $r.changedBy -eq 'agent:Cadence Acceptance Client') (Json $r)
        $r = Tool $s 'cadence_set_job_enabled' @{ jobId = $script:AgentJob; enabled = $false } -AllowError
        Check 'a change is refused while suspended' ((ErrCode $r) -eq 'suspended') (Json $r)
        $c = Invoke-Cad @('--agents', 'resume')
        Check ('owner: --agents resume (no --yes) works (exit ' + $c.Code + ')') ($c.Code -eq 0) ($c.Out + $c.Err)
        $g = Tool $s 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 0 }
        $r = Tool $s 'cadence_save_job' @{ jobId = $script:AgentJob; expectedRevision = [string]$g.revision; schedule = @{ cron = @((Pinned-Cron 25 10)) } }
        Check 'writes work again after the owner resumed' ($r.outcome -eq 'updated') (Json $r)
        $script:AgentRev = [string]$r.job.revision
    }

    AJourney 'A8. "Updated ... by" shows in the probe; a CLI change takes over updatedBy' {
        $j = Get-ProbeJob $script:AgentJob
        Check '--probe: updatedBy = agent:Cadence Acceptance Client, createdBy the same' ([string]$j.updatedBy -eq 'agent:Cadence Acceptance Client' -and [string]$j.createdBy -eq 'agent:Cadence Acceptance Client') (Json $j)
        $f = New-JobJson $script:AgentJob $agentOkName (Pinned-Cron 50 10) '2099-01-01' $agentCmd '/d /c "echo acceptance-agent-ok"'
        $r = Apply-Job $f
        Check ('CLI --apply of a change (exit ' + $r.Code + ')') ($r.Code -eq 0) ($r.Out + $r.Err)
        $j = Get-ProbeJob $script:AgentJob
        Check '--probe: updatedBy is now "cli", createdBy is still the agent' ([string]$j.updatedBy -eq 'cli' -and [string]$j.createdBy -eq 'agent:Cadence Acceptance Client') (Json $j)
        $g = Tool $script:S1 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 0 }
        Check 'cadence_get_job agrees (updated by the owner, created by an agent)' ($g.updated.byKind -ne 'agent' -and $g.created.byKind -eq 'agent') (Json $g.updated)
        $script:AgentRev = [string]$g.revision
        foreach ($state in 'list-updated-by', 'editor-agent-lock') {
            $png = Join-Path $Work ($state + '.png')
            if (-not (Test-Path -LiteralPath $RenderExe)) { Info ("Cadence.Render.exe not built (dotnet build Cadence.slnx -c Release): the $state render is skipped"); continue }
            $c = Invoke-Cad @($state, '--out', $png) -NoFolder -ExePath $RenderExe
            $size = 0; if (Test-Path -LiteralPath $png) { $size = (Get-Item -LiteralPath $png).Length }
            Check ("Cadence.Render $state wrote a PNG over 20 KB for the human check ($size bytes, exit $($c.Code))") ($c.Code -eq 0 -and $size -gt 20480) ($c.Out + $c.Err)
        }
    }

    AJourney 'A9. A foreign task is invisible and untouchable' {
        $s = $script:S1
        $xml = '<?xml version="1.0" encoding="UTF-16"?><Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><RegistrationInfo><Description>Acceptance test: a task Cadence did not make (no marker).</Description></RegistrationInfo><Triggers><TimeTrigger><StartBoundary>2099-01-01T00:00:00</StartBoundary><Enabled>false</Enabled></TimeTrigger></Triggers><Principals><Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals><Settings><Enabled>false</Enabled></Settings><Actions Context="Author"><Exec><Command>cmd.exe</Command><Arguments>/d /c exit 0</Arguments></Exec></Actions></Task>'
        [void](Get-TestFolder).RegisterTask($agentForeignName, $xml, 6, $null, $null, 3)
        $script:AgentForeignHash = Get-Sha ([string](Get-TestTask $agentForeignName).Xml)
        $l = Tool $s 'cadence_list_jobs' @{}
        Check 'the foreign task is absent from cadence_list_jobs' (@($l.jobs | Where-Object { $_.name -eq $agentForeignName }).Count -eq 0 -and @($l.jobs).Count -ge 1)
        $r = Tool $s 'cadence_get_job' @{ jobId = $agentForeignName } -AllowError
        Check 'its name is not an id the tools accept' ((ErrCode $r) -ne '')
        $r = Tool $s 'cadence_save_job' @{ name = $agentForeignName; schedule = @{ cron = @((Pinned-Cron 5 6)) }; program = @{ path = $agentCmd; arguments = '/d /c exit 0' }; enabled = $false }
        Check 'a save named exactly like it creates the agent''s own task "... Foreign (2)" (the display name stays as asked)' ($r.outcome -eq 'created' -and ([string]$r.job.taskPath).EndsWith($agentForeignName + ' (2)') -and [string]$r.job.name -eq $agentForeignName) (Json $r)
        $twoId = [string]$r.job.jobId
        Check 'the foreign task XML hash is unchanged after the save' ((Get-Sha ([string](Get-TestTask $agentForeignName).Xml)) -eq $script:AgentForeignHash)
        if ($twoId) {
            $d = Tool $s 'cadence_delete_job' @{ jobId = $twoId; confirmName = $agentForeignName }
            Check 'deleting the agent''s own "(2)" job leaves the foreign task alone' ($d.deleted -eq $true -and $null -ne (Get-TestTask $agentForeignName) -and (Get-Sha ([string](Get-TestTask $agentForeignName).Xml)) -eq $script:AgentForeignHash)
        }
    }

    AJourney 'A10. Malicious and oversized input is refused; nothing is written' {
        $s = $script:S1
        $h0 = Get-CtHashes
        $bat = Join-Path $Work 'agent-job.bat'
        [IO.File]::WriteAllText($bat, "@echo off`r`nexit /b 0`r`n")
        $mk = { param($n, $path, $a) @{ name = ($Prefix + ' Agent Bad ' + $n); schedule = @{ cron = @((Pinned-Cron 5 6)) }; program = @{ path = $path; arguments = $a }; enabled = $false } }
        $cases = @(
            @{ L = 'UNC program path'; A = (& $mk 1 '\\localhost\C$\Windows\System32\cmd.exe' '/d /c exit 0') },
            @{ L = 'bare python.exe (no PATH in Task Scheduler)'; A = (& $mk 2 'python.exe' '-V') },
            @{ L = 'Cadence.exe itself as the program'; A = (& $mk 3 $Exe '--agents resume') },
            @{ L = 'a .bat path as the program'; A = (& $mk 4 $bat '') },
            @{ L = 'arguments with a newline'; A = (& $mk 5 $agentCmd ("/d /c exit 0`r`nexit 1")) },
            @{ L = 'agentLock:false at the top level'; A = @{ name = ($Prefix + ' Agent Bad 6'); schedule = @{ cron = @((Pinned-Cron 5 6)) }; program = @{ path = $agentCmd; arguments = '/d /c exit 0' }; agentLock = $false } },
            @{ L = 'every minute (too frequent)'; A = @{ name = ($Prefix + ' Agent Bad 7'); schedule = @{ cron = @('* * * * *') }; program = @{ path = $agentCmd; arguments = '/d /c exit 0' } } },
            @{ L = 'every minute on 20 December (December density case)'; A = @{ name = ($Prefix + ' Agent Bad 8'); schedule = @{ cron = @('* * 20 12 *') }; program = @{ path = $agentCmd; arguments = '/d /c exit 0' } } }
        )
        foreach ($c in $cases) {
            $r = Tool $s 'cadence_save_job' $c.A -AllowError
            Check ('refused: ' + $c.L + ' -> ' + (ErrCode $r)) ((ErrCode $r) -ne '') (Json $r)
            if ($c.L -match 'December') { Check 'the December case is refused with schedule_too_frequent' ((ErrCode $r) -eq 'schedule_too_frequent') (Json $r) }
            if ($c.L -match 'every minute \(') { Check 'every minute is refused with schedule_too_frequent' ((ErrCode $r) -eq 'schedule_too_frequent') (Json $r) }
        }
        Check 'nothing was written by any of the refused calls (task hashes identical)' (Same-Hashes $h0 (Get-CtHashes))
        # a 2 MiB line: -32600, and the session keeps working
        $big = '{"jsonrpc":"2.0","id":9001,"method":"tools/call","params":{"name":"cadence_list_jobs","arguments":{"nameContains":"' + ('a' * 2097152) + '"}}}'
        Send-CadenceMcpRaw -Session $s -Text $big
        $line = Read-CadenceMcpLine -Session $s -TimeoutSec 20
        $o = $null; try { $o = $line | ConvertFrom-Json } catch { }
        Check 'a 2 MiB line is refused with JSON-RPC -32600' ($o -and $o.error -and $o.error.code -eq -32600) ([string]$line)
        $p = Invoke-CadenceMcp -Session $s -Method 'ping'
        Check 'and ping still works afterwards' ($p -and $p.PSObject.Properties.Name -contains 'result')
        # an agent's enable/disable without a revision races nothing and goes through
        $r = Tool $s 'cadence_set_job_enabled' @{ jobId = $script:AgentJob; enabled = $false }
        Check 'cadence_set_job_enabled without a revision, racing nothing: "updated"' ([string]$r.outcome -eq 'updated') (Json $r)
        $r = Tool $s 'cadence_set_job_enabled' @{ jobId = $script:AgentJob; enabled = $true }
        Check 'and back on' ([string]$r.outcome -eq 'updated') (Json $r)
        $script:AgentRev = [string]$r.job.revision
    }

    AJourney 'A10b. A locked job is untouchable for agents' {
        $s = $script:S1
        $lockName = $Prefix + ' Agent Locked'
        $f = New-JobJson 'acclock1' $lockName '0 3 1 1 *' '2099-01-01' $agentCmd '/d /c exit 0'
        $r = Apply-Job $f
        Check ('owner creates a job with the CLI (exit ' + $r.Code + ')') ($r.Code -eq 0) ($r.Out + $r.Err)
        $r = Invoke-Cad @('--agents', 'lock', 'acclock1', '--yes')
        Check ('--agents lock acclock1 (exit ' + $r.Code + ')') ($r.Code -eq 0) ($r.Out + $r.Err)
        $xml0 = [string](Get-TestTask $lockName).Xml
        $g = Tool $s 'cadence_get_job' @{ jobId = 'acclock1'; recentRuns = 0 }
        Check 'the agent can read it and sees it is locked' ($g.lockedForAgents -eq $true) (Json $g)
        $try = @(
            @{ T = 'cadence_save_job'; A = @{ jobId = 'acclock1'; expectedRevision = [string]$g.revision; schedule = @{ cron = @('0 4 1 1 *') } } },
            @{ T = 'cadence_save_job'; A = @{ jobId = 'acclock1'; expectedRevision = [string]$g.revision; agentLock = $false } },
            @{ T = 'cadence_set_job_enabled'; A = @{ jobId = 'acclock1'; enabled = $false } },
            @{ T = 'cadence_run_job_now'; A = @{ jobId = 'acclock1' } },
            @{ T = 'cadence_delete_job'; A = @{ jobId = 'acclock1'; confirmName = $lockName } }
        )
        $n = 0
        foreach ($t in $try) { $r = Tool $s $t.T $t.A -AllowError; if ((ErrCode $r) -ne '') { $n++ } else { Fail ('locked job: ' + $t.T + ' was accepted: ' + (Json $r)) } }
        Check 'save, lock escape, disable, run-now and delete are all refused' ($n -eq 5)
        Check 'the locked task XML is byte-identical afterwards' (([string](Get-TestTask $lockName).Xml) -eq $xml0)
        [void](Invoke-Cad @('--agents', 'unlock', 'acclock1', '--yes'))
        [void](Invoke-Cad @('--delete', 'acclock1', '--yes'))
        Check 'the owner removed the locked job again' ($null -eq (Get-TestTask $lockName))
    }

    AJourney 'A11. Two writers collide gracefully' {
        $sa = $script:S1
        $sb = Open-Agent 'Cadence Acceptance Client B' '2025-11-25' 'cadence-acceptance-b'
        $script:Sess.B = $sb
        $ga = Tool $sa 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 0 }
        $gb = Tool $sb 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 0 }
        Check 'both sessions read the same revision' ([string]$ga.revision -eq [string]$gb.revision)
        $rev = [string]$ga.revision
        $mk = { param($id, $cron) ConvertTo-Json -Compress -Depth 10 -InputObject ([ordered]@{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = [ordered]@{ name = 'cadence_save_job'; arguments = [ordered]@{ jobId = $script:AgentJob; expectedRevision = $rev; schedule = [ordered]@{ cron = @($cron) } } } }) }
        $cronA = Pinned-Cron 10 12; $cronB = Pinned-Cron 20 13
        Send-CadenceMcpRaw -Session $sa -Text (& $mk 7001 $cronA)
        Send-CadenceMcpRaw -Session $sb -Text (& $mk 7002 $cronB)
        $la = Read-CadenceMcpLine -Session $sa -TimeoutSec 25
        $lb = Read-CadenceMcpLine -Session $sb -TimeoutSec 25
        $oa = ($la | ConvertFrom-Json).result.structuredContent
        $ob = ($lb | ConvertFrom-Json).result.structuredContent
        $outs = @([string]$oa.outcome, [string]$ob.outcome)
        $codes = @((ErrCode $oa), (ErrCode $ob))
        Check 'exactly one writer got "updated" and the other got stale_revision' ((($outs -contains 'updated') -and ($codes -contains 'stale_revision'))) ('A: ' + $la + ' B: ' + $lb)
        $loserMsg = if ($codes[0] -eq 'stale_revision') { ErrMsg $oa } else { ErrMsg $ob }
        $winnerTitle = if ($outs[0] -eq 'updated') { 'Cadence Acceptance Client' } else { 'Cadence Acceptance Client B' }
        Check 'the stale answer names the other writer' ($loserMsg -match [regex]::Escape($winnerTitle)) $loserMsg
        $winCron = if ($outs[0] -eq 'updated') { $cronA } else { $cronB }
        $g = Tool $sa 'cadence_get_job' @{ jobId = $script:AgentJob; recentRuns = 0 }
        Check 'the final job holds the winner''s schedule' (@($g.schedule.cron)[0] -eq $winCron) (Json $g.schedule)
        $j = Get-ProbeJob $script:AgentJob
        Check '--probe: the final task is inSync and equals the winner' ($j.sync -eq 'inSync' -and @($j.cron)[0] -eq $winCron) (Json $j)
        $script:AgentRev = [string]$g.revision
        $x = Close-Agent $sb 'A11 second writer'
        $script:Sess.B = $null
        Check 'writer B ends cleanly' ($x.Exited -and $x.ExitCode -eq 0 -and -not $x.Killed)
    }

    AJourney 'A12. Delete: the name must be confirmed; history goes with the job' {
        $s = $script:S1
        $id = $script:AgentJob
        $hist = Join-Path $LocalData ('runs\' + $id)
        $r = Tool $s 'cadence_delete_job' @{ jobId = $id; confirmName = 'not the name' } -AllowError
        Check 'a wrong confirmName is refused' ((ErrCode $r) -ne '' -and $null -ne (Get-TestTask $agentOkName)) (Json $r)
        $r = Tool $s 'cadence_delete_job' @{ jobId = $id; confirmName = $agentOkName }
        Check 'the right name: deleted' ($r.deleted -eq $true) (Json $r)
        Check 'the task is gone from Task Scheduler' ($null -eq (Get-TestTask $agentOkName))
        Check 'the run history folder is gone (%LOCALAPPDATA%\CadenceTest\runs\<id>)' (-not (Test-Path -LiteralPath $hist)) $hist
        $r = Tool $s 'cadence_delete_job' @{ jobId = $id; confirmName = $agentOkName }
        Check 'deleting again: alreadyGone' ($r.alreadyGone -eq $true) (Json $r)
        Check 'the foreign task is still byte-identical' ((Get-Sha ([string](Get-TestTask $agentForeignName).Xml)) -eq $script:AgentForeignHash)
        $script:AgentJob = $null
    }

    AJourney 'A13. Clean shutdown; the audit log is complete and holds no secrets' {
        $x = Close-Agent $script:S1 'main session'
        $script:S1 = $null
        Check 'closing stdin ends the main session with exit 0 within 5 s (no kill)' ($x.Exited -and $x.ExitCode -eq 0 -and -not $x.Killed) (Json $x)
        Check 'every session that was opened ended with exit 0, none had to be killed' (@($script:StopResults | Where-Object { $_.Exited -and $_.Code -eq 0 -and -not $_.Killed -and $_.Gone }).Count -eq $script:StopResults.Count -and $script:StopResults.Count -ge 3) (($script:StopResults | ForEach-Object { $_.Label + '=' + $_.Code }) -join ', ')
        Check 'no Cadence.exe is alive' (-not (Get-Process -Name Cadence -ErrorAction SilentlyContinue))
        $lines = @(Read-AuditLines)
        $parsed = @($lines | ForEach-Object { Parse-Audit $_ } | Where-Object { $_ })
        $outcomes = @($parsed | ForEach-Object { [string]$_.outcome } | Sort-Object -Unique)
        Check 'the audit log has connected, ok, refused and disconnected lines' (($outcomes -contains 'connected') -and ($outcomes -contains 'ok') -and ($outcomes -contains 'refused' -or $outcomes -contains 'suspended') -and ($outcomes -contains 'disconnected')) ('outcomes: ' + ($outcomes -join ','))
        Check 'every audit line is valid JSON' ($parsed.Count -eq $lines.Count -and $lines.Count -gt 20) ($parsed.Count.ToString() + ' of ' + $lines.Count)
        Check 'the audit log names the agent (Cadence Acceptance Client)' (@($lines | Where-Object { $_ -match 'Cadence Acceptance Client' }).Count -ge 1)
        $all = ($lines -join "`n") + "`n" + (($script:AgentAllText) -join "`n")
        Check 'the environment value never appears in the audit log, the replies or stderr' (-not $all.Contains($agentSecret))
        Check 'the foreign task is untouched at the end (XML hash)' ($null -ne (Get-TestTask $agentForeignName) -and (Get-Sha ([string](Get-TestTask $agentForeignName).Xml)) -eq $script:AgentForeignHash)
        if (Get-TestTask $agentForeignName) { Remove-OurTask $agentForeignName }
        Check 'no Cadence Acceptance Agent task is left except the older foreign ones (script-made)' (@(Get-TestTaskNames | Where-Object { $_ -like ($Prefix + ' Agent*') }).Count -eq 0) ((Get-TestTaskNames) -join '|')
    }
}
finally {
    # ---------------------------------------------------------------- cleanup, always (also on failure and Ctrl-C)
    Say ''
    Say '== Cleanup'
    try { Ui-Close } catch { }
    try { Stop-AllCadenceMcp } catch { }
    try { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'clean-test-tasks.ps1') | ForEach-Object { Say ('      ' + $_) } } catch { Say ('      cleanup of \CadenceTest failed: ' + $_.Exception.Message) }
    if (-not $pre.RegTest -and (Test-Path 'HKCU:\Software\CadenceTest')) { try { Remove-Item 'HKCU:\Software\CadenceTest' -Recurse -Force; Say '      removed HKCU\Software\CadenceTest (written by the UI)' } catch { } }
    # our own leftovers only: wrapper/test processes whose command line names CadenceTest, our ping, and any Cadence.exe we started
    try {
        foreach ($p in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and ($_.CommandLine -match 'CadenceTest' -or ($_.Name -eq 'PING.EXE' -and $_.CommandLine -match '-n 30 127\.0\.0\.1')) })) {
            if ($p.CommandLine -match 'acceptance\.ps1') { continue }
            try { Stop-Process -Id $p.ProcessId -Force; Say ('      stopped leftover ' + $p.Name + ' ' + $p.ProcessId) } catch { }
        }
    } catch { }
}

# ---------------------------------------------------------------- final invariants
Journey 'Final: nothing left behind, nothing else touched' {
    Check 'no \CadenceTest folder' (-not (Test-FolderExists $FolderPath))
    Check 'no %LOCALAPPDATA%\CadenceTest' (-not (Test-Path -LiteralPath $LocalData))
    Check 'no HKCU\Software\CadenceTest' (-not (Test-Path 'HKCU:\Software\CadenceTest'))
    Check '\Cadence was not created' ((Test-FolderExists '\Cadence') -eq $pre.CadenceFolder)
    Check '%LOCALAPPDATA%\Cadence was not created' ((Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Cadence')) -eq $pre.CadenceData)
    Check 'HKCU\Software\Cadence was not created' ((Test-Path 'HKCU:\Software\Cadence') -eq $pre.RegCadence)
    $left = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and ($_.Name -eq 'Cadence.exe' -or $_.CommandLine -match 'CadenceTest' -or ($_.Name -eq 'PING.EXE' -and $_.CommandLine -match '-n 30 127\.0\.0\.1')) -and $_.CommandLine -notmatch 'acceptance\.ps1' })
    Check 'no stray Cadence.exe / ping / cmd / powershell process' ($left.Count -eq 0) (($left | ForEach-Object { $_.Name + ' ' + $_.ProcessId }) -join ', ')
    if ($before) {
        $after = Get-Snapshot
        $removed = @($before.Keys | Where-Object { -not $after.ContainsKey($_) })
        $added = @($after.Keys | Where-Object { -not $before.ContainsKey($_) })
        $changed = @($before.Keys | Where-Object { $after.ContainsKey($_) -and $after[$_] -ne $before[$_] })
        Check ('all ' + $before.Count + ' tasks outside \CadenceTest are unchanged (names and XML hashes)') ($removed.Count + $added.Count + $changed.Count -eq 0) ('removed: ' + ($removed -join ', ') + '; added: ' + ($added -join ', ') + '; changed: ' + ($changed -join ', '))
    } else { Fail 'the before-snapshot was never taken' }
}

Say ''
Say '================================================================'
$secs = [int]((Get-Date) - $started).TotalSeconds
Say ('Acceptance summary: {0} PASS, {1} FAIL, {2} KNOWN-ISSUE ({3} s)' -f $script:Pass, $script:Fail, $script:Known, $secs)
if ($script:Known -gt 0) { Say 'KNOWN-ISSUE lines are the Windows Defender block described in the README "Safety notes"; they do not fail the run.' }
if ($script:Fail -gt 0) { Say 'RESULT: FAIL'; exit 1 }
Say 'RESULT: PASS'
exit 0
