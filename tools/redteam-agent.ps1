<#
  tools\redteam-agent.ps1 - live red-team pass against the Cadence agent write tools (PLAN 12, Checkpoint D / E).

  It starts the REAL bin\Cadence.exe --mcp --folder CadenceTest the way a buggy or hostile AI client would and attacks the
  write tools (cadence_save_job, cadence_set_job_enabled, cadence_run_job_now, cadence_delete_job) and the two switch tools.
  One line per attempt: HELD (the attack failed), BROKE (a rule was bypassed or the server misbehaved) or NOTE (an honest limit
  or an observation). Exit code 1 when any attempt BROKE.

  SAFETY (the script enforces them; do not weaken):
   * Only \CadenceTest, %LOCALAPPDATA%\CadenceTest and HKCU\Software\CadenceTest are created, changed or deleted. Never \Cadence.
   * Every job an attack gets the server to ACCEPT is disabled (and uses a yearly cron), is only read back (never run), and is deleted
     in the finally block. Acceptance is proved by cadence_get_job and the task XML. Only 'cmd.exe /d /c exit 0' is ever run.
   * Every process is started with CreateNoWindow, tracked, and killed in the finally block. No GUI is launched.
   * If Windows (Defender) blocks a launch the script records a NOTE and moves on.

  Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File tools\redteam-agent.ps1 [-Sections 1,2,...] [-FloodSeconds 45]
  Takes about 10-20 minutes.
#>
param(
  [string[]]$Sections = @('1,2,3,4,5,6,7,8,9'),
  [int]$FloodSeconds = 60,
  [string]$Exe = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Off
$Sections = @($Sections | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { [int]$_ })
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_DISABLE_GUI_ERRORS = '1'
$Secret = 'RT_SECRET_' + [guid]::NewGuid().ToString('N')
$env:CADENCE_RT_SECRET = $Secret            # inherited by every server we start: it must never come back out
$env:CADENCE_RT_ENVVAL = 'RT_ENVVAL_' + [guid]::NewGuid().ToString('N').Substring(0, 12)
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\Cadence.exe' }
if (-not (Test-Path $Exe)) { throw "Cadence.exe not found at $Exe (run build.cmd first)" }
$LocalRoot = Join-Path $env:LOCALAPPDATA 'CadenceTest'
$Work = Join-Path $LocalRoot 'redteam'
$AuditFile = Join-Path $LocalRoot 'agents\audit.jsonl'
$AuditFile1 = Join-Path $LocalRoot 'agents\audit.1.jsonl'
$OwnerFile = Join-Path $LocalRoot 'agents\audit-owner.jsonl'   # [P23] owner and switch events
$RegPath = 'Software\CadenceTest\Agents'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$Procs = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$AllText = New-Object System.Collections.Generic.List[string]     # every line any server wrote to stdout/stderr, for the leak and purity scans
$Out = New-Object System.Collections.Generic.List[string]
$script:nHeld = 0; $script:nBroke = 0; $script:nNote = 0
$CMD = 'C:\Windows\System32\cmd.exe'

function Clip($s, $n = 150) { if ($null -eq $s) { return '<null>' }; $s = ([string]$s) -replace '[\x00-\x1f]', ' '; if ($s.Length -le $n) { $s } else { $s.Substring(0, $n) + '...' } }
function Emit($tag, $name, $ev) { $l = ('{0,-5} {1}' -f $tag, $name) + $(if ($ev) { '  -- ' + (Clip $ev 700) } else { '' }); $Out.Add($l); Write-Host $l }
function Held($name, $ev = '') { $script:nHeld++; Emit 'HELD' $name $ev }
function Broke($name, $ev = '') { $script:nBroke++; Emit 'BROKE' $name $ev }
function Note($name, $ev = '') { $script:nNote++; Emit 'NOTE' $name $ev }
function Chk($ok, $name, $ev = '') { if ($ok) { Held $name $ev } else { Broke $name $ev } }
function Head($t) { Write-Host ''; Write-Host "=== $t ===" }

# ---------------------------------------------------------------- Task Scheduler (COM) helpers: read and cleanup only, plus the two fixtures
$svc = New-Object -ComObject Schedule.Service; $svc.Connect()
$sha = [System.Security.Cryptography.SHA256]::Create()
function HashOf([string]$s) { [BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes([string]$s))).Replace('-', '').Substring(0, 16) }
function FolderOrNull($p) { try { $svc.GetFolder($p) } catch { $null } }
function WalkTasks($f, $acc, [bool]$skipCadence) {
  if ($skipCadence -and ($f.Path -ieq '\Cadence' -or $f.Path -ieq '\CadenceTest')) { return }
  try { foreach ($t in $f.GetTasks(1)) { $x = ''; try { $x = $t.Xml } catch { $x = '<unreadable>' }; $acc[$t.Path] = HashOf $x } } catch { }
  try { foreach ($s in $f.GetFolders(0)) { WalkTasks $s $acc $skipCadence } } catch { }
}
function SnapOthers { $acc = @{}; WalkTasks ($svc.GetFolder('\')) $acc $true; $acc }
function SnapCT { $acc = @{}; $f = FolderOrNull '\CadenceTest'; if ($f) { WalkTasks $f $acc $false }; $acc }
function CTSubfolders { $f = FolderOrNull '\CadenceTest'; if (-not $f) { return @() }; $l = New-Object System.Collections.Generic.List[string]; function W($x) { foreach ($s in $x.GetFolders(0)) { $l.Add($s.Path); W $s } }; W $f; @($l) }
function TaskXml($path) { try { ($svc.GetFolder('\CadenceTest').GetTask($path)).Xml } catch { $null } }
function SameMap($a, $b) { (DiffMap $a $b) -eq '' }
function DiffMap($a, $b) { $d = New-Object System.Collections.Generic.List[string]; foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { $d.Add("-$k") } elseif ($a[$k] -ne $b[$k]) { $d.Add("~$k") } }; foreach ($k in $b.Keys) { if (-not $a.ContainsKey($k)) { $d.Add("+$k") } }; ($d -join ', ') }
function EnsureCT { $root = $svc.GetFolder('\'); $f = FolderOrNull '\CadenceTest'; if (-not $f) { $f = $root.CreateFolder('CadenceTest') }; $f }
function ForeignXml($desc) { @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>$desc</Description></RegistrationInfo>
  <Triggers><TimeTrigger><StartBoundary>2099-01-01T00:00:00</StartBoundary><Enabled>false</Enabled></TimeTrigger></Triggers>
  <Settings><Enabled>false</Enabled></Settings>
  <Actions Context="Author"><Exec><Command>C:\Windows\System32\cmd.exe</Command><Arguments>/d /c exit 0</Arguments></Exec></Actions>
</Task>
"@ }
function AddForeign($name, $desc = 'red-team foreign task') { $f = EnsureCT; $null = $f.RegisterTask($name, (ForeignXml $desc), 6, $null, $null, 3, $null); ($f.GetTask($name)).Xml }
function WipeCT { $f = FolderOrNull '\CadenceTest'; if ($f) { try { function WDel($x) { foreach ($s in @($x.GetFolders(0))) { WDel $s; try { $x.DeleteFolder($s.Name, 0) } catch { } }; foreach ($t in @($x.GetTasks(1))) { try { $x.DeleteTask($t.Name, 0) } catch { } } }; WDel $f } catch { } } }

# ---------------------------------------------------------------- registry switch (HKCU\Software\CadenceTest only)
function SwitchRaw([int]$v) { $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($RegPath); try { $k.SetValue('Suspended', $v, [Microsoft.Win32.RegistryValueKind]::DWord); if ($v -eq 1) { $k.SetValue('SuspendedAt', [DateTimeOffset]::Now.ToString('o')); $k.SetValue('SuspendedBy', 'cli') } } finally { $k.Close() } }
function SwitchState { $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($RegPath); if (-not $k) { return $false }; try { [int]($k.GetValue('Suspended', 0)) -eq 1 } finally { $k.Close() } }

# ---------------------------------------------------------------- processes
function NewPsi($args_) { $psi = New-Object System.Diagnostics.ProcessStartInfo; $psi.FileName = $Exe; $psi.Arguments = $args_; $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true; $psi.WindowStyle = 'Hidden'; $psi.StandardOutputEncoding = $utf8; $psi.StandardErrorEncoding = $utf8; $psi }
function RunCli([string]$argLine, [int]$timeoutMs = 90000) {
  $p = [System.Diagnostics.Process]::Start((NewPsi $argLine)); $Procs.Add($p); $p.StandardInput.Close()
  $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
  if (-not $p.WaitForExit($timeoutMs)) { try { $p.Kill() } catch { }; return @{ Code = -1; Out = 'timeout'; Err = '' } }
  $p.WaitForExit()
  $r = @{ Code = $p.ExitCode; Out = $o.Result; Err = $e.Result }
  $AllText.Add('[cli stderr] ' + $r.Err)
  $r
}
function Probe { $r = RunCli '--probe --all --folder CadenceTest'; try { $r.Out | ConvertFrom-Json } catch { $null } }
function ProbeJob($id) { $p = Probe; if ($p) { @($p.jobs | Where-Object { $_.id -eq $id })[0] } }
function Owner-Apply($id, $name, $cron = '0 3 1 1 *', $enabled = $false, $extraRun = '') {
  $f = Join-Path $Work ("own-$id.json")
  $j = '{"id":"' + $id + '","name":"' + $name + '","enabled":' + $enabled.ToString().ToLower() + ',"schedule":{"cron":"' + $cron + '"},"run":{"exe":"C:\\Windows\\System32\\cmd.exe","args":"/d /c exit 0"}' + $extraRun + '}'
  [IO.File]::WriteAllText($f, $j, $utf8)
  RunCli ('--apply "' + $f + '" --yes --folder CadenceTest')
}

# ---------------------------------------------------------------- MCP session
function Start-Mcp([string]$name = 'redteam', [string]$title = $null, [string]$extraArgs = '') {
  $p = [System.Diagnostics.Process]::Start((NewPsi ('--mcp --folder CadenceTest' + $extraArgs))); $Procs.Add($p)
  $s = @{ P = $p; In = $p.StandardInput.BaseStream; Pending = $null; Err = $p.StandardError.ReadToEndAsync(); Name = $name; Id = 100; Peak = 0L }
  $nameJson = ($name | ConvertTo-Json -Compress)
  $ci = '{"name":' + $nameJson + ',"version":"1.0"' + $(if ($title) { ',"title":' + ($title | ConvertTo-Json -Compress) } else { '' }) + '}'
  $r = Req $s ('{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":' + $ci + '}}')
  if (-not $r -or -not $r.result) { throw "initialize failed: $r" }
  SendLine $s '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  $s
}
function SendBytes($s, [byte[]]$b) { $s.In.Write($b, 0, $b.Length); $s.In.Flush() }
function SendLine($s, [string]$text) { SendBytes $s ($utf8.GetBytes($text + "`n")) }
function ReadOne($s, [int]$ms = 15000) {
  if ($null -eq $s.Pending) { $s.Pending = $s.P.StandardOutput.ReadLineAsync() }
  if ($s.Pending.Wait($ms)) { $l = $s.Pending.Result; $s.Pending = $null; if ($null -ne $l) { $AllText.Add($l) }; return $l }
  '<<timeout>>'
}
function Parse($line) { if ($line -eq '<<timeout>>' -or $null -eq $line) { return $null }; try { $line | ConvertFrom-Json } catch { $null } }
function Req($s, [string]$json, [int]$ms = 15000) { SendLine $s $json; Parse (ReadOne $s $ms) }
function J($o) { ConvertTo-Json -InputObject $o -Compress -Depth 40 }
function CallJson($name, $argsJson, $id) { '{"jsonrpc":"2.0","id":' + $id + ',"method":"tools/call","params":{"name":"' + $name + '","arguments":' + $argsJson + '}}' }
function TJ($s, $name, [string]$argsJson, [int]$ms = 20000) { $s.Id++; Req $s (CallJson $name $argsJson $s.Id) $ms }
function T($s, $name, $argsObj, [int]$ms = 20000) { TJ $s $name (J $argsObj) $ms }
function Stop-Mcp($s, [int]$waitMs = 12000) {
  try { $s.In.Close() } catch { }
  $exited = $s.P.WaitForExit($waitMs)
  while ($true) { $l = ReadOne $s 1500; if ($l -eq '<<timeout>>' -or $null -eq $l) { break } }
  if (-not $exited) { try { $s.P.Kill() } catch { } }
  $e = ''; if ($s.Err.Wait(3000)) { $e = $s.Err.Result }
  $AllText.Add('[server stderr] ' + $e)
  @{ Exited = $exited; Code = $(if ($exited) { $s.P.ExitCode } else { $null }); Err = $e }
}
function Kill-Mcp($s) { try { $s.P.Kill() } catch { }; try { $s.P.WaitForExit(3000) | Out-Null } catch { }; try { $AllText.Add('[server stderr] ' + $(if ($s.Err.Wait(2000)) { $s.Err.Result } else { '' })) } catch { } }

# result inspection
function Code($r) { if ($null -eq $r) { return 'none' }; if ($r.error) { return 'rpc' + $r.error.code }; if ($r.result -and $r.result.isError) { return [string]$r.result.structuredContent.error.code }; if ($r.result) { return 'ok' }; 'none' }
function Msg($r) { if ($null -eq $r) { return '<no reply>' }; if ($r.error) { return [string]$r.error.message }; if ($r.result.content) { return [string]$r.result.content[0].text }; '' }
function SCont($r) { if ($r -and $r.result) { $r.result.structuredContent } }
function Outcome($r) { (SCont $r).outcome }
function JobOf($r) { (SCont $r).job }

function SaveArgs($name, $path = $CMD, $arguments = '/d /c exit 0', $cron = @('0 3 1 1 *'), $enabled = $false, $extraProg = $null) {
  $prog = [ordered]@{ path = $path; arguments = $arguments }
  if ($extraProg) { foreach ($k in $extraProg.Keys) { $prog[$k] = $extraProg[$k] } }
  [ordered]@{ name = $name; schedule = [ordered]@{ cron = @($cron) }; program = $prog; enabled = $enabled }
}
function Create($s, $name, $path = $CMD, $arguments = '/d /c exit 0', $cron = @('0 3 1 1 *'), $enabled = $false, $extraProg = $null) { T $s 'cadence_save_job' (SaveArgs $name $path $arguments $cron $enabled $extraProg) }
function GetJob($s, $id) { T $s 'cadence_get_job' @{ jobId = $id; recentRuns = 0 } }
function Rev($s, $id) { (SCont (GetJob $s $id)).revision }
function DelJob($s, $id, $name) { T $s 'cadence_delete_job' @{ jobId = $id; confirmName = $name } }
function ListNames($s) { $r = T $s 'cadence_list_jobs' @{}; @((SCont $r).jobs) }

# audit helpers
function AuditAll { $l = New-Object System.Collections.Generic.List[string]; foreach ($f in @($AuditFile1, $AuditFile, $OwnerFile)) { if (Test-Path $f) { try { $fs = [IO.File]::Open($f, 'Open', 'Read', 'ReadWrite,Delete'); $sr = New-Object IO.StreamReader($fs, $utf8); $t = $sr.ReadToEnd(); $sr.Close(); foreach ($x in ($t -split "`n")) { if ($x.Length -gt 0) { $l.Add($x.TrimEnd("`r")) } } } catch { } } }; # [P23] owner/switch lines live in audit-owner.jsonl: merge by time
  ,@($(for ($ix = 0; $ix -lt $l.Count; $ix++) { [pscustomobject]@{ K = [regex]::Match($l[$ix], '"at":"([^"]+)"').Groups[1].Value; I = $ix; X = $l[$ix] } }) | Sort-Object K, I | ForEach-Object { $_.X }) }   # stable: same-second lines keep file order
function AuditCount { $n = 0; foreach ($AuditFile in @($AuditFile, $OwnerFile)) { if (Test-Path $AuditFile) { try { $fs = [IO.File]::Open($AuditFile, 'Open', 'Read', 'ReadWrite,Delete'); $sr = New-Object IO.StreamReader($fs, $utf8); $t = $sr.ReadToEnd(); $sr.Close(); $n += @($t -split "`n" | Where-Object { $_.Length -gt 0 }).Count } catch { } } }; $n }
function AuditHasText([string]$needle) { foreach ($f in @($AuditFile, $AuditFile1, (Join-Path $LocalRoot 'agents\audit.2.jsonl'), (Join-Path $LocalRoot 'agents\audit.3.jsonl'), (Join-Path $LocalRoot 'agents\audit.4.jsonl'), $OwnerFile, (Join-Path $LocalRoot 'agents\audit-owner.1.jsonl'))) {   # [CP-E] every file of both streams: it read only audit.jsonl and audit.1.jsonl, so the owner's line (in audit-owner.jsonl since Phase 23) always looked 'erased'
 if (Test-Path $f) { try { $fs = [IO.File]::Open($f, 'Open', 'Read', 'ReadWrite,Delete'); $sr = New-Object IO.StreamReader($fs, $utf8); $t = $sr.ReadToEnd(); $sr.Close(); if ($t.Contains($needle)) { return $true } } catch { } } }; $false }
function AuditWeight { $n = 0; foreach ($f in @($AuditFile, $OwnerFile)) { if (Test-Path $f) { try { $fs = [IO.File]::Open($f, 'Open', 'Read', 'ReadWrite,Delete'); $sr = New-Object IO.StreamReader($fs, $utf8); $t = $sr.ReadToEnd(); $sr.Close(); foreach ($x in ($t -split "`n")) { if ($x.Length -gt 0) { $m = [regex]::Match($x, '"count":(\d+)'); $n += $(if ($m.Success) { [int]$m.Groups[1].Value } else { 1 }) } } } catch { } } }; $n }   # [CP-E] calls the log stands for (a folded line counts its 'count')
function AuditTail($n) { $a = AuditAll; @($a | Select-Object -Last $n) }
function AuditParse($line) { try { $line | ConvertFrom-Json } catch { $null } }
function ProcWorkingSet($s) { try { $s.P.Refresh(); [long]$s.P.WorkingSet64 } catch { 0 } }


# ================================================================ baseline, fixtures
$before = SnapOthers
$cadFolderBefore = [bool](FolderOrNull '\Cadence')
$cadLocalBefore = Test-Path (Join-Path $env:LOCALAPPDATA 'Cadence')
$cadRegBefore = Test-Path 'HKCU:\Software\Cadence'
$ctExistedBefore = [bool](FolderOrNull '\CadenceTest')
$ctLocalExistedBefore = Test-Path $LocalRoot
$ctRegExistedBefore = Test-Path 'HKCU:\Software\CadenceTest'
Write-Host ("baseline: {0} tasks outside \Cadence*; \Cadence exists={1}; local\Cadence={2}; HKCU\Software\Cadence={3}; \CadenceTest existed={4} local={5} reg={6}" -f $before.Count, $cadFolderBefore, $cadLocalBefore, $cadRegBefore, $ctExistedBefore, $ctLocalExistedBefore, $ctRegExistedBefore)
if ($ctExistedBefore -or $ctLocalExistedBefore -or $ctRegExistedBefore) { Write-Host 'NOTE: leftovers of an earlier CadenceTest run exist; they are removed first.' ; WipeCT; try { $null = $svc.GetFolder('\').DeleteFolder('CadenceTest', 0) } catch { }; if (Test-Path $LocalRoot) { [IO.Directory]::Delete($LocalRoot, $true) }; try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\CadenceTest', $false) } catch { } }
[void][IO.Directory]::CreateDirectory($Work)
$OwnerId = 'rtown001'; $OwnerName = 'RT owner job'
$LockId = 'rtlock01'; $LockName = 'RT locked job'
$ForeignName = 'RT foreign'
$ForeignXml0 = $null
try {
  $S = $null
  # fixtures: an owner job made by the CLI, a foreign task, a locked job
  $c = Owner-Apply $OwnerId $OwnerName
  if ($c.Code -ne 0) { throw "fixture apply failed: $($c.Out) $($c.Err)" }
  $c = Owner-Apply 'rtlock01' $LockName
  if ($c.Code -ne 0) { throw "fixture apply failed: $($c.Out) $($c.Err)" }
  $c = RunCli ('--agents lock rtlock01 --yes --folder CadenceTest'); if ($c.Code -ne 0) { throw "lock failed: $($c.Out) $($c.Err)" }
  $ForeignXml0 = AddForeign $ForeignName
  $ForeignHash0 = HashOf $ForeignXml0
  $LockXml0 = TaskXml 'RT locked job'
  $OwnerXml0 = TaskXml 'RT owner job'
  Write-Host ("fixtures: owner job {0}, locked job {1}, foreign task '{2}' (hash {3})" -f $OwnerId, $LockId, $ForeignName, $ForeignHash0)
  $S = Start-Mcp 'redteam' 'Red-team client'
  $r = Req $S '{"jsonrpc":"2.0","id":"tl","method":"tools/list"}'
  $tools = @($r.result.tools | ForEach-Object { $_.name })
  Chk ($tools.Count -eq 12) 'server offers exactly 12 tools' ($tools -join ',')
  function ForeignIntact { (HashOf (TaskXml $ForeignName)) -eq $ForeignHash0 }
  function LockIntact { (TaskXml 'RT locked job') -eq $LockXml0 }


  # ---------------------------------------------------------------- shared helpers for the sections
  $Fixtures = @('RT owner job', 'RT locked job', 'RT foreign')
  function Reset-Agent { # delete every task in \CadenceTest except the fixtures (COM, no rate limit) and any subfolder
    $f = FolderOrNull '\CadenceTest'; if (-not $f) { return }
    foreach ($t in @($f.GetTasks(1))) { if ($Fixtures -notcontains $t.Name) { try { $f.DeleteTask($t.Name, 0) } catch { } } }
    foreach ($sub in @($f.GetFolders(0))) { try { WDel $sub; $f.DeleteFolder($sub.Name, 0) } catch { } }
  }
  function New-Session([string]$name = 'redteam') { try { if ($script:S -and -not $script:S.P.HasExited) { $null = Stop-Mcp $script:S 8000 } } catch { }; $script:S = Start-Mcp $name 'Red-team client' }
  $script:Resets = 0
  function TW($name, $argsObj, [int]$ms = 25000) { # a write call that restarts the server when the 30-a-minute bucket is empty
    for ($i = 0; $i -lt 3; $i++) { $r = T $script:S $name $argsObj $ms; if ((Code $r) -eq 'rate_limited') { $script:Resets++; New-Session; continue }; return $r }
    $r
  }
  function TWJ($name, [string]$json, [int]$ms = 25000) {
    for ($i = 0; $i -lt 3; $i++) { $r = TJ $script:S $name $json $ms; if ((Code $r) -eq 'rate_limited') { $script:Resets++; New-Session; continue }; return $r }
    $r
  }
  function IsWrite($r) { # did the call report that it changed or started something?
    $c = Code $r; if ($c -ne 'ok') { return $false }
    $o = SCont $r; ($o.outcome -in @('created', 'updated', 'renamed', 'refreshed')) -or ($o.deleted -eq $true) -or ($o.started -eq $true)
  }
  function Make-Fixture-Id-Variants { }

if ($Sections -contains 1) {
  Head '1 foreign and cross-folder reach'
  Reset-Agent
  $snap0 = SnapCT
  # 1a. every id-taking tool, hit with names, paths, look-alikes and guessed ids of the foreign task
  $badIds = @('RT foreign', '\CadenceTest\RT foreign', 'RT foreign (2)', '..\RT foreign', '..\..\Windows\System32\Tasks\RT foreign', '\\?\C:\x', 'NUL', 'CON', 'COM1', 'RTOWN001', 'rtown001 ', "rtown001`n", "rtown001`0", 'rtown00' + [char]0xFF11, 'rtown00' + [char]0x0661, 'rt0wn001', 'rtown00l', 'rtown00', 'rtown0011', '', ' ', 'rtown001/../rtown001', 'RTFORE~1', 'rtforeig', 'foreign0', 'cadence1', 'rtown002', 'rtown000', 'rtlock02', 'rtlock00', ('a' * 10000))
  $guess = New-Object System.Collections.Generic.List[string]
  $rnd = New-Object System.Random 4242
  $alpha = 'abcdefghijklmnopqrstuvwxyz0123456789'
  for ($i = 0; $i -lt 40; $i++) { $g = ''; for ($k = 0; $k -lt 8; $k++) { $g += $alpha[$rnd.Next(36)] }; $guess.Add($g) }
  $write = 0; $odd = New-Object System.Collections.Generic.List[string]
  New-Session
  $idList = @($badIds) + @($guess)
  foreach ($id in $idList) {
    $idJ = $id | ConvertTo-Json -Compress
    foreach ($tool in @(
        @('cadence_get_job', '{"jobId":' + $idJ + '}'),
        @('cadence_set_job_enabled', '{"jobId":' + $idJ + ',"enabled":true}'),
        @('cadence_run_job_now', '{"jobId":' + $idJ + '}'),
        @('cadence_delete_job', '{"jobId":' + $idJ + ',"confirmName":"RT foreign"}'),
        @('cadence_get_run_output', '{"jobId":' + $idJ + ',"runId":"20261001-000000-000"}'),
        @('cadence_save_job', '{"jobId":' + $idJ + ',"expectedRevision":"000000000000","name":"pwn"}'))) {
      $r = TWJ $tool[0] $tool[1]
      if (IsWrite $r) { $write++; $odd.Add($tool[0] + ' ' + (Clip $id 30)) }
      elseif ((Code $r) -eq 'ok' -and $tool[0] -ne 'cadence_delete_job') { $odd.Add($tool[0] + ' ok for ' + (Clip $id 30)) }
    }
  }
  $snap1 = SnapCT
  Chk ($write -eq 0 -and $odd.Count -eq 0) ("1a: {0} bogus/guessed ids x 6 tools: nothing written, nothing found" -f $idList.Count) (($odd | Select-Object -First 3) -join '; ')
  Chk ((DiffMap $snap0 $snap1) -eq '') '1a: \CadenceTest task XML hashes identical before/after (foreign, owner and locked tasks untouched)' (DiffMap $snap0 $snap1)
  # the alreadyGone path must not touch anything either
  $r = TW 'cadence_delete_job' @{ jobId = 'zzzzzzzz'; confirmName = 'RT foreign' }; Chk ((SCont $r).alreadyGone -eq $true -and (DiffMap $snap0 (SnapCT)) -eq '') '1a: delete of an unknown but well-formed id reports alreadyGone and touches nothing' (Msg $r)
  # the tools never list the foreign task
  $names = @(ListNames $script:S | ForEach-Object { $_.name })
  Chk ($names -notcontains 'RT foreign') '1a: cadence_list_jobs never shows the foreign task in the managed folder' ($names -join ',')
  $r = T $script:S 'cadence_list_jobs' @{ nameContains = 'foreign' }; Chk ((SCont $r).count -eq 0) '1a: nameContains=foreign finds nothing' (Msg $r)

  # 1b. names that try to land on, shadow or escape: created through the agent, then checked in the Task Scheduler tree
  $u = [char]0x2028; $bidi = [string][char]0x202E; $cyr = [string][char]0x0435; $full = ([string][char]0xFF32) + ([string][char]0xFF34)
  $names = @('..', '..\..\Windows\System32\Tasks\x', 'a\b', 'a/b', 'C:\x', 'a:b', 'trail.', 'trail ', 'trail. . ', 'CON', 'NUL', 'com1', 'LPT9.txt', 'AUX.x', 'RTFORE~1', ('RT for' + $cyr + 'ign'), ($full + ' foreign'), 'rt FOREIGN', 'RT foreign ', 'RT  foreign', ('RT' + [char]0xA0 + 'foreign'), 'RT foreign.', 'RT foreign..', ('x' * 100), ('name ' + $bidi + 'live'), "name`0nul", "line1`nline2", ("a" + $u + "b"), '.', '...', '-', '___', 'rt OWNER job', 'RT locked job.', 'RT LOCKED JOB', "RT locked`tjob", '\CadenceTest\RT foreign', '\\server\share\x', '%SystemRoot%', '$(whoami)', "x'; Remove-Item C:\ -Recurse #", '"; exit 1; "', '<script>')
  $bad = New-Object System.Collections.Generic.List[string]; $okNames = 0
  foreach ($n in $names) {
    Reset-Agent
    $r = TW 'cadence_save_job' (SaveArgs $n)
    $c = Code $r
    if ($c -eq 'ok') {
      $okNames++
      $tp = [string](JobOf $r).taskPath
      $tail = $tp.Substring('\CadenceTest\'.Length)
      $subs = CTSubfolders
      $delta = DiffMap $snap0 (SnapCT)
      $xml = TaskXml $tail
      $valid = $false; try { $d = New-Object System.Xml.XmlDocument; $d.LoadXml($xml); $valid = $true } catch { }
      if (-not $tp.StartsWith('\CadenceTest\') -or $tail.Contains('\') -or $tail.Contains('/') -or $subs.Count -gt 0 -or -not (ForeignIntact) -or -not (LockIntact) -or -not $valid -or $delta -notmatch '^\+\\CadenceTest\\[^\\]+$') { $bad.Add((Clip $n 30) + ' -> ' + $tp + ' delta=' + $delta + ' subs=' + ($subs -join ',')) }
    } elseif (-not (SameMap $snap0 (SnapCT))) { $bad.Add((Clip $n 30) + ' refused (' + $c + ') but the folder changed: ' + (DiffMap $snap0 (SnapCT))) }
  }
  Reset-Agent
  Chk ($bad.Count -eq 0) ("1b: {0} hostile display names ({1} accepted, rest refused): every task stays one level under \CadenceTest, valid XML, foreign and locked tasks untouched, no subfolder" -f $names.Count, $okNames) (($bad | Select-Object -First 3) -join ' | ')
  # a name that collides with the foreign task must get its own task name (never overwrite)
  $r = TW 'cadence_save_job' (SaveArgs 'rt FOREIGN'); $tp = [string](JobOf $r).taskPath
  Chk ((Code $r) -eq 'ok' -and $tp -ne '\CadenceTest\RT foreign' -and (ForeignIntact)) '1b: a job named like the foreign task (case differs) gets a different task name, foreign task untouched' $tp
  # rename onto the foreign task name, onto the owner job's name, onto the locked job's name
  $id = (JobOf $r).jobId; $rev = (JobOf $r).revision
  foreach ($target in @('RT foreign', 'RT owner job', 'RT locked job')) {
    $rev = Rev $script:S $id
    $r2 = TW 'cadence_save_job' ([ordered]@{ jobId = $id; expectedRevision = $rev; name = $target })
    $c2 = Code $r2
    Chk (($c2 -in @('name_taken', 'ok')) -and (ForeignIntact) -and (LockIntact) -and (TaskXml 'RT owner job') -eq $OwnerXml0 -and ($c2 -ne 'ok' -or (JobOf $r2).taskPath -ne ('\CadenceTest\' + $target))) ("1b: rename an agent job to '$target'") ("$c2 " + (Clip (Msg $r2) 100) + $(if ($c2 -eq 'ok') { ' -> ' + (JobOf $r2).taskPath } else { '' }))
  }
  Reset-Agent

  # 1c. damaged, copied, newer-version and drifted jobs, addressed by their ids
  $f = EnsureCT
  $null = Owner-Apply 'rtdrift1' 'RT drift'; $null = Owner-Apply 'rtdamag1' 'RT damaged'; $null = Owner-Apply 'rtnewer1' 'RT newer'; $null = Owner-Apply 'rtcopy01' 'RT copy src'
  function Rereg($taskName, $xml) { $null = (EnsureCT).RegisterTask($taskName, $xml, 6, $null, $null, 3, $null) }
  $x = TaskXml 'RT drift'; Rereg 'RT drift' ($x -replace '<StartWhenAvailable>true</StartWhenAvailable>', '<StartWhenAvailable>false</StartWhenAvailable>')
  $x = TaskXml 'RT damaged'; Rereg 'RT damaged' ($x -replace '"schema":1,', '"schema":"x",{{{')
  $x = TaskXml 'RT newer'; Rereg 'RT newer' ($x -replace '"schema":1,', '"schema":99,')
  $x = TaskXml 'RT copy src'; Rereg 'RT copy src (copy)' $x
  Start-Sleep -Milliseconds 300
  $pr = Probe
  $states = @($pr.jobs | Where-Object { $_.id -like 'rtd*' -or $_.id -like 'rtn*' -or $_.id -like 'rtc*' } | ForEach-Object { $_.id + ':' + $_.ownership + '/' + $_.sync + '/' + $_.problem }) -join ' ; '
  Note '1c: states the CLI sees for the four prepared jobs' (Clip $states 400)
  $fx = @{}; foreach ($t in @('RT drift', 'RT damaged', 'RT newer', 'RT copy src', 'RT copy src (copy)')) { $fx[$t] = TaskXml $t }
  $matrix = ''; $snapD = SnapCT; $oddD = New-Object System.Collections.Generic.List[string]; $codesSeen = @{}
  foreach ($id in @('rtdrift1', 'rtdamag1', 'rtnewer1')) {
    $nm = switch ($id) { 'rtdrift1' { 'RT drift' } 'rtdamag1' { 'RT damaged' } default { 'RT newer' } }
    foreach ($tool in @(@('cadence_set_job_enabled', @{ jobId = $id; enabled = $true }), @('cadence_run_job_now', @{ jobId = $id }), @('cadence_delete_job', @{ jobId = $id; confirmName = $nm }), @('cadence_save_job', [ordered]@{ jobId = $id; expectedRevision = '000000000000'; name = 'hijack' }), @('cadence_save_job', [ordered]@{ jobId = $id; name = 'hijack2'; program = @{ arguments = '/d /c exit 7' } }))) {
      $r = TW $tool[0] $tool[1]
      $c = Code $r
      if ($c -eq 'ok' -and $tool[0] -ne 'cadence_delete_job') { $oddD.Add("$id $($tool[0]) ok") } elseif ($c -eq 'ok' -and (SCont $r).deleted -eq $true) { $oddD.Add("$id deleted") }
      $codesSeen[$c] = 1; $matrix += ($id.Substring(2,4) + '/' + $tool[0].Replace('cadence_','').Replace('_job','') + '=' + $c + '; ')
    }
  }
  $dd = DiffMap $snapD (SnapCT)
  Chk ($oddD.Count -eq 0 -and $dd -eq '') '1c: drifted / damaged / newer-version jobs refuse every agent write and run-now (15 calls); their tasks are byte-identical afterwards' ((($oddD | Select-Object -First 3) -join '; ') + ' ' + $dd + ' codes: ' + (($codesSeen.Keys | Sort-Object) -join ','))
  Note '1c: codes per id and tool' (Clip $matrix 900)
  $r = TW 'cadence_delete_job' @{ jobId = 'rtdamag1'; confirmName = 'RT damaged' }
  if ((Code $r) -eq 'ok' -and (SCont $r).alreadyGone -eq $true -and $null -ne (TaskXml 'RT damaged')) { Note '1c: delete by id of a DAMAGED job answers alreadyGone although its task exists (a damaged job has no readable id, so the agent cannot be told it is protected; nothing was deleted)' (Clip (Msg $r) 120) }
  # a copy: the ORIGINAL is the job (by design); the copy task itself must never be written. Then with the original gone, the lone copy is refused.
  $copyX = TaskXml 'RT copy src (copy)'
  $null = TW 'cadence_set_job_enabled' @{ jobId = 'rtcopy01'; enabled = $true }
  $null = TW 'cadence_run_job_now' @{ jobId = 'rtcopy01' }
  Chk ((TaskXml 'RT copy src (copy)') -eq $copyX) '1c: operations on a duplicated id land on the original; the copy task is byte-identical' ''
  $r = TW 'cadence_delete_job' @{ jobId = 'rtcopy01'; confirmName = 'RT copy src' }
  $gone = $null -eq (TaskXml 'RT copy src')
  Note '1c: deleting by the id of a duplicated job deletes the original (the copy stays for the owner to resolve)' ((Code $r) + ' originalGone=' + $gone + ' copyStillThere=' + ((TaskXml 'RT copy src (copy)') -eq $copyX))
  $odd2 = New-Object System.Collections.Generic.List[string]
  foreach ($tool in @(@('cadence_set_job_enabled', @{ jobId = 'rtcopy01'; enabled = $true }), @('cadence_run_job_now', @{ jobId = 'rtcopy01' }), @('cadence_delete_job', @{ jobId = 'rtcopy01'; confirmName = 'RT copy src' }), @('cadence_save_job', [ordered]@{ jobId = 'rtcopy01'; expectedRevision = '000000000000'; name = 'hijack3'; program = @{ arguments = '/d /c exit 8' } }))) {
    $r = TW $tool[0] $tool[1]; if ((Code $r) -eq 'ok' -and (SCont $r).alreadyGone -ne $true) { $odd2.Add($tool[0] + ' ok') }
  }
  Chk ($odd2.Count -eq 0 -and (TaskXml 'RT copy src (copy)') -eq $copyX) '1c: with the original gone, the lone copy refuses agent writes and stays byte-identical' (($odd2 -join ',') + ' ' + (Code $r) + ' ' + (Clip (Msg $r) 100))
  $r = GetJob $script:S 'rtnewer1'; Note '1c: get_job on a newer-version job' ((Code $r) + ' ' + (Clip (Msg $r) 120))
  # 1d. odd --folder values for the server itself (stdin closed: only the argument parser and startup run)
  $folderBad = New-Object System.Collections.Generic.List[string]
  foreach ($fv in @('CadenceTest\sub', 'CadenceTest2', 'CadenceTest\', 'CadenceTest/', 'CadenceTest\..', 'CadenceTest:x', 'C:\CadenceTest', '\\?\CadenceTest', 'Cadence Test', '')) {
    $c = RunCli ('--mcp --folder "' + $fv + '"') 20000
    if ($c.Code -eq 0) { $folderBad.Add("'$fv' accepted") }
  }
  Chk ((-not [bool](FolderOrNull '\Cadence')) -and $folderBad.Count -eq 0) '1d: odd --folder values are refused by the server (nothing created, \Cadence still absent)' ($folderBad -join ', ')
  Reset-Agent
}


if ($Sections -contains 2) {
  Head '2 suspension and the switch tools'
  Reset-Agent; New-Session
  # an agent job to attack while suspended, and a runnable one (cmd /c exit 0 only)
  $r = TW 'cadence_save_job' (SaveArgs 'RT susp victim' $CMD '/d /c exit 0' @('0 3 1 1 *') $true); $vid = (JobOf $r).jobId
  $r = TW 'cadence_save_job' (SaveArgs 'RT runner' $CMD '/d /c exit 0' @('0 3 1 1 *') $true); $rid = (JobOf $r).jobId
  Chk ($vid -and $rid) '2: fixtures for the suspension tests exist (agent-made, harmless)' "$vid $rid"
  # 2a. suspended from the CLI: every write tool refuses, nothing changes, reads still work, each refusal is audited
  $vrev = Rev $script:S $vid
  $c = RunCli '--agents suspend --folder CadenceTest'; Chk ($c.Code -eq 0) '2a: CLI suspend' (Clip $c.Out 80)
  $snapS = SnapCT; $a0 = AuditCount
  $calls = @(
    @('cadence_save_job', (SaveArgs 'RT new while suspended')),
    @('cadence_save_job', [ordered]@{ jobId = $vid; expectedRevision = $vrev; name = 'renamed while suspended' }),
    @('cadence_set_job_enabled', @{ jobId = $vid; enabled = $false }),
    @('cadence_run_job_now', @{ jobId = $rid }),
    @('cadence_delete_job', @{ jobId = $vid; confirmName = 'RT susp victim' }))
  $codes = @(); $landed = 0
  foreach ($cl in $calls) { $r = TW $cl[0] $cl[1]; $codes += (Code $r); if ((Code $r) -ne 'suspended') { $landed++ } }
  Chk ($landed -eq 0 -and (SameMap $snapS (SnapCT))) '2a: while suspended, save/rename/enable/run/delete are all refused with code suspended; task XML unchanged' (($codes -join ',') + ' ' + (DiffMap $snapS (SnapCT)))
  $a1 = AuditCount; Chk (($a1 - $a0) -eq 5) '2a: each of the 5 refused write attempts left exactly one audit line' ("new lines: " + ($a1 - $a0))
  $r = T $script:S 'cadence_get_status' @{}; Chk ((SCont $r).agents.suspended -eq $true) '2a: reads work while suspended and say so (get_status)' (Clip (Msg $r) 120)
  $r = T $script:S 'cadence_get_job' @{ jobId = $vid }; Chk ((Code $r) -eq 'ok') '2a: get_job works while suspended' ''
  # suspension also holds for the agent job that is already past parsing: a long-lived session picks it up on the NEXT call (no cache)
  # 2a'. fail-closed: the stored value has the wrong type
  $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($RegPath)
  $kinds = @(@('banana', [Microsoft.Win32.RegistryValueKind]::String), @([byte[]](1, 2, 3), [Microsoft.Win32.RegistryValueKind]::Binary), @(7, [Microsoft.Win32.RegistryValueKind]::DWord), @('1', [Microsoft.Win32.RegistryValueKind]::String), @('0', [Microsoft.Win32.RegistryValueKind]::String), @(0L, [Microsoft.Win32.RegistryValueKind]::QWord))
  $fc = New-Object System.Collections.Generic.List[string]
  foreach ($kv in $kinds) {
    $k.DeleteValue('Suspended', $false); $k.SetValue('Suspended', $kv[0], $kv[1])
    $r = TW 'cadence_set_job_enabled' @{ jobId = $vid; enabled = $false }
    if ((Code $r) -ne 'suspended') { $fc.Add($kv[1].ToString() + '=' + $(if ($kv[0] -is [byte[]]) { 'bytes' } else { $kv[0] }) + ' -> ' + (Code $r)) }
  }
  $k.Close()
  Chk ($fc.Count -eq 0) '2a: an unreadable or wrongly typed Suspended value fails closed (String banana/1/0, Binary, DWORD 7, QWORD 0)' ($fc -join '; ')
  # key deleted = not suspended (by design)
  [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($RegPath, $false)
  $r = TW 'cadence_set_job_enabled' @{ jobId = $vid; enabled = $false }; Note '2a: with the registry key absent agents are allowed (by design, PLAN 12.4.2)' (Code $r)
  $c = RunCli '--agents resume --folder CadenceTest'

  # 2b. the race: the flag flips from outside while several clients hammer all four write tools
  $nSrv = 4; $hs = @(); for ($i = 0; $i -lt $nSrv; $i++) { $hs += ,(Start-Mcp ('hammer' + $i)) }
  $cyc = 14
  $runStamp = { $t = ($svc.GetFolder('\CadenceTest')).GetTask('RT runner'); [string]$t.LastRunTime.Ticks }
  $state = @(); for ($i = 0; $i -lt $nSrv; $i++) { $state += ,@{ Pending = $null; N = 0; Last = [DateTime]::MinValue; Sent = 0; Created = $null; Id = 1000 + $i * 100000 } }
  $stats = @{ ok = 0; suspended = 0; other = 0; landedOn = 0; byCode = @{} }
  $pump = {
    param([int]$ms, [bool]$on, [int]$capPerSrv)
    $end = [DateTime]::UtcNow.AddMilliseconds($ms)
    foreach ($x in $state) { $x.Sent = 0 }
    while ([DateTime]::UtcNow -lt $end) {
      for ($i = 0; $i -lt $nSrv; $i++) {
        $st = $state[$i]; $sv = $hs[$i]
        if ($sv.Pending -and $sv.Pending.IsCompleted) {
          $line = $sv.Pending.Result; $sv.Pending = $null; $AllText.Add([string]$line)
          $o = Parse $line; $c = Code $o
          if ($c -eq 'ok') { $stats.ok++; $js = SCont $o; if ($js.outcome -eq 'created' -and $js.job) { $st.Created = $js.job.jobId; $st.CreatedName = $js.job.name } } elseif ($c -eq 'suspended') { $stats.suspended++ } else { $stats.other++ }
          $stats.byCode[$c] = 1 + [int]$stats.byCode[$c]
        }
        if (-not $sv.Pending -and ([DateTime]::UtcNow - $st.Last).TotalMilliseconds -ge 12 -and ($on -or $st.Sent -lt $capPerSrv)) {
          $n = $st.N++; $st.Sent++; $st.Last = [DateTime]::UtcNow; $st.Id++
          switch ($n % 4) {
            0 { $a = CallJson 'cadence_set_job_enabled' ('{"jobId":"' + $vid + '","enabled":' + ($(if ($n % 8 -eq 0) { 'true' } else { 'false' })) + '}') $st.Id }
            1 { $a = CallJson 'cadence_save_job' (J (SaveArgs ("RT race $i n$n"))) $st.Id }
            2 { if ($st.Created) { $a = CallJson 'cadence_delete_job' ('{"jobId":"' + $st.Created + '","confirmName":' + (($st.CreatedName) | ConvertTo-Json -Compress) + '}') $st.Id; $st.Created = $null } else { $a = CallJson 'cadence_run_job_now' ('{"jobId":"' + $rid + '"}') $st.Id } }
            3 { $a = CallJson 'cadence_run_job_now' ('{"jobId":"' + $rid + '"}') $st.Id }
          }
          SendLine $sv $a
          $sv.Pending = $sv.P.StandardOutput.ReadLineAsync()
        }
      }
      Start-Sleep -Milliseconds 2
    }
  }
  $viol = New-Object System.Collections.Generic.List[string]
  for ($c = 0; $c -lt $cyc; $c++) {
    SwitchRaw 1
    & $pump 450 $true 0                          # grace: a write already past its second check may still complete (documented: milliseconds)
    $sa = SnapCT; $ra = & $runStamp
    & $pump 450 $true 0
    $sb = SnapCT; $rb = & $runStamp
    $d = DiffMap $sa $sb; if ($d -ne '' -or $ra -ne $rb) { $viol.Add("cycle $c while ON: " + $d + ' run:' + $ra + '->' + $rb) }
    SwitchRaw 0
    & $pump 260 $false 2
    if ($c -eq 6) { # refresh the servers: new rate buckets
    }
  }
  SwitchRaw 1; & $pump 600 $true 0; $sa = SnapCT; & $pump 400 $true 0; $sb = SnapCT; if ((DiffMap $sa $sb) -ne '') { $viol.Add('final ON window: ' + (DiffMap $sa $sb)) }
  Chk ($viol.Count -eq 0) ("2b: race: flag held ON in $cyc cycles (450 ms grace) while $nSrv clients hammered save/enable/run/delete: nothing changed while ON") ((($viol | Select-Object -First 3) -join ' | ') + ' replies: ok=' + $stats.ok + ' suspended=' + $stats.suspended + ' other=' + $stats.other)
  Chk ($stats.ok -ge 10) '2b: the hammer was effective (writes did land while the flag was OFF)' ('ok=' + $stats.ok + ' codes=' + (($stats.byCode.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Name + ':' + $_.Value }) -join ','))
  # rapid flapping (a few ms apart): no assertion about individual writes, but state must stay consistent
  $t0 = [DateTime]::UtcNow
  while (([DateTime]::UtcNow - $t0).TotalMilliseconds -lt 3500) { SwitchRaw 1; & $pump 12 $true 0; SwitchRaw 0; & $pump 12 $false 3 }
  SwitchRaw 1; & $pump 600 $true 0; SwitchRaw 0
  foreach ($sv in $hs) { $null = Stop-Mcp $sv 15000 }
  $pr = Probe; $rows = @($pr.jobs | Where-Object { $_.createdBy -like 'agent:*' })
  $badRows = @($rows | Where-Object { $_.sync -ne 'inSync' }); $dup = @($pr.jobs | Group-Object id | Where-Object { $_.Count -gt 1 })
  Chk ($badRows.Count -eq 0 -and $dup.Count -eq 0) '2b: after the rapid flapping every agent job is inSync, no duplicate job id, no orphan' ("agent jobs=" + $rows.Count + ' bad=' + (($badRows | ForEach-Object { $_.name + ':' + $_.sync }) -join ',') + ' dup=' + $dup.Count)
  $tasks = @((SnapCT).Keys | Where-Object { $_ -notin @('\CadenceTest\RT owner job', '\CadenceTest\RT locked job', '\CadenceTest\RT foreign') })
  Chk ($tasks.Count -eq $rows.Count) '2b: task count equals job count (no half-created task left)' ("tasks=" + $tasks.Count + ' jobs=' + $rows.Count)
  Reset-Agent; New-Session

  # 2c. the switch tools, used by the agent (allowed by the owner's decision)
  $r = T $script:S 'cadence_suspend_agents' @{ reason = 'red-team check' }
  Chk ((Code $r) -eq 'ok' -and (SCont $r).suspended -eq $true -and (SwitchState)) '2c: an agent can suspend (allowed by design) and the registry says so' (Clip (Msg $r) 140)
  $r = T $script:S 'cadence_suspend_agents' @{}; Chk ((Code $r) -eq 'ok' -and (SCont $r).changed -eq $false) '2c: suspending twice is idempotent' (Clip (Msg $r) 100)
  $r = T $script:S 'cadence_resume_agents' @{ reason = 'red-team check' }; Chk ((Code $r) -eq 'ok' -and (SCont $r).suspended -eq $false -and -not (SwitchState)) '2c: an agent can resume (allowed by design)' (Clip (Msg $r) 100)
  $ctl = ([string][char]1) + 'a' + ([string][char]0x1B) + '[2J' + ([string][char]0x202E) + 'b' + ([string][char]0x2028) + 'c' + ([string][char]7) + ([string][char]0) + 'd' + ([string][char]0x85) + 'e' + "`r`n" + '{"outcome":"ok"}'
  $r = T $script:S 'cadence_suspend_agents' @{ reason = $ctl }
  $reasonBack = [string](SCont $r).reason
  Chk ((Code $r) -eq 'ok' -and $reasonBack -notmatch '[\x00-\x1f\x7f-\x9f  ‪-‮⁦-⁩]') '2c: a reason with control characters, ESC, bidi, U+2028, NUL, CR/LF is cleaned in the result' (Clip $reasonBack 80)
  $last = AuditTail 1 | Select-Object -Last 1; $ao = AuditParse $last
  Chk ($ao -and $ao.outcome -eq 'suspended' -and $last -notmatch '[\x00-\x1f]' -and $last -notmatch [string][char]0x202E -and $last -notmatch [string][char]0x2028) '2c: the audit line for that suspend is one valid JSON line without control or bidi characters' (Clip $last 200)
  $st = RunCli '--agents status --folder CadenceTest'; $stj = RunCli '--agents status --json --folder CadenceTest'; $lg = RunCli '--agents log --folder CadenceTest'
  $all = $st.Out + $stj.Out + $lg.Out
  $badCp = @([regex]::Matches($all, '[ ---  ‪-‮⁦-⁩]') | ForEach-Object { 'U+{0:X4}' -f [int][char]$_.Value } | Sort-Object -Unique)
  Chk ($badCp.Count -eq 0) '2c: the owner''s terminal output (--agents status, status --json, log) holds no ESC, control or bidi characters from the reason' (($badCp -join ',') + ' status: ' + (Clip ($st.Out -replace '\s+', ' ') 200))
  $r = T $script:S 'cadence_resume_agents' @{ reason = ('x' * 10000) }; Chk ((Code $r) -eq 'invalid_argument') '2c: a 10,000-character reason is refused (invalid_argument), state unchanged' ((Code $r) + ' ' + (Clip (Msg $r) 80) + ' suspended=' + (SwitchState))
  $r = T $script:S 'cadence_resume_agents' @{ reason = ('x' * 200) }; Chk ((Code $r) -eq 'ok') '2c: a 200-character reason is accepted' (Code $r)
  $emoji = [string]::Concat((1..101 | ForEach-Object { [char]::ConvertFromUtf32(0x1F600) }))
  $r = T $script:S 'cadence_suspend_agents' @{ reason = $emoji }; Note '2c: reason of 101 emoji (202 UTF-16 units, over the limit by units, under by characters)' ((Code $r) + ' ' + (Clip (Msg $r) 80))
  $null = T $script:S 'cadence_resume_agents' @{}
  foreach ($bad in @('{"reason":null}', '{"reason":5}', '{"reason":["a"]}', '{"why":"x"}')) {
    $r = TJ $script:S 'cadence_suspend_agents' $bad; Chk ((Code $r) -ne 'ok') ("2c: switch tool with bad arguments $bad is refused") ((Code $r) + ' ' + (Clip (Msg $r) 90))
    if (SwitchState) { $null = T $script:S 'cadence_resume_agents' @{} }
  }
  $r = TJ $script:S 'cadence_suspend_agents' '{"reason":"x","reason":"y"}'; Note '2c: duplicate JSON keys are accepted (last one wins), for the switch tools and all others' ((Code $r) + ' reason in result: ' + (SCont $r).reason)
  if (SwitchState) { $null = T $script:S 'cadence_resume_agents' @{} }
  # the malformed switch call and the audit (Checkpoint D7: malformed switch calls were not logged)
  # [CP-E] measured as calls the log stands for: an identical refusal right before it folds into one line with a count (Phase 23), so a line count of +0 was a false alarm
  $a0 = AuditWeight; $rm = TJ $script:S 'cadence_suspend_agents' '{"reason":5}'; $a1 = AuditWeight
  Chk (($a1 - $a0) -ge 1) '2c: a malformed switch call is audited (a new line, or +1 on the count of an identical line just before it)' ('code ' + (Code $rm) + ', calls logged +' + ($a1 - $a0))
  # rapid flapping through the tools: speed, audit growth, correctness
  $a0 = AuditCount; $sw = [Diagnostics.Stopwatch]::StartNew(); $failed = 0
  for ($i = 0; $i -lt 100; $i++) { $r = T $script:S $(if ($i % 2 -eq 0) { 'cadence_suspend_agents' } else { 'cadence_resume_agents' }) @{ reason = "flap $i" }; if ((Code $r) -ne 'ok') { $failed++ } }
  $sw.Stop(); $a1 = AuditCount
  # [P23] D7: 10 real changes per rolling minute per server, then rate_limited (the state stays as the last allowed change left it: resumed)
  Chk ($failed -ge 40 -and -not (SwitchState)) '2c: 100 agent suspend/resume calls in a row: only 10 real changes, every further change is rate_limited (a call that changes nothing is still ok), final state resumed' ("refused=$failed in " + [int]$sw.ElapsedMilliseconds + ' ms')
  New-Session   # a fresh server has a fresh switch budget
  Note '2c: flapping the switch is limited to 10 changes a minute and audited compactly (Checkpoint D7: closed in Phase 23)' ("100 calls -> " + ($a1 - $a0) + " audit lines, " + [int](100000 / [Math]::Max(1, $sw.ElapsedMilliseconds)) + " calls/s")
  # an agent resuming what the owner suspended: allowed by design; is the owner told?
  $null = RunCli '--agents suspend --folder CadenceTest'; $null = T $script:S 'cadence_resume_agents' @{ reason = 'agent resumed it' }
  $st = RunCli '--agents status --folder CadenceTest'
  Chk ($st.Out -match 'agent') '2c: after an agent resumed what the owner suspended, --agents status tells the owner who did it' (Clip ($st.Out -replace '\s+', ' ') 220)
  $null = RunCli '--agents resume --folder CadenceTest'
  Reset-Agent
}


if ($Sections -contains 3) {
  Head '3 the per-job lock'
  Reset-Agent; New-Session
  $lockAudit0 = AuditCount
  $viewL = SCont (GetJob $script:S $LockId)
  Chk ($viewL.lockedForAgents -eq $true) '3: get_job shows the owner lock (lockedForAgents)' ''
  $revL = $viewL.revision
  $attempts = New-Object System.Collections.Generic.List[object]
  function Att($label, $tool, $json) { $attempts.Add(@{ L = $label; Tool = $tool; Json = $json }) }
  Att 'enable' 'cadence_set_job_enabled' ('{"jobId":"' + $LockId + '","enabled":true}')
  Att 'enable + right revision' 'cadence_set_job_enabled' ('{"jobId":"' + $LockId + '","enabled":true,"expectedRevision":"' + $revL + '"}')
  Att 'disable (it is already off)' 'cadence_set_job_enabled' ('{"jobId":"' + $LockId + '","enabled":false}')
  Att 'rename' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","name":"renamed by agent"}')
  Att 'change program arguments' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","program":{"arguments":"/d /c exit 9"}}')
  Att 'change schedule' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","schedule":{"cron":["0 4 1 1 *"]}}')
  Att 'identical save (the "unchanged" path)' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","name":"' + $LockName + '"}')
  Att 'agentLock:false top level' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","agentLock":false}')
  Att 'agentLock:false in program' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","program":{"agentLock":false}}')
  Att 'agentLock:false in schedule' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","schedule":{"agentLock":false}}')
  Att 'AgentLock:false (case)' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","AgentLock":false}')
  Att 'agentlock:false (case)' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","agentlock":false}')
  Att 'agentLock escape' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","agentLock":false}')
  Att 'agentLock:false on set_job_enabled' 'cadence_set_job_enabled' ('{"jobId":"' + $LockId + '","enabled":true,"agentLock":false}')
  Att 'lockedForAgents:false' 'cadence_save_job' ('{"jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","lockedForAgents":false}')
  Att 'duplicate jobId keys (agent job first)' 'cadence_save_job' ('{"jobId":"zzzzzzzz","jobId":"' + $LockId + '","expectedRevision":"' + $revL + '","name":"dup"}')
  Att 'run now' 'cadence_run_job_now' ('{"jobId":"' + $LockId + '"}')
  Att 'delete with the exact name' 'cadence_delete_job' ('{"jobId":"' + $LockId + '","confirmName":"' + $LockName + '"}')
  Att 'delete with name + spaces' 'cadence_delete_job' ('{"jobId":"' + $LockId + '","confirmName":"  ' + $LockName + '  "}')
  Att 'create a job with the locked job''s name' 'cadence_save_job' (J (SaveArgs $LockName))
  Att 'create a job named like it (case/punctuation)' 'cadence_save_job' (J (SaveArgs 'rt LOCKED job.'))
  $codes = @{}; $unlocked = New-Object System.Collections.Generic.List[string]
  $snapL = SnapCT; $a0 = AuditCount
  foreach ($a in $attempts) {
    $r = TWJ $a.Tool $a.Json
    $c = Code $r; $codes[$a.L] = $c
    $createTry = $a.L -like 'create*'
    if ($createTry) { if ($c -eq 'ok' -and (JobOf $r).taskPath -in @('\CadenceTest\RT locked job')) { $unlocked.Add($a.L + ' -> overwrote') } }
    elseif ($c -eq 'ok') { $unlocked.Add($a.L + ' -> ok: ' + (Clip (Msg $r) 60)) }
    if (-not (LockIntact)) { $unlocked.Add($a.L + ' -> locked task XML CHANGED') }
  }
  Chk ($unlocked.Count -eq 0) ("3: {0} attacks on the locked job (enable/disable, save, rename, schedule, program, agentLock tricks, escapes, case, duplicate keys, run-now, delete): none changed it" -f $attempts.Count) (($unlocked | Select-Object -First 3) -join ' | ')
  $summary = ($attempts | ForEach-Object { $_.L.Substring(0, [Math]::Min(14, $_.L.Length)) + '=' + $codes[$_.L] }) -join '; '
  Note '3: codes per attempt (locked is expected for valid shapes, invalid_argument for unknown properties)' (Clip $summary 600)
  $notLocked = @($attempts | Where-Object { $_.L -notlike 'create*' -and $_.L -notlike 'agent*' -and $_.L -notlike 'AgentLock*' -and $_.L -notlike 'lockedFor*' -and $_.L -notlike '*duplicate*' -and $codes[$_.L] -ne 'locked' })
  Chk ($notLocked.Count -eq 0) '3: every direct operation on the locked job answers with code locked (checked before the revision, before "unchanged")' (($notLocked | ForEach-Object { $_.L + '=' + $codes[$_.L] }) -join ', ')
  $a1 = AuditCount; $writeCalls = @($attempts | Where-Object { $true }).Count
  $createdNames = @(ListNames $script:S | ForEach-Object { $_.name })
  Note '3: audit lines added by the attempts (every attempt, also invalid ones; invalid_argument is audited or not?)' ("attempts=" + $writeCalls + " new lines=" + ($a1 - $a0))
  # delete-and-recreate under the same name: the delete is refused, so the name stays taken
  $r = TW 'cadence_save_job' (SaveArgs $LockName); Chk ((Code $r) -ne 'ok' -or (JobOf $r).taskPath -ne '\CadenceTest\RT locked job') '3: creating a job under the locked job''s name never replaces it' ((Code $r) + ' ' + $(if ((Code $r) -eq 'ok') { (JobOf $r).taskPath }))
  # two-step: an agent job renamed to a name that sanitizes to the locked job's task name
  Reset-Agent
  $r = TW 'cadence_save_job' (SaveArgs 'RT step one'); $sid = (JobOf $r).jobId; $okStep = $true
  foreach ($target in @('RT locked job', 'rt locked job', 'RT  locked   job', 'RT locked job.', 'RT locked job ', 'RT-locked-job', 'RT locked job (2)')) {
    $rv = Rev $script:S $sid
    $r2 = TW 'cadence_save_job' ([ordered]@{ jobId = $sid; expectedRevision = $rv; name = $target })
    if (-not (LockIntact)) { $okStep = $false }
  }
  Chk ($okStep) '3: two-step rename of an agent job through 7 spellings of the locked job''s name: locked task XML never changes' (LockIntact)
  Chk ((SnapCT)['\CadenceTest\RT locked job'] -eq (HashOf $LockXml0)) '3: the locked task is byte-identical at the end of section 3' ''
  # the lock is opt-in: an unlocked owner job is within the agent's reach (by design)
  $null = Owner-Apply 'rtown002' 'RT owner two'
  $c1 = Code (TW 'cadence_set_job_enabled' @{ jobId = 'rtown002'; enabled = $true }); $c2 = Code (TW 'cadence_set_job_enabled' @{ jobId = 'rtown002'; enabled = $false })
  $c3 = Code (TW 'cadence_save_job' ([ordered]@{ jobId = 'rtown002'; expectedRevision = (Rev $script:S 'rtown002'); name = 'RT owner two renamed' }))
  $c4 = Code (DelJob $script:S 'rtown002' 'RT owner two renamed')
  Note '3: the lock is opt-in: an UNLOCKED job made by the owner can be enabled, disabled, renamed and deleted by an agent (by design; lock the jobs you care about)' "enable=$c1 disable=$c2 rename=$c3 delete=$c4"
  Reset-Agent
}


if ($Sections -contains 4) {
  Head '4 program rules'
  Reset-Agent; New-Session
  # payload files (harmless: never executed). The copies prove what a text check cannot see.
  $copyExe = Join-Path $Work 'tool1.exe'; Copy-Item $Exe $copyExe -Force
  $copyCad = Join-Path $Work 'Cadence-copy.exe'; Copy-Item $Exe $copyCad -Force
  $batFile = Join-Path $Work 'job.bat'; [IO.File]::WriteAllText($batFile, "@echo off`r`nexit /b 0`r`n")
  $ps1File = Join-Path $Work 'job.ps1'; [IO.File]::WriteAllText($ps1File, "exit 0`r`n")
  $noExt = Join-Path $Work 'script'; [IO.File]::WriteAllText($noExt, "echo hi`r`n")
  $lnk = Join-Path $Work 'cmdlink.lnk'; try { $ws = New-Object -ComObject WScript.Shell; $sc = $ws.CreateShortcut($lnk); $sc.TargetPath = $CMD; $sc.Save() } catch { }
  $short = ''; try { $fso = New-Object -ComObject Scripting.FileSystemObject; $short = $fso.GetFile($copyExe).ShortPath } catch { }
  $shortCad = ''; try { $shortCad = (New-Object -ComObject Scripting.FileSystemObject).GetFile($Exe).ShortPath } catch { }
  $env:CADENCE_RT_EXEVAR = $Exe
  $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("& '" + $Exe + "' --agents resume --folder CadenceTest"))   # NEVER run: the job is saved disabled
  $PS = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
  # a restart so the server sees CADENCE_RT_EXEVAR
  New-Session
  # expect: 'refuse' = a rule in PLAN 12.5.2 says so; 'accept' = no rule; 'honest' = accepted, and the README states the limit (a text check cannot see it)
  $cases = New-Object System.Collections.Generic.List[object]
  function Case($label, $expect, $path, $args_ = '/d /c exit 0', $env_ = $null, $wd = $null, $why = '') { $cases.Add(@{ L = $label; E = $expect; P = $path; A = $args_; Env = $env_; Wd = $wd; Why = $why }) }
  # --- Cadence itself and the markers (rule 5, CP-R9)
  Case 'Cadence.exe itself' 'refuse' $Exe '--agents status'
  Case 'Cadence.exe itself, no args' 'refuse' $Exe ''
  Case 'copy named Cadence-copy.exe' 'refuse' $copyCad '--agents status'
  Case 'cmd /c "Cadence.exe" --agents resume' 'refuse' $CMD ('/d /c "' + $Exe + '" --agents resume')
  Case 'path from %VAR% that expands to Cadence.exe' 'refuse' '%CADENCE_RT_EXEVAR%' '--agents status'
  Case 'args "--agents resume" via cmd' 'refuse' $CMD '/d /c echo --agents resume'
  Case 'args --mcp' 'refuse' $CMD '/d /c echo --mcp'
  Case 'args caret-split cad^ence.exe' 'refuse' $CMD '/d /c cad^ence.exe'
  Case 'args forward slashes C:/x/Cadence.exe' 'refuse' $CMD '/d /c C:/x/Cadence.exe'
  Case 'args backtick-split --ag`ents' 'refuse' $PS '-NoProfile -Command "& x --ag`ents"'
  Case 'registry marker Software\CadenceTest' 'refuse' 'C:\Windows\System32\reg.exe' 'add HKCU\Software\CadenceTest\Agents /v Suspended /d 0 /f'
  Case 'registry marker with caret HKCU\Soft^ware\Cad^ence' 'refuse' 'C:\Windows\System32\reg.exe' 'add HKCU\Soft^ware\Cad^enceTest\Agents /f'
  Case 'env value carries a marker' 'refuse' $CMD '/d /c exit 0' @{ X = 'Cadence.exe --agents resume' }
  Case 'upper case CADENCE.EXE in args' 'refuse' $CMD '/d /c CADENCE.EXE'
  # --- speed-bump bypasses Checkpoint D listed (honest limits)
  Case 'cmd %COMSPEC:~0,0% hides the marker' 'honest' $CMD ('/d /c "' + ($Exe -replace 'Cadence\.exe$', 'Cade%COMSPEC:~0,0%nce.exe') + '" --a%COMSPEC:~0,0%gents resume') $null $null 'cmd turns %COMSPEC:~0,0% into nothing at run time; the save-time check cannot'
  Case 'PowerShell -EncodedCommand (base64 of "& Cadence.exe --agents resume")' 'honest' $PS ('-NoProfile -EncodedCommand ' + $enc) $null $null 'base64 hides everything'
  Case 'renamed copy tool1.exe with --apply' 'honest' $copyExe '--apply C:\x\job.json --yes --folder CadenceTest' $null $null '--apply is not a marker: a job could change/replace any job, also locked ones'
  Case 'renamed copy tool1.exe with --delete' 'honest' $copyExe '--delete rtlock01 --yes --folder CadenceTest' $null $null 'deletes a LOCKED job'
  Case 'renamed copy tool1.exe with --reapply/--untrack/--run' 'honest' $copyExe '--untrack rtlock01 --yes --folder CadenceTest' $null $null ''
  Case 'copy via 8.3 short name' 'honest' $(if ($short -and $short -ne $copyExe) { $short } else { $copyExe }) '--delete rtlock01 --yes --folder CadenceTest' $null $null ('8.3 name: ' + $short)
  $shortCopy = ''; try { $shortCopy = (New-Object -ComObject Scripting.FileSystemObject).GetFile($copyCad).ShortPath } catch { }
  Case 'Cadence-copy.exe through its 8.3 short name' 'honest' $(if ($shortCopy) { $shortCopy } else { $copyCad }) '--apply C:\x\job.json --yes' $null $null ('short name: ' + $shortCopy)
  Case 'schtasks.exe /delete the locked job (no Cadence involved)' 'honest' 'C:\Windows\System32\schtasks.exe' '/delete /tn "\CadenceTest\RT locked job" /f' $null $null 'Task Scheduler itself: no marker to match'
  Case 'schtasks.exe /change any task' 'honest' 'C:\Windows\System32\schtasks.exe' '/change /tn "\CadenceTest\RT foreign" /disable' $null $null 'reaches foreign tasks'
  Case 'powershell concatenated registry path' 'honest' $PS "-NoProfile -Command Set-ItemProperty ('HKCU:\Soft'+'ware\Cad'+'enceTest\Agents') Suspended 0" $null $null 'string concatenation hides software\cadence'
  Case 'cmd /c with & | < > ^ % arguments' 'honest' $CMD '/d /c exit 0 & echo a | findstr a < nul > nul ^& %OS%' $null $null 'arbitrary command line: a scheduler runs what it is told'
  Case '.bat through cmd /d /s /c' 'accept' $CMD ('/d /s /c ""' + $batFile + '""') $null $null 'the documented way to run a .bat'
  Case 'powershell -File script.ps1' 'accept' $PS ('-NoProfile -ExecutionPolicy Bypass -File "' + $ps1File + '"')
  Case 'environment COMSPEC' 'honest' $CMD '/d /c exit 0' @{ COMSPEC = 'C:\x\evil.exe' } $null 'changes what cmd.exe /c resolves to inside the job'
  Case 'environment PATH' 'honest' $CMD '/d /c exit 0' @{ PATH = 'C:\x' } $null 'changes command resolution'
  Case 'environment PATHEXT' 'honest' $CMD '/d /c exit 0' @{ PATHEXT = '.EXE;.BAT' } $null ''
  Case 'environment PSModulePath' 'honest' $PS '-NoProfile -Command exit 0' @{ PSModulePath = 'C:\x' } $null 'module hijack path'
  Case 'environment DOTNET_STARTUP_HOOKS / COR_PROFILER' 'honest' $CMD '/d /c exit 0' @{ DOTNET_STARTUP_HOOKS = 'C:\x\h.dll'; COR_ENABLE_PROFILING = '1' } $null ''
  # --- program path shape (rules 1-3)
  Case 'bare name cmd.exe' 'refuse' 'cmd.exe'
  Case 'relative .\cmd.exe' 'refuse' '.\cmd.exe'
  Case 'relative ..\Windows\System32\cmd.exe' 'refuse' '..\Windows\System32\cmd.exe'
  Case 'drive-relative C:cmd.exe' 'refuse' 'C:cmd.exe'
  Case 'drive-relative C:Windows\System32\cmd.exe' 'refuse' 'C:Windows\System32\cmd.exe'
  Case 'UNC to the local admin share' 'refuse' '\\localhost\C$\Windows\System32\cmd.exe'
  Case 'extended-length \\?\C:\...' 'refuse' '\\?\C:\Windows\System32\cmd.exe'
  Case 'device path \\.\C:\...' 'refuse' '\\.\C:\Windows\System32\cmd.exe'
  Case 'path 1030 characters' 'refuse' ('C:\' + ('a' * 1026) + '.exe')
  Case 'path 5000 characters' 'refuse' ('C:\' + ('a' * 5000) + '.exe')
  Case 'quote in path' 'refuse' 'C:\Windows\System32\"cmd.exe'
  Case 'newline in path' 'refuse' "C:\Windows\System32\cmd.exe`n"
  Case 'NUL in path' 'refuse' "C:\Windows\System32\cmd.exe`0.txt"
  Case '.bat target' 'refuse' $batFile
  Case '.cmd target' 'refuse' (Join-Path $Work 'x.cmd')
  Case '.ps1 target' 'refuse' $ps1File
  Case '.lnk target' 'refuse' $lnk
  Case 'interpreter-less script (no extension)' 'refuse' $noExt
  Case 'a folder' 'refuse' 'C:\Windows\System32\'
  Case 'a missing .exe' 'refuse' 'C:\Windows\System32\does-not-exist-rt.exe'
  Case 'alternate data stream cmd.exe:x' 'refuse' 'C:\Windows\System32\cmd.exe:x.exe'
  Case 'trailing dots cmd.exe.' 'honest' 'C:\Windows\System32\cmd.exe.' '/d /c exit 0' $null $null 'Windows ignores trailing dots'
  Case 'UPPER CASE .EXE' 'accept' 'C:\WINDOWS\SYSTEM32\CMD.EXE'
  Case 'forward slashes C:/Windows/System32/cmd.exe' 'honest' 'C:/Windows/System32/cmd.exe'
  Case '%SystemRoot%\System32\cmd.exe' 'accept' '%SystemRoot%\System32\cmd.exe'
  Case '%ComSpec%' 'accept' '%ComSpec%' '/d /c exit 0' $null $null 'checked with the SERVER''s environment, runs with the USER''s (D8)'
  Case 'variable that exists only in the server (D8)' 'honest' '%CADENCE_RT_EXEVAR%' '/d /c exit 0' $null $null 'unreachable at run time: the check and the run see different environments'
  # --- arguments, environment, working folder (rules 4, 6, 7)
  Case 'arguments 8001 characters' 'refuse' $CMD ('/d /c ' + ('a' * 7995))
  Case 'arguments 8000 characters' 'accept' $CMD ('/d /c ' + ('a' * 7994))
  Case 'arguments with CR/LF' 'refuse' $CMD "/d /c exit 0`r`nexit 1"
  Case 'arguments with NUL' 'refuse' $CMD "/d /c exit 0`0"
  $e31 = @{}; 1..31 | ForEach-Object { $e31['V' + $_] = 'x' }
  Case 'environment: 31 entries' 'refuse' $CMD '/d /c exit 0' $e31
  Case 'environment: value 2001 characters' 'refuse' $CMD '/d /c exit 0' @{ A = ('v' * 2001) }
  Case 'environment: name "1A"' 'refuse' $CMD '/d /c exit 0' @{ '1A' = 'x' }
  Case 'environment: name with "="' 'refuse' $CMD '/d /c exit 0' @{ 'A=B' = 'x' }
  Case 'environment: name with a space' 'refuse' $CMD '/d /c exit 0' @{ 'A B' = 'x' }
  Case 'environment: name with a NUL' 'refuse' $CMD '/d /c exit 0' @{ ("A" + [char]0 + "B") = 'x' }
  Case 'environment: value with NUL' 'refuse' $CMD '/d /c exit 0' @{ A = ("a" + [char]0 + "b") }
  $dupEnv = New-Object System.Collections.Specialized.OrderedDictionary; $dupEnv.Add('Abc', '1'); $dupEnv.Add('ABC', '2')
  Case 'environment: two names differing in case' 'refuse' $CMD '/d /c exit 0' $dupEnv
  Case 'working folder relative' 'refuse' $CMD '/d /c exit 0' $null '.\x'
  Case 'working folder UNC' 'refuse' $CMD '/d /c exit 0' $null '\\localhost\C$\Windows'
  Case 'working folder missing' 'refuse' $CMD '/d /c exit 0' $null 'C:\no-such-folder-rt'
  Case 'working folder %TEMP%' 'accept' $CMD '/d /c exit 0' $null '%TEMP%'
  $results = @{ refuse_ok = 0; refuse_broke = New-Object System.Collections.Generic.List[string]; honest = New-Object System.Collections.Generic.List[string]; accept_bad = New-Object System.Collections.Generic.List[string]; shape = New-Object System.Collections.Generic.List[string] }
  $snapP = SnapCT
  foreach ($cs in $cases) {
    Reset-Agent
    $prog = [ordered]@{ path = $cs.P; arguments = $cs.A }
    if ($cs.Env) { $prog['environment'] = $cs.Env }
    if ($cs.Wd) { $prog['workingFolder'] = $cs.Wd }
    $a = [ordered]@{ name = 'RT prog case'; schedule = [ordered]@{ cron = @('0 3 1 1 *') }; program = $prog; enabled = $false }
    $r = TW 'cadence_save_job' $a
    $c = Code $r; $accepted = ($c -eq 'ok')
    $proved = ''
    if ($accepted) {
      # prove acceptance from the stored task, never by running it
      $jv = JobOf $r; $xml = TaskXml ([string]$jv.taskPath).Substring('\CadenceTest\'.Length)
      $enabledInXml = $xml -match '<Enabled>false</Enabled>'
      $proved = 'stored path=' + (Clip $jv.program.path 60) + ' enabled=' + $jv.enabled + ' xmlDisabled=' + $enabledInXml
      if ($jv.enabled -ne $false -or -not $enabledInXml) { Broke ("4: accepted case '" + $cs.L + "' is not disabled in the task") $proved }
    }
    switch ($cs.E) {
      'refuse' { if ($accepted) { $results.refuse_broke.Add($cs.L + ' [' + $proved + ']') } else { $results.refuse_ok++ } }
      'accept' { if (-not $accepted) { $results.accept_bad.Add($cs.L + ' -> ' + $c + ' ' + (Clip (Msg $r) 120)) } }
      'honest' { $results.honest.Add($(if ($accepted) { 'ACCEPTED ' } else { 'refused  ' }) + $cs.L + $(if ($cs.Why) { ' (' + $cs.Why + ')' } else { '' })) }
    }
    if (-not $accepted -and $c -notin @('program_rejected', 'invalid_argument', 'schedule_invalid')) { $results.shape.Add($cs.L + ' -> ' + $c) }
    if (-not (ForeignIntact) -or -not (LockIntact)) { Broke ("4: case '" + $cs.L + "' changed the foreign or locked task") '' }
  }
  Reset-Agent
  foreach ($x in $results.refuse_broke) { Broke ('4: a rule says refuse, the server ACCEPTED: ' + $x) '' }
  foreach ($x in $results.accept_bad) { Broke ('4: legitimate program refused: ' + $x) '' }
  Chk ($results.refuse_broke.Count -eq 0) ("4: {0} cases the rules refuse: all refused (cadence/markers, relative, UNC, device, long, quotes/CRLF/NUL, scripts, shortcuts, folders, missing, ADS, args/env/working-folder limits)" -f ($results.refuse_ok + $results.refuse_broke.Count)) ''
  foreach ($h in $results.honest) { if ($h.StartsWith('ACCEPTED')) { Note ('4 honest limit: ' + $h) 'accepted disabled, proved from the task XML, never run' } else { Held ('4 text check caught: ' + $h) '' } }
  if ($results.shape.Count) { Note '4: refusals with an unexpected code' (($results.shape | Select-Object -First 5) -join '; ') }
  Chk ((DiffMap $snapP (SnapCT)) -eq '') '4: after all cases only the three fixtures remain (no job left behind, none changed)' (DiffMap $snapP (SnapCT))
  # what the message says for a variable that expands to a value only the server knows (information leak through the refusal text)
  Reset-Agent
  # The prefix C:\ makes the expanded text pass the shape check, so the message that says "does not exist" is the one that could echo the value.
  $leakCases = @(
    @('program.path C:\%VAR%.exe (does not exist)', (SaveArgs 'RT leak probe' ('C:\%CADENCE_RT_SECRET%.exe'))),
    @('program.path C:\%VAR%.bat (script hint)', (SaveArgs 'RT leak probe' ('C:\%CADENCE_RT_SECRET%.bat'))),
    @('program.path C:\%VAR%.x%VAR2% (extension message)', (SaveArgs 'RT leak probe' ('C:\%CADENCE_RT_SECRET%.x%CADENCE_RT_ENVVAL%'))),
    @('program.path %VAR% alone', (SaveArgs 'RT leak probe' ('%CADENCE_RT_SECRET%'))),
    @('program.workingFolder C:\%VAR% (does not exist)', (SaveArgs 'RT leak probe' $CMD '/d /c exit 0' @('0 3 1 1 *') $false @{ workingFolder = 'C:\%CADENCE_RT_SECRET%' })),
    @('program.workingFolder %VAR% alone', (SaveArgs 'RT leak probe' $CMD '/d /c exit 0' @('0 3 1 1 *') $false @{ workingFolder = '%CADENCE_RT_SECRET%' })))
  foreach ($lc in $leakCases) {
    $r = TW 'cadence_save_job' $lc[1]; $m = Msg $r; $m2 = [string](SCont $r).error.hint
    Chk (($m + $m2) -notmatch [regex]::Escape($Secret) -and ($m + $m2) -notmatch [regex]::Escape($env:CADENCE_RT_ENVVAL)) ('4/8: ' + $lc[0] + ': the refusal text does not contain the server environment value') ((Code $r) + ' ' + (Clip $m 190))
  }
  Reset-Agent
  # what could such a job do? (stated once, from the evidence above)
  Note '4: what an accepted job could do (never executed here)' 'an agent-scheduled job runs as the owner: via a renamed copy of Cadence.exe it could --apply/--delete/--untrack jobs (also locked ones), --agents config (limits); via schtasks it could delete or disable ANY task the owner can; via reg/powershell it could set Suspended=0. The text check only stops the obvious spellings.'
}


if ($Sections -contains 5) {
  Head '5 limits: interval, trigger count, cap, rate, metadata, timeouts'
  Reset-Agent; New-Session
  # 5a. minimum interval: preview and save must agree, and both must refuse everything denser than 5 minutes
  $dense = @(
    @('* * * * *', @('* * * * *')), @('*/1', @('*/1 * * * *')), @('0-59', @('0-59 * * * *')), @('*/2', @('*/2 * * * *')), @('*/4', @('*/4 * * * *')),
    @('*/7 (gap 4 at the hour wrap)', @('*/7 * * * *')), @('0,4', @('0,4 * * * *')), @('0,1 at 9', @('0,1 9 * * *')), @('every minute of hour 9', @('* 9 * * *')),
    @('0-30 at 12', @('0-30 12 * * *')), @('every minute on Mondays', @('* * * * 1')), @('29 Feb', @('* * 29 2 *')), @('20 Dec', @('* * 20 12 *')),
    @('all January', @('* * * 1 *')), @('31 Dec', @('* * 31 12 *')), @('0,1 on 1 Jan', @('0,1 0 1 1 *')), @('*/3 on 1 Jan', @('*/3 * 1 1 *')),
    @('dom OR dow: 1st or Monday', @('* * 1 * 1')), @('dom OR dow 1-7 or Monday', @('* * 1-7 * 1')), @('dom 15 OR Friday every minute', @('* * 15 * 5')),
    @('union gap 1', @('0 9 * * *', '1 9 * * *')), @('union gap 4', @('0 9 * * *', '4 9 * * *')), @('@logon + every minute', @('@logon', '* * * * *')),
    @('ten lines, one gap of 1', @('0 1 * * *', '0 2 * * *', '0 3 * * *', '0 4 * * *', '0 5 * * *', '0 6 * * *', '0 7 * * *', '0 8 * * *', '0 9 * * *', '1 9 * * *')),
    @('hour list dense', @('0,1,2,3 0 * * *')), @('range with step 1-59/2', @('1-59/2 * * * *')), @('every minute Feb 29 only dow', @('* * 29 2 1')),
    @('1st Jan midnight every minute', @('* 0 1 1 *')), @('last-day style 28-31 every minute', @('* * 28-31 2 *')), @('every minute Sunday=7', @('* * * * 7')), @('every minute weekdays names', @('* * * * MON-FRI')))
  $fine = @(@('*/5', @('*/5 * * * *')), @('*/6', @('*/6 * * * *')), @('*/9', @('*/9 * * * *')), @('hourly', @('0 * * * *')), @('0,5', @('0,5 * * * *')), @('@logon', @('@logon')),
    @('29 Feb at 3', @('0 3 29 2 *')), @('weekdays 18:00', @('0 18 * * 1-5')), @('union gap 5', @('0 9 * * *', '5 9 * * *')), @('0 0 1 1 *', @('0 0 1 1 *')))
  $invalid = @(@('six fields', @('* * * * * *')), @('seconds step', @('*/5 * * * * *')), @('@every 1m', @('@every 1m')), @('@reboot', @('@reboot')), @('minute 60', @('60 * * * *')), @('hour 24', @('* 24 * * *')),
    @('day 32', @('* * 32 * *')), @('month 13', @('* * * 13 *')), @('fullwidth digit', @(([string][char]0xFF10) + ' * * * *')), @('letters', @('a b c d e')), @('201 chars', @(('0' * 201))), @('negative step', @('*/-1 * * * *')), @('zero step', @('*/0 * * * *')), @('blank', @('   ')), @('NUL', @("0 0 * * *`0")), @('newline', @("* * * * *`n* * * * *")))
  $viol = New-Object System.Collections.Generic.List[string]; $maxMs = 0; $maxLabel = ''
  $disagree = New-Object System.Collections.Generic.List[string]
  foreach ($grp in @(@('dense', $dense), @('fine', $fine), @('invalid', $invalid))) {
    foreach ($case in $grp[1]) {
      Reset-Agent
      $sw = [Diagnostics.Stopwatch]::StartNew()
      $p = T $script:S 'cadence_preview_schedule' @{ cron = @($case[1]) }
      $pms = $sw.ElapsedMilliseconds; $sw.Restart()
      $r = TW 'cadence_save_job' (SaveArgs 'RT limit probe' $CMD '/d /c exit 0' $case[1] $false)
      $sms = $sw.ElapsedMilliseconds
      if ($pms -gt $maxMs) { $maxMs = $pms; $maxLabel = 'preview ' + $case[0] }; if ($sms -gt $maxMs) { $maxMs = $sms; $maxLabel = 'save ' + $case[0] }
      $saved = ((Code $r) -eq 'ok'); $sc = SCont $p; $mayPrev = $sc.agentsMaySave
      switch ($grp[0]) {
        'dense' { if ($saved) { $viol.Add('ACCEPTED dense ' + $case[0] + ' gap=' + $sc.minimumGapMinutes) } }
        'fine' { if (-not $saved) { $viol.Add('refused fine ' + $case[0] + ' ' + (Code $r) + ' ' + (Clip (Msg $r) 80)) } }
        'invalid' { if ($saved) { $viol.Add('ACCEPTED invalid ' + $case[0]) } }
      }
      if ($grp[0] -ne 'invalid' -and (Code $p) -eq 'ok' -and $null -ne $mayPrev -and ([bool]$mayPrev -ne $saved)) { $disagree.Add($case[0] + ': preview agentsMaySave=' + $mayPrev + ' save=' + (Code $r)) }
    }
  }
  Reset-Agent
  Chk ($viol.Count -eq 0) ("5a: {0} dense schedules refused, {1} sensible ones accepted, {2} invalid ones refused (lists, ranges, steps, dom/dow OR, rare dates, unions, @logon mixes)" -f $dense.Count, $fine.Count, $invalid.Count) (($viol | Select-Object -First 4) -join ' | ')
  Chk ($disagree.Count -eq 0) '5a: cadence_preview_schedule.agentsMaySave always agrees with what cadence_save_job does' (($disagree | Select-Object -First 3) -join ' | ')
  Chk ($maxMs -lt 8000) '5a: no schedule check takes long (time bound)' ("slowest: $maxLabel $maxMs ms")
  # the same limit must hold for a CHANGE of an existing job and for the schedule given as templateId
  $r = TW 'cadence_save_job' (SaveArgs 'RT interval job' $CMD '/d /c exit 0' @('*/10 * * * *') $false); $iid = (JobOf $r).jobId
  $rv = Rev $script:S $iid
  $r2 = TW 'cadence_save_job' ([ordered]@{ jobId = $iid; expectedRevision = $rv; schedule = @{ cron = @('* * * * *') } })
  Chk ((Code $r2) -eq 'schedule_too_frequent') '5a: changing an existing job to every minute is refused' ((Code $r2) + ' ' + (Clip (Msg $r2) 100))
  $r3 = TW 'cadence_save_job' ([ordered]@{ name = 'RT tpl'; schedule = @{ templateId = 'every-15-minutes' }; program = @{ path = $CMD; arguments = '/d /c exit 0' }; enabled = $false })
  Note '5a: template every-15-minutes is accepted' (Code $r3)
  $r4 = TW 'cadence_save_job' ([ordered]@{ name = 'RT tpl2'; schedule = @{ templateId = 'hourly'; cron = @('* * * * *') }; program = @{ path = $CMD; arguments = '/d /c exit 0' }; enabled = $false })
  Chk ((Code $r4) -ne 'ok') '5a: templateId together with cron is refused (cannot smuggle a dense cron next to a template)' ((Code $r4) + ' ' + (Clip (Msg $r4) 90))
  # D6: enabling an owner's dense disabled job
  $null = Owner-Apply 'rtdense1' 'RT owner dense' '* * * * *' $false
  $r = TW 'cadence_set_job_enabled' @{ jobId = 'rtdense1'; enabled = $true }
  if ((Code $r) -eq 'ok') { Note '5a: an agent can turn ON the owner''s disabled every-minute job (finding D6, still open: the 5-minute rule applies to schedules agents set, not to switching on)' ((Outcome $r) + ' enabled=' + (JobOf $r).enabled); $null = TW 'cadence_set_job_enabled' @{ jobId = 'rtdense1'; enabled = $false } } else { Held '5a: enabling the owner''s dense job is refused' ((Code $r) + ' ' + (Clip (Msg $r) 80)) }
  Reset-Agent

  # 5b. trigger count (48)
  $many = @(); 1..10 | ForEach-Object { $k = $_; $many += ("{0} 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23 * * 1,2,3,4,5,6,7" -f ($k * 5)) }
  $variants = @(@('10 lines x 24 hours', $many), @('one line, every 5 minutes across all hours', @('*/5 0-23 * * *')), @('20 weekdays x hours', @('0 8-17 * * 1,2,3,4,5')), @('every 10 min, 8 hours (many triggers?)', @('*/10 8-17 * * *')), @('10 lines x 12 irregular minutes', @(1..10 | ForEach-Object { '1,7,13,19,23,29,31,37,41,43,47,53 ' + $_ + ' * * *' })), @('1 line, 40 irregular minutes', @('1,2,4,5,7,8,10,11,13,14,16,17,19,20,22,23,25,26,28,29,31,32,34,35,37,38,40,41,43,44,46,47,49,50,52,53,55,56,58,59 * * * *')), @('ten different minutes', @('3 * * * *', '13 * * * *', '23 * * * *', '33 * * * *', '43 * * * *', '53 * * * *', '8 */2 * * *', '18 */3 * * *', '28 */4 * * *', '38 */5 * * *')))
  $trigBad = New-Object System.Collections.Generic.List[string]; $trigSeen = @()
  foreach ($v in $variants) {
    Reset-Agent
    $r = TW 'cadence_save_job' (SaveArgs 'RT triggers' $CMD '/d /c exit 0' $v[1] $false)
    if ((Code $r) -eq 'ok') {
      $xml = TaskXml ([string](JobOf $r).taskPath).Substring('\CadenceTest\'.Length); $n = ([regex]::Matches($xml, '<(CalendarTrigger|TimeTrigger|LogonTrigger|BootTrigger)>')).Count
      $trigSeen += ($v[0] + ' -> accepted with ' + $n + ' triggers'); if ($n -gt 48) { $trigBad.Add($v[0] + ' has ' + $n + ' triggers') }
    } else { $trigSeen += ($v[0] + ' -> ' + (Code $r) + ' ' + (Clip (Msg $r) 70)) }
  }
  Reset-Agent
  Chk ($trigBad.Count -eq 0) '5b: no accepted schedule compiles to more than 48 triggers' ($trigBad -join '; ')
  Note '5b: trigger counts per schedule' ($trigSeen -join ' || ')

  # 5c. the agent-job cap: counted live, not by session, name or client
  Reset-Agent; New-Session 'cap client A'
  $made = @(); $firstRefusal = $null; $capCode = ''
  for ($i = 1; $i -le 22; $i++) {
    $r = TW 'cadence_save_job' (SaveArgs ("RT cap $i")); if ((Code $r) -eq 'ok') { $made += (JobOf $r).jobId } else { if (-not $firstRefusal) { $firstRefusal = $i; $capCode = (Code $r) + ' ' + (Clip (Msg $r) 100) } }
  }
  Chk ($made.Count -eq 20 -and $firstRefusal -eq 21) '5c: the 21st agent-created job is refused (limit_reached), 20 exist' ("made=" + $made.Count + " first refusal at $firstRefusal : $capCode")
  New-Session 'cap client B (a different name)'; $r = TW 'cadence_save_job' (SaveArgs 'RT cap other client'); Chk ((Code $r) -eq 'limit_reached') '5c: another client name does not get a new allowance' ((Code $r))
  $r = TW 'cadence_save_job' ([ordered]@{ jobId = $made[0]; expectedRevision = (Rev $script:S $made[0]); name = 'RT cap renamed' }); $r = TW 'cadence_save_job' (SaveArgs 'RT cap after rename'); Chk ((Code $r) -eq 'limit_reached') '5c: create / rename / create does not free a slot' (Code $r)
  $r = TW 'cadence_save_job' (SaveArgs 'RT cap 1'); Chk ((Code $r) -in @('limit_reached', 'name_taken')) '5c: re-creating an existing name is refused' (Code $r)
  $r = DelJob $script:S $made[1] 'RT cap 2'; $r2 = TW 'cadence_save_job' (SaveArgs 'RT cap reborn'); $r3 = TW 'cadence_save_job' (SaveArgs 'RT cap reborn two')
  Chk ((Code $r) -eq 'ok' -and (Code $r2) -eq 'ok' -and (Code $r3) -eq 'limit_reached') '5c: deleting one frees exactly one slot' ((Code $r) + '/' + (Code $r2) + '/' + (Code $r3))
  $st = SCont (T $script:S 'cadence_get_status' @{}); Chk ($st.agents.agentJobs -eq 20) '5c: get_status counts 20 agent jobs' ([string]$st.agents.agentJobs)
  Reset-Agent
  # client names that try to look like the owner: the job must still count as agent-made (byKind agent) and carry agent: in the metadata
  $spoofs = @('', ' ', 'cli', 'app', 'Cadence (the app)', 'agent:evil', 'the owner', ('x' + [char]0x202E + 'y'), ('x' * 500), '"},"createdBy":"cli', "line1`nline2", 'Cadence CLI', ('c' + [char]0x0430 + 'dence'))
  $spoofBad = New-Object System.Collections.Generic.List[string]; $spoofSeen = @()
  foreach ($sp in $spoofs) {
    Reset-Agent
    foreach ($mode in @('name', 'title')) {
      $p = $null
      try {
        $psi = NewPsi '--mcp --folder CadenceTest'; $pp = [System.Diagnostics.Process]::Start($psi); $Procs.Add($pp)
        $ss = @{ P = $pp; In = $pp.StandardInput.BaseStream; Pending = $null; Err = $pp.StandardError.ReadToEndAsync(); Name = 'spoof'; Id = 100 }
        $nm = $(if ($mode -eq 'name') { $sp } else { 'spoofname' }); $ti = $(if ($mode -eq 'title') { $sp } else { $null })
        $ci = '{"name":' + ($nm | ConvertTo-Json -Compress) + ',"version":"1"' + $(if ($null -ne $ti) { ',"title":' + ($ti | ConvertTo-Json -Compress) } else { '' }) + '}'
        $ri = Req $ss ('{"jsonrpc":"2.0","id":"i","method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":' + $ci + '}}')
        SendLine $ss '{"jsonrpc":"2.0","method":"notifications/initialized"}'
        if (-not $ri.result) { $spoofSeen += ((Clip $sp 12) + '/' + $mode + ': init refused'); continue }
        $r = T $ss 'cadence_save_job' (SaveArgs 'RT spoof job'); $jv = JobOf $r
        if ((Code $r) -ne 'ok') { $spoofSeen += ((Clip $sp 12) + '/' + $mode + ': ' + (Code $r)); $null = Stop-Mcp $ss; continue }
        $pj = ProbeJob $jv.jobId
        $okk = ($jv.created.byKind -eq 'agent') -and ([string]$pj.createdBy).StartsWith('agent:') -and ($jv.created.by -notmatch '[\x00-\x1f‮]')
        if (-not $okk) { $spoofBad.Add((Clip $sp 20) + '/' + $mode + ' -> by=' + $jv.created.by + ' kind=' + $jv.created.byKind + ' meta=' + $pj.createdBy) }
        $spoofSeen += ((Clip $sp 10) + '/' + $mode + '=' + (Clip $jv.created.by 28))
        $null = Stop-Mcp $ss
      } catch { $spoofSeen += ('error ' + $_.Exception.Message) }
    }
  }
  Reset-Agent
  Chk ($spoofBad.Count -eq 0) ("5c: {0} spoofed client names/titles (empty, cli, app, owner, agent:, bidi, 500 chars, JSON injection, newline, Cyrillic): every job is recorded as an agent's and counted by the cap" -f $spoofs.Count) (($spoofBad | Select-Object -First 3) -join ' | ')
  Note '5c: how those client names were recorded' (Clip ($spoofSeen -join ' ; ') 500)

  # 5d. rate limits (per server process, in memory)
  New-Session 'rate client'
  $r = TW 'cadence_save_job' (SaveArgs 'RT rate job' $CMD '/d /c exit 0' @('0 3 1 1 *') $false); $rj = (JobOf $r).jobId
  $okn = 0; $first = $null; for ($i = 0; $i -lt 40; $i++) { $r = T $script:S 'cadence_set_job_enabled' @{ jobId = $rj; enabled = ($i % 2 -eq 1) }; if ((Code $r) -eq 'ok') { $okn++ } elseif (-not $first) { $first = $i; $fc = (Code $r) + ' ' + (Clip (Msg $r) 90) } }
  Chk ($okn -le 31 -and $first -ne $null) '5d: the 30-writes-per-minute bucket stops a runaway loop' ("ok=$okn, first refusal at call $first : $fc")
  New-Session 'rate client'; $r = T $script:S 'cadence_set_job_enabled' @{ jobId = $rj; enabled = $true }
  if ((Code $r) -eq 'ok') { Note '5d: restarting the server gives a fresh rate bucket (known: in-memory per process; the cap and minimum interval are enforced live, so this only raises call volume)' '' }
  # many servers at once
  $multi = @(); for ($i = 0; $i -lt 5; $i++) { $multi += ,(Start-Mcp ('multi' + $i)) }
  $tot = 0; foreach ($m in $multi) { for ($i = 0; $i -lt 30; $i++) { $r = T $m 'cadence_set_job_enabled' @{ jobId = $rj; enabled = ($i % 2 -eq 0) }; if ((Code $r) -eq 'ok' -or (Code $r) -eq 'stale_revision') { $tot++ } } }
  foreach ($m in $multi) { $null = Stop-Mcp $m }
  Note '5d: five concurrent servers each get their own bucket' ("$tot write calls accepted in a few seconds (5 x 30)")
  # run-now: one per job per 30 s
  Reset-Agent; New-Session 'rate client'
  $r = TW 'cadence_save_job' (SaveArgs 'RT runner' $CMD '/d /c exit 0' @('0 3 1 1 *') $true); $rid = (JobOf $r).jobId
  $r1 = TW 'cadence_run_job_now' @{ jobId = $rid }; $r2 = TW 'cadence_run_job_now' @{ jobId = $rid }
  Chk ((Code $r1) -eq 'ok' -and (Code $r2) -ne 'ok') '5d: a second run-now of the same job within 30 s is refused' ((Code $r1) + ' / ' + (Code $r2) + ' ' + (Clip (Msg $r2) 80))
  if ((Code $r1) -ne 'ok') { Note '5d: run-now was not accepted (Defender or other)' (Clip (Msg $r1) 160) }
  Start-Sleep -Seconds 4
  $g = SCont (GetJob $script:S $rid); Note '5d: runner result after a few seconds' ([string]$g.lastResult.status + ' exit=' + $g.lastResult.exitCode + ' ' + $g.lastResult.message)

  # 5e. metadata 16 KB, name limits, timeouts
  Reset-Agent
  $meta = @(
    @('args 8000 + 3 x 2000 env', $CMD, ('/d /c ' + ('a' * 7993)), @{ A = ('x' * 2000); B = ('y' * 2000); C = ('z' * 2000) }),
    @('args 8000 + 7 x 2000 env (over 16 KB)', $CMD, ('/d /c ' + ('a' * 7993)), (1..7 | ForEach-Object -Begin { $h = [ordered]@{} } -Process { $h['V' + $_] = ('x' * 2000) } -End { $h })),
    @('30 x 2000 env', $CMD, '/d /c exit 0', (1..30 | ForEach-Object -Begin { $h = [ordered]@{} } -Process { $h['W' + $_] = ('x' * 2000) } -End { $h })),
    @('args all quotes (JSON escaping doubles them)', $CMD, ('"' * 8000), @{}))
  $metaSeen = @(); $metaBad = New-Object System.Collections.Generic.List[string]
  foreach ($m in $meta) {
    Reset-Agent
    $r = TW 'cadence_save_job' (SaveArgs 'RT meta' $m[1] $m[2] @('0 3 1 1 *') $false @{ environment = $m[3] })
    if ((Code $r) -eq 'ok') { $xml = TaskXml 'RT meta'; $doc = ([regex]::Match($xml, '<Documentation>([\s\S]*?)</Documentation>')).Groups[1].Value.Length; $metaSeen += ($m[0] + ' -> accepted, Documentation ' + $doc + ' chars'); if ($doc -gt 16384 + 400) { $metaBad.Add($m[0] + ' stored ' + $doc) } }
    else { $metaSeen += ($m[0] + ' -> ' + (Code $r) + ' ' + (Clip (Msg $r) 70)); if ((SnapCT).Count -ne 3) { $metaBad.Add($m[0] + ' refused but a task appeared') } }
  }
  Reset-Agent
  Chk ($metaBad.Count -eq 0) '5e: the 16 KB metadata limit holds (refused cleanly, nothing half-written, accepted ones stay near the limit)' (($metaBad -join '; ') + ' || ' + ($metaSeen -join ' || '))
  $tvals = @(@('0', '0'), @('1', '1'), @('34560', '34560'), @('34561', '34561'), @('-1', '-1'), @('1.5', '1.5'), @('"60"', '"60"'), @('null', 'null'), @('true', 'true'), @('1e2', '1e2'), @('huge', '99999999999999999999'), @('1e400', '1e400'))
  $tSeen = @(); $tBad = New-Object System.Collections.Generic.List[string]
  foreach ($tv in $tvals) {
    Reset-Agent
    $j = '{"name":"RT timeout","schedule":{"cron":["0 3 1 1 *"]},"program":{"path":"' + ($CMD -replace '\\', '\\') + '","arguments":"/d /c exit 0","timeoutMinutes":' + $tv[1] + '},"enabled":false}'
    $r = TWJ 'cadence_save_job' $j; $c = Code $r
    $tSeen += ($tv[0] + '=' + $c)
    if ($tv[0] -in @('34561', '-1', '1.5', '"60"', 'null', 'true', 'huge', '1e400') -and $c -eq 'ok') { $tBad.Add($tv[0] + ' accepted') }
    if ($tv[0] -in @('0', '1', '34560') -and $c -ne 'ok') { $tBad.Add($tv[0] + ' refused: ' + (Clip (Msg $r) 80)) }
  }
  Reset-Agent
  Chk ($tBad.Count -eq 0) '5e: timeoutMinutes: 0, 1, 34560 accepted; 34561, -1, 1.5, "60", null, true, 1e400, 99999999999999999999 refused' (($tBad -join '; ') + ' || ' + ($tSeen -join ' '))
  Note '5e: 1e2 as timeoutMinutes' (($tSeen | Where-Object { $_ -like '1e2=*' }) -join '')
  $nm = @(@('name 100', ('n' * 100)), @('name 101', ('n' * 101)), @('only punctuation', '!!!'), @('only spaces', '   '), @('emoji only', [char]::ConvertFromUtf32(0x1F600)), @('empty', ''))
  $nSeen = @(); foreach ($n in $nm) { Reset-Agent; $r = TW 'cadence_save_job' (SaveArgs $n[1]); $nSeen += ($n[0] + '=' + (Code $r) + $(if ((Code $r) -eq 'ok') { ' as ' + (Clip (JobOf $r).taskPath 40) } else { '' })) }
  Note '5e: name limits' ($nSeen -join ' ; ')
  Reset-Agent
}


if ($Sections -contains 6) {
  Head '6 concurrency, lost updates, kills mid-write'
  Reset-Agent; New-Session 'conc A'
  $B = Start-Mcp 'conc B'
  $r = TW 'cadence_save_job' (SaveArgs 'RT conc job' $CMD '/d /c exit 0' @('0 3 1 1 *') $false); $xid = (JobOf $r).jobId
  # 6a. two servers, same revision, different changes, at the same moment
  $both = 0; $none = 0; $codesSeen = @{}; $bad6 = New-Object System.Collections.Generic.List[string]
  for ($i = 1; $i -le 10; $i++) {
    $rev = Rev $script:S $xid
    $a1 = CallJson 'cadence_save_job' (J ([ordered]@{ jobId = $xid; expectedRevision = $rev; program = @{ arguments = "/d /c exit $i" } })) 7001
    $a2 = CallJson 'cadence_save_job' (J ([ordered]@{ jobId = $xid; expectedRevision = $rev; program = @{ arguments = "/d /c exit $($i + 50)" } })) 7002
    SendLine $script:S $a1; SendLine $B $a2
    $ra = Parse (ReadOne $script:S 30000); $rb = Parse (ReadOne $B 30000)
    $ca = Code $ra; $cb = Code $rb; $codesSeen["$ca/$cb"] = 1 + [int]$codesSeen["$ca/$cb"]
    if ($ca -eq 'ok' -and $cb -eq 'ok' -and (Outcome $ra) -ne 'unchanged' -and (Outcome $rb) -ne 'unchanged') { $both++ }
    if ($ca -ne 'ok' -and $cb -ne 'ok') { $none++ }
    $pj = ProbeJob $xid; if (-not $pj -or $pj.sync -ne 'inSync') { $bad6.Add("round $i sync=" + $pj.sync) }
  }
  Chk ($both -eq 0 -and $bad6.Count -eq 0) '6a: two servers saving the same revision at once, 10 rounds: never both succeed (no lost update), job stays inSync' ("both=$both none=$none outcomes=" + (($codesSeen.GetEnumerator() | ForEach-Object { $_.Name + 'x' + $_.Value }) -join ',') + ' ' + ($bad6 -join ';'))
  if ($none -gt 0) { Note '6a: rounds where neither writer succeeded (stale_revision/busy for both)' "$none of 10" }
  # explicit stale revision after another writer: the message names who changed it
  $rev = Rev $script:S $xid; $null = T $B 'cadence_set_job_enabled' @{ jobId = $xid; enabled = $true }
  $r = T $script:S 'cadence_save_job' ([ordered]@{ jobId = $xid; expectedRevision = $rev; program = @{ arguments = '/d /c exit 3' } })
  Chk ((Code $r) -eq 'stale_revision') '6a: a stale revision is refused after another agent changed the job' ((Code $r) + ' ' + (Clip (Msg $r) 140))
  $null = T $B 'cadence_set_job_enabled' @{ jobId = $xid; enabled = $false }
  # the CLI (no revision, last writer wins by design) against the agent: sequential and simultaneous
  $rev = Rev $script:S $xid
  $jf = Join-Path $Work 'conc.json'; [IO.File]::WriteAllText($jf, '{"id":"' + $xid + '","name":"RT conc job","enabled":false,"schedule":{"cron":"0 3 1 1 *"},"run":{"exe":"C:\\Windows\\System32\\cmd.exe","args":"/d /c exit 11"}}', $utf8)
  $c = RunCli ('--apply "' + $jf + '" --yes --folder CadenceTest')
  $r = T $script:S 'cadence_save_job' ([ordered]@{ jobId = $xid; expectedRevision = $rev; program = @{ arguments = '/d /c exit 12' } })
  Chk ($c.Code -eq 0 -and (Code $r) -eq 'stale_revision') '6a: an owner change through the CLI makes the agent''s older revision stale (lost update prevented)' ('cli=' + $c.Code + ' agent=' + (Code $r))
  $sim = 0; $simBad = New-Object System.Collections.Generic.List[string]
  for ($i = 1; $i -le 6; $i++) {
    $rev = Rev $script:S $xid
    $p = [System.Diagnostics.Process]::Start((NewPsi ('--apply "' + $jf + '" --yes --folder CadenceTest'))); $Procs.Add($p); $p.StandardInput.Close(); $oo = $p.StandardOutput.ReadToEndAsync(); $ee = $p.StandardError.ReadToEndAsync()
    $r = T $script:S 'cadence_save_job' ([ordered]@{ jobId = $xid; expectedRevision = $rev; program = @{ arguments = "/d /c exit $(20 + $i)" } })
    $null = $p.WaitForExit(60000)
    $pj = ProbeJob $xid; $x = TaskXml ([string]$pj.taskPath).Substring('\CadenceTest\'.Length)
    $valid = $false; try { $d = New-Object System.Xml.XmlDocument; $d.LoadXml($x); $valid = $true } catch { }
    if (-not $pj -or $pj.sync -ne 'inSync' -or -not $valid -or $p.ExitCode -ne 0 -and $p.ExitCode -ne 1) { $simBad.Add("round $i cli=" + $p.ExitCode + " agent=" + (Code $r) + " sync=" + $pj.sync) }
  }
  Chk ($simBad.Count -eq 0) '6a: CLI --apply and the agent save started together, 6 rounds: one valid task, inSync, no half write (the app uses the same write path as the CLI)' (($simBad -join '; '))

  # 6b. two creators of the same name at the same moment
  $dupBad = New-Object System.Collections.Generic.List[string]; $dupSeen = @{}
  for ($i = 1; $i -le 10; $i++) {
    Reset-Agent; $null = T $script:S 'cadence_get_status' @{}
    $nm = "RT race name $i"
    $a1 = CallJson 'cadence_save_job' (J (SaveArgs $nm $CMD '/d /c exit 1')) 7101
    $a2 = CallJson 'cadence_save_job' (J (SaveArgs $nm $CMD '/d /c exit 2')) 7102
    SendLine $script:S $a1; SendLine $B $a2
    $ra = Parse (ReadOne $script:S 30000); $rb = Parse (ReadOne $B 30000)
    $k = (Code $ra) + '/' + (Code $rb); $dupSeen[$k] = 1 + [int]$dupSeen[$k]
    $pr = Probe; $mine = @($pr.jobs | Where-Object { $_.name -eq $nm }); $ids = @($pr.jobs | Group-Object id | Where-Object { $_.Count -gt 1 })
    if ($mine.Count -ne 1 -or $ids.Count -gt 0) { $dupBad.Add("round $i : jobs named '$nm' = " + $mine.Count + ' dupIds=' + $ids.Count + " ($k)") }
  }
  Chk ($dupBad.Count -eq 0) '6b: two clients creating the same name at the same moment, 10 rounds: exactly one job per name, no duplicate job id' (($dupBad | Select-Object -First 3) -join ' | ' + ' outcomes: ' + (($dupSeen.GetEnumerator() | ForEach-Object { $_.Name + 'x' + $_.Value }) -join ','))
  # the cap against two creators (19 existing, two servers create at once)
  Reset-Agent; $null = Stop-Mcp $B; New-Session 'cap racer A'
  $ids19 = @(); for ($i = 1; $i -le 19; $i++) { $r = TW 'cadence_save_job' (SaveArgs "RT capr $i"); if ((Code $r) -eq 'ok') { $ids19 += (JobOf $r).jobId } }
  $B = Start-Mcp 'cap racer B'; $capOver = 0
  for ($i = 1; $i -le 3; $i++) {
    $a1 = CallJson 'cadence_save_job' (J (SaveArgs "RT capr x$i")) 7201; $a2 = CallJson 'cadence_save_job' (J (SaveArgs "RT capr y$i")) 7202
    SendLine $script:S $a1; SendLine $B $a2; $ra = Parse (ReadOne $script:S 30000); $rb = Parse (ReadOne $B 30000)
    $cnt = @((Probe).jobs | Where-Object { $_.createdBy -like 'agent:*' }).Count
    if ($cnt -gt 20) { $capOver = $cnt }
    # back to 19
    foreach ($t in @($svc.GetFolder('\CadenceTest').GetTasks(1))) { if ($t.Name -like 'RT capr [xy]*') { $svc.GetFolder('\CadenceTest').DeleteTask($t.Name, 0) } }
  }
  Chk ($capOver -eq 0 -and $ids19.Count -eq 19) '6b: with 19 of 20 agent jobs, two servers creating at the same moment never exceed 20 (3 rounds)' ("ids=" + $ids19.Count + " max seen over=" + $capOver)
  $null = Stop-Mcp $B; Reset-Agent; New-Session 'kill client'

  # 6c. kill the server at random points during writes
  $rnd = New-Object System.Random 777
  $r = TW 'cadence_save_job' (SaveArgs 'RT kill base' $CMD '/d /c exit 0' @('0 3 1 1 *') $false); $kid = (JobOf $r).jobId
  $nApplied = 0; $nNotApplied = 0; $unaudited = 0; $killBad = New-Object System.Collections.Generic.List[string]; $dupAfterRename = 0; $createdByKill = 0; $n = 0; $delAfter = 0
  $nameIdx = 0
  $rounds = @('create', 'update', 'rename', 'rename', 'delete', 'enable')
  for ($i = 0; $i -lt 30; $i++) {
    $kind = $rounds[$i % $rounds.Count]
    $s2 = Start-Mcp ('killer' + $i)
    $curName = (SCont (GetJob $s2 $kid)).name
    $rev = (SCont (GetJob $s2 $kid)).revision
    switch ($kind) {
      'create' { $nameIdx++; $line = CallJson 'cadence_save_job' (J (SaveArgs "RT kill new $nameIdx")) 9001 }
      'update' { $line = CallJson 'cadence_save_job' (J ([ordered]@{ jobId = $kid; expectedRevision = $rev; program = @{ arguments = "/d /c exit $i" } })) 9001 }
      'rename' { $nameIdx++; $line = CallJson 'cadence_save_job' (J ([ordered]@{ jobId = $kid; expectedRevision = $rev; name = "RT kill ren $nameIdx" })) 9001 }
      'delete' { $nameIdx++; $rr = $null; $rr = T $s2 'cadence_save_job' (SaveArgs "RT kill del $nameIdx"); $did = (JobOf $rr).jobId; $line = CallJson 'cadence_delete_job' ('{"jobId":"' + $did + '","confirmName":"RT kill del ' + $nameIdx + '"}') 9001 }
      'enable' { $line = CallJson 'cadence_set_job_enabled' ('{"jobId":"' + $kid + '","enabled":' + $(if ($i % 2 -eq 0) { 'true' } else { 'false' }) + ',"expectedRevision":"' + $rev + '"}') 9001 }
    }
    SendLine $s2 $line
    Start-Sleep -Milliseconds $rnd.Next(0, 260)
    Kill-Mcp $s2
    Start-Sleep -Milliseconds 150
    # state checks
    $pr = Probe
    $applied = $false; $auditNeedle = $null
    if ($pr) {
      switch ($kind) {
        'create' { $applied = [bool](@($pr.jobs | Where-Object { $_.name -eq "RT kill new $nameIdx" }).Count); $auditNeedle = "RT kill new $nameIdx" }
        'update' { $kx = @($pr.jobs | Where-Object { $_.id -eq $kid })[0]; if ($kx) { $applied = (TaskXml ([string]$kx.taskPath).Substring('\CadenceTest'.Length)) -match ('exit ' + $i + '\W') } }
        'rename' { $applied = [bool](@($pr.jobs | Where-Object { $_.name -eq "RT kill ren $nameIdx" }).Count); $auditNeedle = "RT kill ren $nameIdx" }
        'delete' { $applied = -not [bool](@($pr.jobs | Where-Object { $_.name -eq "RT kill del $nameIdx" }).Count); $auditNeedle = "RT kill del $nameIdx" }
        'enable' { $kx = @($pr.jobs | Where-Object { $_.id -eq $kid })[0]; if ($kx) { $applied = ([bool]$kx.enabled -eq ($i % 2 -eq 0)) } }
      }
    }
    if ($applied) { $nApplied++ } else { $nNotApplied++ }
    if ($applied -and $auditNeedle -and $kind -ne 'delete') { $aud = @((AuditAll) | Where-Object { $_.Contains($auditNeedle) -and $_.Contains('"outcome":"ok"') }).Count; if ($aud -eq 0) { $unaudited++ } }
    if (-not $pr) { $killBad.Add("round $i ($kind): probe unreadable"); continue }
    $orph = @($pr.foreign | Where-Object { $_.taskPath -like '\CadenceTest\*' -and $_.name -ne 'RT foreign' })
    $dups = @($pr.jobs | Group-Object id | Where-Object { $_.Count -gt 1 })
    $dupIds = @($dups | ForEach-Object { $_.Name }); $notSync = @($pr.jobs | Where-Object { $_.sync -ne 'inSync' -and ($dupIds -notcontains $_.id) })
    $badXml = 0; foreach ($t in @($svc.GetFolder('\CadenceTest').GetTasks(1))) { try { $d = New-Object System.Xml.XmlDocument; $d.LoadXml($t.Xml) } catch { $badXml++ } }
    if ($orph.Count -gt 0) { $killBad.Add("round $i ($kind): orphan/foreign-looking task " + ($orph | ForEach-Object { $_.name }) -join ',') }
    if ($badXml -gt 0) { $killBad.Add("round $i ($kind): invalid task XML") }
    if ($dups.Count -gt 0) { if ($kind -eq 'rename') { $dupAfterRename++ } else { $killBad.Add("round $i ($kind): duplicate job id " + ($dups | ForEach-Object { $_.Name }) -join ',') } }
    if ($notSync.Count -gt 0 -and $dups.Count -eq 0) { $killBad.Add("round $i ($kind): not inSync: " + (($notSync | ForEach-Object { $_.name + ':' + $_.sync }) -join ',')) }
    # the next write works
    $s3 = Start-Mcp ('after' + $i)
    $cur = @((Probe).jobs | Where-Object { $_.id -eq $kid })
    $nx = T $s3 'cadence_save_job' (SaveArgs "RT after kill $i"); $nextOk = ((Code $nx) -eq 'ok')
    if (-not $nextOk) { $killBad.Add("round $i ($kind): the next create failed: " + (Code $nx) + ' ' + (Clip (Msg $nx) 80)) }
    # the killed job can still be changed (or the refusal is clean and tells the owner what to do)
    $g = GetJob $s3 $kid
    if ((Code $g) -eq 'ok') { $rv2 = (SCont $g).revision; $up = T $s3 'cadence_save_job' ([ordered]@{ jobId = $kid; expectedRevision = $rv2; program = @{ arguments = '/d /c exit 0' } }); if ((Code $up) -ne 'ok' -and (Code $up) -ne 'not_allowed_in_this_state') { $killBad.Add("round $i ($kind): update after kill -> " + (Code $up) + ' ' + (Clip (Msg $up) 80)) } }
    $null = Stop-Mcp $s3
    # tidy extras (not the base job): created-by-kill jobs and the after-kill job
    foreach ($t in @($svc.GetFolder('\CadenceTest').GetTasks(1))) { if ($t.Name -like 'RT after kill*' -or $t.Name -like 'RT kill new*' -or $t.Name -like 'RT kill del*') { try { $svc.GetFolder('\CadenceTest').DeleteTask($t.Name, 0) } catch { } } }
    # if a rename left a copy, the owner resolves it: do that by deleting the copy task and renaming the original back through COM-free means: just remove the copy (drifted) task so later rounds start clean
    $pr2 = Probe; foreach ($g2 in @($pr2.jobs | Group-Object id | Where-Object { $_.Count -gt 1 })) { $cp = @($g2.Group | Where-Object { $_.sync -eq 'drifted' -and $_.problem -like '*copy*' }); foreach ($c2 in $cp) { try { $svc.GetFolder('\CadenceTest').DeleteTask(([string]$c2.taskPath).Substring('\CadenceTest\'.Length), 0) } catch { } } }
    $baseTask = @((Probe).jobs | Where-Object { $_.id -eq $kid }); if ($baseTask.Count -eq 0) { $r = TW 'cadence_save_job' (SaveArgs 'RT kill base' $CMD '/d /c exit 0' @('0 3 1 1 *') $false); $kid = (JobOf $r).jobId }
  }
  Chk ($killBad.Count -eq 0) '6c: server killed at 30 random points during create/update/rename/delete/enable: no orphan task, no invalid XML, no duplicate id (except renames), always inSync, the next write works' (($killBad | Select-Object -First 4) -join ' | ')
  Note '6c: how the 30 kills fell (the write was already done / not yet done when the server died)' ("applied=$nApplied not applied=$nNotApplied; applied but with no audit line (killed between the write and the log)=$unaudited")
  if ($dupAfterRename -gt 0) { Note '6c: a kill between a rename''s create and delete leaves a copy (known residual risk, CP-D: the owner resolves it in the app)' ("copies left in $dupAfterRename of 10 rename kills; the copy is flagged 'copy' (drifted) and refuses agent writes") } else { Held '6c: no rename kill left a copy in 10 tries (window is very short)' '' }
  Reset-Agent
}


if ($Sections -contains 7) {
  Head '7 protocol and resource abuse against the write tools'
  Reset-Agent; New-Session 'proto'
  function Alive($s) { $r = Req $s '{"jsonrpc":"2.0","id":"alive","method":"ping"}' 15000; ($r -ne $null -and $null -ne $r.result) }
  $snap7 = SnapCT
  $big = 'A' * 10000
  # 7a. oversized and hostile values in every write argument
  $cases = @(
    @('cron array of 10,000 lines', 'cadence_save_job', ('{"name":"x","schedule":{"cron":[' + ((1..10000 | ForEach-Object { '"0 3 1 1 *"' }) -join ',') + ']},"program":{"path":"' + ($CMD -replace '\\', '\\') + '"},"enabled":false}')),
    @('name 10,000 chars', 'cadence_save_job', ('{"name":"' + $big + '","schedule":{"cron":["0 3 1 1 *"]},"program":{"path":"' + ($CMD -replace '\\', '\\') + '"},"enabled":false}')),
    @('name 1 MB', 'cadence_save_job', ('{"name":"' + ('B' * 1000000) + '","schedule":{"cron":["0 3 1 1 *"]},"program":{"path":"' + ($CMD -replace '\\', '\\') + '"},"enabled":false}')),
    @('arguments 5 MB', 'cadence_save_job', ('{"name":"x","schedule":{"cron":["0 3 1 1 *"]},"program":{"path":"' + ($CMD -replace '\\', '\\') + '","arguments":"' + ('C' * 5000000) + '"},"enabled":false}')),
    @('environment with 20,000 keys', 'cadence_save_job', ('{"name":"x","schedule":{"cron":["0 3 1 1 *"]},"program":{"path":"' + ($CMD -replace '\\', '\\') + '","environment":{' + ((1..20000 | ForEach-Object { '"K' + $_ + '":"v"' }) -join ',') + '}},"enabled":false}')),
    @('jobId 10,000 chars', 'cadence_delete_job', ('{"jobId":"' + $big + '","confirmName":"x"}')),
    @('confirmName 1 MB', 'cadence_delete_job', ('{"jobId":"aaaaaaaa","confirmName":"' + ('D' * 1000000) + '"}')),
    @('reason 1 MB', 'cadence_suspend_agents', ('{"reason":"' + ('E' * 1000000) + '"}')),
    @('nested 5,000 deep in a property', 'cadence_save_job', ('{"name":"x","schedule":{"cron":["0 3 1 1 *"]},"program":{"path":"' + ($CMD -replace '\\', '\\') + '","environment":' + ('{"a":' * 5000) + '1' + ('}' * 5000) + '},"enabled":false}')),
    @('nested 100,000 arrays as arguments', 'cadence_set_job_enabled', ('{"jobId":"aaaaaaaa","enabled":' + ('[' * 100000) + (']' * 100000) + '}')),
    @('U+2028 / NUL / bidi in arguments and env', 'cadence_save_job', ('{"name":"x","schedule":{"cron":["0 3 1 1 *"]},"program":{"path":"' + ($CMD -replace '\\', '\\') + '","arguments":"/d /c exit 0 ‮\u0000","environment":{"A":"b \u0000"}},"enabled":false}')),
    @('duplicate keys: name twice', 'cadence_save_job', ('{"name":"first","name":"second","schedule":{"cron":["0 3 1 1 *"]},"program":{"path":"' + ($CMD -replace '\\', '\\') + '"},"enabled":false}')),
    @('property name case JOBID', 'cadence_get_job', '{"JOBID":"aaaaaaaa"}'),
    @('__proto__ and constructor', 'cadence_save_job', '{"__proto__":{"x":1},"constructor":{"y":2},"name":"x"}'),
    @('arguments is an array', 'cadence_save_job', '[1,2,3]'),
    @('arguments is a string', 'cadence_save_job', '"hello"'),
    @('arguments is null', 'cadence_save_job', 'null'))
  $a0 = AuditCount; $sw7 = [Diagnostics.Stopwatch]::StartNew(); $bad7 = New-Object System.Collections.Generic.List[string]; $seen7 = @()
  foreach ($cs in $cases) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = TWJ $cs[1] $cs[2] 60000
    $c = Code $r; $seen7 += ($cs[0] + '=' + $c + '/' + $sw.ElapsedMilliseconds + 'ms')
    if ($c -eq 'none') { $bad7.Add($cs[0] + ': no answer') }
    elseif ($c -eq 'ok' -and $cs[0] -notlike 'duplicate*' -and $cs[0] -notlike 'U+2028*' -and $cs[0] -notlike 'property name*') { $bad7.Add($cs[0] + ': accepted') }
    if (-not (Alive $script:S)) { $bad7.Add($cs[0] + ': server not responding afterwards'); New-Session 'proto' }
  }
  Chk ($bad7.Count -eq 0) ("7a: {0} oversized / nested / malformed / duplicated arguments: every one answered, none accepted wrongly, server alive after each" -f $cases.Count) (($bad7 | Select-Object -First 3) -join ' | ')
  Note '7a: codes and times' (Clip ($seen7 -join ' ; ') 700)
  Reset-Agent
  $dup = SnapCT; Note '7a: tasks created by the cases that were accepted (duplicate keys, U+2028 payload)' ((DiffMap $snap7 $dup))
  Reset-Agent
  # 7b. invalid UTF-8 and odd framing around a write
  $good = '{"jsonrpc":"2.0","id":8001,"method":"tools/call","params":{"name":"cadence_save_job","arguments":{"name":"RT bytes '
  $tail = '","schedule":{"cron":["0 3 1 1 *"]},"program":{"path":"' + ($CMD -replace '\\', '\\') + '"},"enabled":false}}}'
  $bytes = New-Object System.Collections.Generic.List[byte]; $bytes.AddRange($utf8.GetBytes($good)); $bytes.AddRange([byte[]](0xFF, 0xFE, 0xC0, 0xAF, 0xED, 0xA0, 0x80)); $bytes.AddRange($utf8.GetBytes($tail)); $bytes.Add(10)
  SendBytes $script:S $bytes.ToArray(); $r = Parse (ReadOne $script:S 20000)
  Chk ($r -ne $null -and (Alive $script:S)) '7b: invalid UTF-8 (FF FE C0 AF and a lone surrogate) inside a write call: answered, server alive' ((Code $r) + ' ' + (Clip (Msg $r) 80))
  $made = @((SnapCT).Keys | Where-Object { $_ -like '*RT bytes*' }); if ($made.Count) { $xml = TaskXml (($made[0]) -replace '^\\CadenceTest\\', ''); $okx = $true; try { $d = New-Object System.Xml.XmlDocument; $d.LoadXml($xml) } catch { $okx = $false }; Chk $okx '7b: the task made from an invalid-UTF-8 name has valid XML' $made[0] }
  Reset-Agent
  # a tools/call sent as a notification (no id): is the write executed, and is it audited?
  $a0 = AuditCount
  SendLine $script:S ('{"jsonrpc":"2.0","method":"tools/call","params":{"name":"cadence_save_job","arguments":' + (J (SaveArgs 'RT notify write')) + '}}')
  Start-Sleep -Milliseconds 1500; $a1 = AuditCount; $created = @((SnapCT).Keys | Where-Object { $_ -like '*RT notify write*' }).Count
  if ($created -eq 0) { Held '7b: a tools/call sent as a notification (no id) is not executed' "audit +$($a1 - $a0)" } else { Chk (($a1 - $a0) -ge 1) '7b: a tools/call sent as a notification IS executed; it must then be audited' ("created=$created audit lines +" + ($a1 - $a0)) }
  Chk (Alive $script:S) '7b: server alive after the notification' ''
  Reset-Agent
  # cancellation of a write in flight: either done or not done, never half
  SendLine $script:S (CallJson 'cadence_save_job' (J (SaveArgs 'RT cancel me')) 8100)
  SendLine $script:S '{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":8100,"reason":"changed my mind"}}'
  $r = Parse (ReadOne $script:S 20000); Start-Sleep -Milliseconds 500
  $pr = Probe; $mine = @($pr.jobs | Where-Object { $_.name -eq 'RT cancel me' })
  Chk ($mine.Count -le 1 -and ($mine.Count -eq 0 -or $mine[0].sync -eq 'inSync') -and (Alive $script:S)) '7b: cancelling a write in flight leaves nothing half-done (job absent or complete and inSync)' ("reply=" + (Code $r) + " jobs=" + $mine.Count)
  Reset-Agent
  foreach ($tn in @('CADENCE_SAVE_JOB', 'cadence_save_job ', ('cadence_save_job' + [char]0), ('cadence_s' + [char]0x0430 + 've_job'), 'cadence_save_job/../cadence_delete_job', '')) {
    $r = Req $script:S ('{"jsonrpc":"2.0","id":8200,"method":"tools/call","params":{"name":' + ($tn | ConvertTo-Json -Compress) + ',"arguments":' + (J (SaveArgs 'RT case probe')) + '}}')
    if ((Code $r) -eq 'ok') { Broke '7b: a mangled tool name reached cadence_save_job' (Clip $tn 40) }
  }
  Held '7b: mangled tool names (upper case, trailing space, NUL, Cyrillic a, path) never reach a write tool' ''
  Reset-Agent

  # 7c. many concurrent writes (rate limiter and queues): 200 pipelined creates
  New-Session 'flood'
  $wsBefore = ProcWorkingSet $script:S; $sw = [Diagnostics.Stopwatch]::StartNew()
  $sb = New-Object System.Text.StringBuilder
  for ($i = 1; $i -le 200; $i++) { [void]$sb.Append((CallJson 'cadence_save_job' (J (SaveArgs ("RT burst $i"))) (9300 + $i))); [void]$sb.Append("`n") }
  SendBytes $script:S ($utf8.GetBytes($sb.ToString()))
  $got = @{}; $codesB = @{}; $t0 = [DateTime]::UtcNow
  while ($got.Count -lt 200 -and ([DateTime]::UtcNow - $t0).TotalSeconds -lt 90) { $l = ReadOne $script:S 20000; if ($l -eq '<<timeout>>' -or $null -eq $l) { break }; $o = Parse $l; if ($o -and $o.id) { $got[[string]$o.id] = 1 + [int]$got[[string]$o.id]; $c = Code $o; $codesB[$c] = 1 + [int]$codesB[$c] } }
  $dupAns = @($got.GetEnumerator() | Where-Object { $_.Value -gt 1 }).Count
  $tasks = @((SnapCT).Keys | Where-Object { $_ -like '*RT burst*' }).Count
  Chk ($got.Count -eq 200 -and $dupAns -eq 0) '7c: 200 pipelined write requests: every one answered exactly once' ("answered=" + $got.Count + " duplicates=$dupAns in " + [int]$sw.Elapsed.TotalSeconds + 's')
  Chk ($tasks -le 20) '7c: the burst created no more than the 20-job cap (30/min limiter and cap both hold)' ("tasks=$tasks codes=" + (($codesB.GetEnumerator() | ForEach-Object { $_.Name + ':' + $_.Value }) -join ','))
  $wsAfter = ProcWorkingSet $script:S; Chk (($wsAfter - $wsBefore) -lt 150MB) '7c: server memory stays bounded during the burst' ("working set " + [int]($wsBefore / 1MB) + ' -> ' + [int]($wsAfter / 1MB) + ' MB')
  Reset-Agent
  # a client that never reads
  $nr = Start-Mcp 'never reads'; $ws0 = ProcWorkingSet $nr
  $sb = New-Object System.Text.StringBuilder; for ($i = 1; $i -le 4000; $i++) { [void]$sb.Append((CallJson 'cadence_get_status' '{}' (9800000 + $i))); [void]$sb.Append("`n") }
  $payload = $utf8.GetBytes($sb.ToString())
  $sent = 0; $wr = $null
  try { $wr = $nr.In.WriteAsync($payload, 0, $payload.Length) } catch { }
  Start-Sleep -Seconds 4; $ws1 = ProcWorkingSet $nr
  $nrAlive = -not $nr.P.HasExited
  # now a third party checks the machine is not wedged: another server answers
  $other = Start-Mcp 'other'; $okOther = Alive $other; $null = Stop-Mcp $other
  try { $nr.In.Close() } catch { }
  $exited = $nr.P.WaitForExit(15000); if (-not $exited) { try { $nr.P.Kill() } catch { } }
  Chk (($ws1 - $ws0) -lt 200MB -and $okOther) '7c: a client that never reads 4,000 replies: memory bounded, other servers unaffected' ("working set " + [int]($ws0 / 1MB) + ' -> ' + [int]($ws1 / 1MB) + " MB; alive=$nrAlive; exitedAfterStdinClose=$exited")
  if (-not $exited) { Note '7c: the never-reading server did not exit within 15 s after its stdin closed (it is blocked writing to a pipe nobody reads; killed by this script)' '' }
  Kill-Mcp $nr
  # 7d. a single huge line
  $r = Req $script:S ('{"jsonrpc":"2.0","id":8300,"method":"tools/call","params":{"name":"cadence_save_job","arguments":{"name":"' + ('Z' * 12000000) + '"}}}') 60000
  Chk ($r -ne $null -and (Code $r) -ne 'ok' -and (Alive $script:S)) '7d: one 12 MB request line is refused and the server keeps working' ((Code $r) + ' ' + (Clip (Msg $r) 100))
  Reset-Agent
}


if ($Sections -contains 8) {
  Head '8 information leaks'
  Reset-Agent; New-Session 'leaks'
  $jobSecret = 'RT_JOBSECRET_' + [guid]::NewGuid().ToString('N')
  $wdDir = Join-Path $Work $Secret; [void][IO.Directory]::CreateDirectory($wdDir)
  $env:CADENCE_RT_WD = $wdDir
  New-Session 'leaks'      # a server that sees CADENCE_RT_WD
  $leakPattern = 'S-1-5-21|' + [regex]::Escape($env:USERDOMAIN) + '\\|' + [regex]::Escape($env:USERNAME) + '|AppData|LOCALAPPDATA|\\Users\\|System32\\Tasks|Windows\\Tasks|   at Cadence|System\.[A-Z][a-z]+Exception|HRESULT|0x8007'
  $texts = New-Object System.Collections.Generic.List[string]
  function Collect($r) { if ($null -ne $r) { $texts.Add((Msg $r)); $sc2 = SCont $r; if ($sc2 -and $sc2.error) { $texts.Add([string]$sc2.error.hint) } } }
  # 8a. values the agent cannot know must not come back, even when it names them with %VAR%
  $r = TW 'cadence_save_job' (SaveArgs 'RT leak job %CADENCE_RT_SECRET%' $CMD '/d /c echo %CADENCE_RT_SECRET% %CADENCE_RT_ENVVAL%' @('0 3 1 1 *') $false @{ environment = [ordered]@{ API_KEY = $jobSecret; OTHER = '%CADENCE_RT_SECRET%' }; workingFolder = '%CADENCE_RT_WD%' })
  Collect $r
  $txt = (Msg $r)
  Chk ((Code $r) -eq 'ok' -and $txt -notmatch [regex]::Escape($Secret) -and $txt -notmatch [regex]::Escape($jobSecret) -and $txt -notmatch [regex]::Escape($env:CADENCE_RT_ENVVAL)) '8a: save result with %VAR% in name, arguments, environment and working folder holds no server environment value and no job environment value' ((Code $r) + ' ' + (Clip $txt 160))
  $jid = (JobOf $r).jobId
  $wf = (JobOf $r).program.workingFolder
  Chk ($wf -notmatch [regex]::Escape($Secret)) '8a: the working folder is shown as written (%CADENCE_RT_WD%), not expanded into a path that names a server-only value' ([string]$wf)
  $g = GetJob $script:S $jid; Collect $g
  Chk ((Msg $g) -notmatch [regex]::Escape($jobSecret) -and (Msg $g) -notmatch [regex]::Escape($Secret)) '8a: get_job shows environment NAMES only (no values)' ('environmentNames=' + (((SCont $g).program.environmentNames) -join ','))
  $l = T $script:S 'cadence_list_jobs' @{}; Collect $l
  Chk ((Msg $l) -notmatch [regex]::Escape($jobSecret) -and (Msg $l) -notmatch [regex]::Escape($Secret)) '8a: list_jobs shows no environment values' ''
  $st = T $script:S 'cadence_get_status' @{}; Collect $st
  Chk ((Msg $st) -notmatch [regex]::Escape($Secret)) '8a: get_status shows no environment values' ''
  # an update that fails must not echo the previous values either
  $rv = Rev $script:S $jid
  $r = TW 'cadence_save_job' ([ordered]@{ jobId = $jid; expectedRevision = '000000000000'; program = @{ environment = @{ API_KEY = $jobSecret + 'x' } } }); Collect $r
  Chk ((Msg $r) -notmatch [regex]::Escape($jobSecret)) '8a: a refused update (stale revision) does not echo environment values' ((Code $r) + ' ' + (Clip (Msg $r) 100))
  # the audit log and the owner's CLI views
  $alog = (AuditAll) -join "`n"; $cl = (RunCli '--agents log --folder CadenceTest').Out
  Chk ($alog -notmatch [regex]::Escape($jobSecret) -and $alog -notmatch [regex]::Escape($Secret) -and $cl -notmatch [regex]::Escape($jobSecret)) '8a: the audit log (file and --agents log) holds no environment value' ''
  $null = DelJob $script:S $jid ('RT leak job %CADENCE_RT_SECRET%')
  [IO.Directory]::Delete($wdDir, $true)

  # 8b. run-output retrieval: after a delete, for ids that never existed, for another job's run id, path-like ids
  Reset-Agent; New-Session 'leaks'
  $r = TW 'cadence_save_job' (SaveArgs 'RT out job A' $CMD '/d /c exit 0' @('0 3 1 1 *') $true); $ja = (JobOf $r).jobId
  $r = TW 'cadence_save_job' (SaveArgs 'RT out job B' $CMD '/d /c exit 0' @('0 3 1 1 *') $false); $jb = (JobOf $r).jobId
  $r = TW 'cadence_run_job_now' @{ jobId = $ja }; $started = ((Code $r) -eq 'ok'); Collect $r
  $runId = $null; $t0 = [DateTime]::UtcNow
  while ($started -and -not $runId -and ([DateTime]::UtcNow - $t0).TotalSeconds -lt 30) { Start-Sleep -Milliseconds 1500; $gg = SCont (T $script:S 'cadence_get_job' @{ jobId = $ja; recentRuns = 5 }); $ru = @($gg.recentRuns); if ($ru.Count -gt 0) { $runId = $ru[0].runId } }
  if (-not $runId) { Note '8b: no run record appeared for a cmd /c exit 0 job within 30 s (launch blocked by Windows/Defender, or slow); the run-output checks use a made-up run id' ((Code $r) + ' ' + (Clip (Msg $r) 100)); $runId = '20260101-000000-000' }
  $probes = @(
    @('own run, own job', $ja, $runId), @('another job''s run id', $jb, $runId), @('job id never existed', 'zzzzzzzz', $runId), @('run id that has no record', $ja, '20260101-000000-001'),
    @('path-like run id', $ja, '..\..\..\Windows\win.ini'), @('run id with slash', $ja, '20260101-000000-000/../x'), @('run id of 10,000 chars', $ja, ('1' * 10000)), @('job id with traversal', '..\..\x', $runId), @('NUL device as job id', 'nul', $runId))
  $pathLeak = New-Object System.Collections.Generic.List[string]
  foreach ($pb in $probes) {
    $rj = $pb[1] | ConvertTo-Json -Compress; $rr = $pb[2] | ConvertTo-Json -Compress
    $r = TJ $script:S 'cadence_get_run_output' ('{"jobId":' + $rj + ',"runId":' + $rr + ',"maxChars":200}'); Collect $r
    if ((Msg $r) -match $leakPattern) { $pathLeak.Add($pb[0] + ': ' + (Clip (Msg $r) 120)) }
  }
  $null = DelJob $script:S $ja 'RT out job A'
  $r = T $script:S 'cadence_get_run_output' @{ jobId = $ja; runId = $runId }; Collect $r
  Chk ((Code $r) -ne 'ok' -or -not (SCont $r).text) '8b: run output of a DELETED job is not returned' ((Code $r) + ' ' + (Clip (Msg $r) 100))
  Chk ($pathLeak.Count -eq 0) '8b: run-output errors (other job''s run id, unknown job, no record, path-like ids, 10,000 chars, NUL device) leak no machine paths, user names, SIDs or stack text' (($pathLeak | Select-Object -First 2) -join ' | ')
  # 8c. error and problem texts of every kind of broken job
  Reset-Agent
  $null = Owner-Apply 'rtleakd1' 'RT leak drift'; $null = Owner-Apply 'rtleakm1' 'RT leak damaged'; $null = Owner-Apply 'rtleakn1' 'RT leak newer'; $null = Owner-Apply 'rtleakc1' 'RT leak copysrc'
  function Rereg2($n, $x) { $null = (EnsureCT).RegisterTask($n, $x, 6, $null, $null, 3, $null) }
  $x = TaskXml 'RT leak drift'; Rereg2 'RT leak drift' ($x -replace '<StartWhenAvailable>true</StartWhenAvailable>', '<StartWhenAvailable>false</StartWhenAvailable>')
  $x = TaskXml 'RT leak damaged'; Rereg2 'RT leak damaged' ($x -replace '"schema":1,', '"schema":"x",{{{')
  $x = TaskXml 'RT leak newer'; Rereg2 'RT leak newer' ($x -replace '"schema":1,', '"schema":99,')
  $x = TaskXml 'RT leak copysrc'; Rereg2 'RT leak copysrc (copy)' $x
  $l = T $script:S 'cadence_list_jobs' @{}; $lt = Msg $l; Collect $l
  Chk ($lt -notmatch $leakPattern) '8c: list_jobs rows for damaged / newer / drifted / copied jobs leak no paths, SIDs, user names or stack text' ((@((SCont $l).jobs | Where-Object { $_.problem } | ForEach-Object { Clip $_.problem 60 }) -join ' ; '))
  foreach ($id in @('rtleakd1', 'rtleakm1', 'rtleakn1')) { foreach ($t in @(@('cadence_get_job', @{ jobId = $id }), @('cadence_set_job_enabled', @{ jobId = $id; enabled = $true }), @('cadence_run_job_now', @{ jobId = $id }))) { $r = TW $t[0] $t[1]; Collect $r } }
  $all8 = ($texts -join "`n")
  Chk ($all8 -notmatch $leakPattern) ("8c: {0} error/hint/problem texts collected in section 8: none holds a machine path, SID, user name or stack text" -f $texts.Count) ([regex]::Match($all8, $leakPattern).Value)
  Chk ($all8 -notmatch [regex]::Escape($Secret) -and $all8 -notmatch [regex]::Escape($jobSecret)) '8c: none of them holds a planted secret' ''
  Reset-Agent
}


if ($Sections -contains 9) {
  Head '9 audit log integrity and flooding'
  Reset-Agent; New-Session 'audit'
  # 9a. every write attempt is audited exactly once, whatever the outcome
  $r = TW 'cadence_save_job' (SaveArgs 'RT audit job' $CMD '/d /c exit 0' @('0 3 1 1 *') $true); $aj = (JobOf $r).jobId; $ar = (JobOf $r).revision
  New-Session 'audit'
  $matrix = @(
    @('created', 'cadence_save_job', (J (SaveArgs 'RT audit two'))),
    @('invalid_argument (unknown property)', 'cadence_save_job', '{"bogus":1}'),
    @('invalid_argument (missing field)', 'cadence_set_job_enabled', '{"jobId":"aaaaaaaa"}'),
    @('not_found', 'cadence_set_job_enabled', '{"jobId":"zzzzzzzz","enabled":true}'),
    @('alreadyGone', 'cadence_delete_job', '{"jobId":"zzzzzzzz","confirmName":"x"}'),
    @('locked', 'cadence_set_job_enabled', ('{"jobId":"' + $LockId + '","enabled":true}')),
    @('stale_revision', 'cadence_save_job', ('{"jobId":"' + $aj + '","expectedRevision":"000000000000","name":"RT audit renamed"}')),
    @('program_rejected', 'cadence_save_job', (J (SaveArgs 'RT audit bad' 'cmd.exe'))),
    @('schedule_too_frequent', 'cadence_save_job', (J (SaveArgs 'RT audit fast' $CMD '/d /c exit 0' @('* * * * *')))),
    @('name_taken', 'cadence_save_job', (J (SaveArgs $OwnerName $CMD '/d /c exit 1'))),
    @('unchanged', 'cadence_set_job_enabled', ('{"jobId":"' + $aj + '","enabled":true}')),
    @('delete name mismatch', 'cadence_delete_job', ('{"jobId":"' + $aj + '","confirmName":"nope"}')),
    @('run now (cmd exit 0)', 'cadence_run_job_now', ('{"jobId":"' + $aj + '"}')),
    @('run now again (rate)', 'cadence_run_job_now', ('{"jobId":"' + $aj + '"}')))
  $off = New-Object System.Collections.Generic.List[string]; $codes9 = @()
  foreach ($m in $matrix) {
    $a0 = AuditCount; $r = TJ $script:S $m[1] $m[2]; $a1 = AuditCount; $d = $a1 - $a0
    $codes9 += ($m[0].Split(' ')[0] + '=' + (Code $r) + '/+' + $d)
    if ($d -gt 2) { $off.Add($m[0] + ' -> ' + (Code $r) + ' audit +' + $d) }   # [P23] a repeat of the same refusal folds into a counted line (+0); a pending count is flushed before the next line (+2)
  }
  # the 30-a-minute limiter's own refusals
  $rj = $aj; $rl = 0; $offRl = New-Object System.Collections.Generic.List[string]
  for ($i = 0; $i -lt 34; $i++) { $a0 = AuditCount; $r = T $script:S 'cadence_set_job_enabled' @{ jobId = $rj; enabled = ($i % 2 -eq 0) }; $d = (AuditCount) - $a0; if ((Code $r) -eq 'rate_limited') { $rl++ }; if ($d -gt 2) { $offRl.Add("call $i " + (Code $r) + " +$d") } }
  $rlSeen = @((AuditAll) | Select-Object -Last 80 | Where-Object { $_ -match 'rate_limited' }).Count
  Chk ($off.Count -eq 0) '9a: 14 write calls with different outcomes (created, invalid, not found, locked, stale, rejected, too frequent, name taken, unchanged, mismatch, run, run again): each left exactly one audit line' (($off -join ' | ') + ' || ' + ($codes9 -join ' '))
  Chk ($offRl.Count -eq 0 -and $rl -gt 0 -and $rlSeen -ge 1) '9a: the rate limiter''s own refusals (rate_limited) are audited, folded into counted lines (never one line more than the call)' ("rate_limited calls=$rl; lines seen=$rlSeen; off: " + ($offRl -join ' | '))
  # 9b. poisoning attempts
  New-Session 'audit'
  $esc = [string][char]0x1B; $bidi = [string][char]0x202E; $ls = [string][char]0x2028
  $poison = @("line1`nline2", '","outcome":"ok","code":"forged","detail":"x', ('x' + $esc + '[2J' + $esc + ']0;owned' + [char]7), ('a' + $bidi + 'b' + $ls + 'c'), ('{"v":1,"tool":"session","outcome":"connected"}'), ("a`r`nb`0c"), ('N' * 100))
  $badLines = New-Object System.Collections.Generic.List[string]; $nCalls = 0; $a0 = AuditCount
  foreach ($pn in $poison) {
    $nCalls++
    $r = TW 'cadence_save_job' (SaveArgs $pn $CMD '/d /c exit 0' @('0 3 1 1 *') $false)
    $null = TW 'cadence_set_job_enabled' ([ordered]@{ jobId = 'zzzzzzzz'; enabled = $true }); $nCalls++
    foreach ($t in @($svc.GetFolder('\CadenceTest').GetTasks(1))) { if ($Fixtures -notcontains $t.Name -and $t.Name -ne 'RT audit job') { try { $svc.GetFolder('\CadenceTest').DeleteTask($t.Name, 0) } catch { } } }
  }
  $a1 = AuditCount; $allowed = @('v', 'at', 'session', 'client', 'clientVersion', 'tool', 'jobId', 'job', 'outcome', 'code', 'detail', 'count')   # [P23] count: how many identical calls a collapsed line stands for
  $lines = @(AuditTail ($a1 - $a0 + 5)); $maxLen = 0; $extra = New-Object System.Collections.Generic.List[string]
  foreach ($ln in $lines) {
    if ($ln.Length -gt $maxLen) { $maxLen = $ln.Length }
    $o = AuditParse $ln
    if (-not $o) { $badLines.Add('unparseable: ' + (Clip $ln 80)); continue }
    foreach ($p in $o.PSObject.Properties) { if ($allowed -notcontains $p.Name) { $extra.Add($p.Name) } }
    if ($ln -match '[\x00-\x1f\x7f]') { $badLines.Add('raw control char in a line') }
    if ($ln -match [string][char]0x202E -or $ln -match [string][char]0x2028) { $badLines.Add('raw bidi/line-separator in a line') }
    if ($o.detail -and $o.detail.Length -gt 400) { $badLines.Add('detail ' + $o.detail.Length + ' chars') }
  }
  Chk ($badLines.Count -eq 0 -and $extra.Count -eq 0) '9b: names with newline, JSON, ESC/OSC, bidi, U+2028, CR/LF/NUL: every audit line is one valid JSON object with only the known fields, no raw control characters' ((($badLines + $extra) | Select-Object -First 3) -join ' | ' + ' lines=' + $lines.Count + ' longest=' + $maxLen)
  Chk (($a1 - $a0) -le ($nCalls + 2) -and ($a1 - $a0) -ge 1) '9b: poison calls produced no more than one line each (no line splitting, no forged extra line; identical refusals fold)' ("calls=$nCalls new lines=" + ($a1 - $a0))
  $forged = @((AuditAll) | Where-Object { $o = AuditParse $_; $o -and $o.tool -eq 'session' -and $o.outcome -eq 'connected' -and $o.client -eq 'audit' -and $o.detail -notlike 'protocol*' })
  Chk ($forged.Count -eq 0) '9b: a job named like a forged session line did not produce a forged session line' ''
  # the file itself: UTF-8, no BOM
  if (Test-Path $AuditFile) { $bytes = [IO.File]::ReadAllBytes($AuditFile); $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF); $badUtf = $false; try { $null = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($bytes) } catch { $badUtf = $true }; $ctl = @($bytes | Where-Object { $_ -lt 0x20 -and $_ -ne 0x0A -and $_ -ne 0x0D }).Count; Chk (-not $bom -and -not $badUtf -and $ctl -eq 0) '9b: audit.jsonl is UTF-8 without BOM, valid, no control bytes besides line ends' ("bytes=" + $bytes.Length + " bom=$bom invalid=$badUtf control=$ctl") }
  # client name / version abuse in the session line
  $cn = Start-Mcp ("c" + "`n" + '","tool":"cadence_save_job","outcome":"ok') ; $null = Stop-Mcp $cn
  $last = (AuditTail 3); $okl = @($last | Where-Object { $o = AuditParse $_; $o -ne $null }).Count
  Chk ($okl -eq $last.Count) '9b: a client name with a newline and JSON does not break the log (lines still valid JSON)' ($last.Count.ToString() + ' lines')
  $psi = NewPsi '--mcp --folder CadenceTest'; $pp = [System.Diagnostics.Process]::Start($psi); $Procs.Add($pp)
  $sv = @{ P = $pp; In = $pp.StandardInput.BaseStream; Pending = $null; Err = $pp.StandardError.ReadToEndAsync(); Id = 5 }
  $a0 = AuditCount; $ri = Req $sv ('{"jsonrpc":"2.0","id":"i","method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"vers","version":"' + ('9' * 10000) + '"}}}'); $null = Stop-Mcp $sv
  $newL = @(AuditTail ((AuditCount) - $a0 + 1)); $ml = ($newL | ForEach-Object { $_.Length } | Measure-Object -Maximum).Maximum
  Chk ($ml -lt 3000) '9b: a 10,000-character client version is cut in the audit line' ("longest new line $ml chars")

  # 9c. audit failure does not fail the tool call
  New-Session 'audit'
  $fs = $null; try { $fs = [IO.File]::Open($AuditFile, 'Open', 'ReadWrite', 'None') } catch { }
  if ($fs) {
    $r = TW 'cadence_set_job_enabled' @{ jobId = $aj; enabled = $false }; $warn = (SCont $r).warnings -join ' '
    $fs.Close()
    Note '9c: with the audit file exclusively locked by another process the write tool still ran (audit is best effort, by design: a write can happen unaudited while the file is locked)' ((Code $r) + ' ' + (Clip $warn 120))
  } else { Note '9c: could not lock the audit file for the test' '' }

  # 9d. flooding: can an agent push the owner's entries out? [CP-E] Since Phase 23 the owner's lines live in audit-owner.jsonl, which agent lines never rotate.
  Reset-Agent; New-Session 'flood'
  $null = RunCli '--agents suspend --folder CadenceTest'; $null = RunCli '--agents resume --folder CadenceTest'
  $owner = @((AuditAll) | Where-Object { $o = AuditParse $_; $o -and $o.tool -eq 'cli' -and $o.outcome -eq 'suspended' })
  if ($owner.Count -eq 0) { Note '9d: the owner''s CLI suspend left no audit entry to track' '' }
  $marker = if ($owner.Count) { ($owner | Select-Object -Last 1) } else { $null }
  $sizeBefore = (Get-Item $AuditFile).Length
  # what does one cheap refused call cost? (gate 1 comes before the rate limiter, so malformed calls are unlimited)
  $longProp = ('p' * 250)
  $line1 = CallJson 'cadence_save_job' ('{"' + $longProp + '":1}') 1
  $sbf = New-Object System.Text.StringBuilder; for ($i = 0; $i -lt 500; $i++) { [void]$sbf.Append((CallJson 'cadence_save_job' ('{"' + $longProp + '":' + $i + '}') (100000 + $i))); [void]$sbf.Append("`n") }
  $batch = $utf8.GetBytes($sbf.ToString())
  $total = 0; $firstRotation = $null; $sw = [Diagnostics.Stopwatch]::StartNew(); $evictedAt = $null; $evictMs = $null; $maxSize = 0; $detailLen = 0
  while ($sw.Elapsed.TotalSeconds -lt $FloodSeconds) {
    $wt = $script:S.In.WriteAsync($batch, 0, $batch.Length)          # asynchronous: the server stops reading while its answers wait for us
    $n = 0; while ($n -lt 500) { $l = ReadOne $script:S 30000; if ($l -eq '<<timeout>>' -or $null -eq $l) { break }; $n++ }
    try { $null = $wt.Wait(30000); $script:S.In.Flush() } catch { }
    $total += $n
    if ($n -lt 500) { Note '9d: the server stopped answering during the flood' "answered $n of 500 in the last batch"; break }
    if (-not $firstRotation -and (Test-Path $AuditFile1)) { $firstRotation = $total }
    $present = $(if (AuditHasText $marker) { 1 } else { 0 })
    $sz = (Get-Item $AuditFile).Length + $(if (Test-Path $AuditFile1) { (Get-Item $AuditFile1).Length } else { 0 }); if ($sz -gt $maxSize) { $maxSize = $sz }
    if ($marker -and $present -eq 0) { $evictedAt = $total; $evictMs = $sw.ElapsedMilliseconds; break }
  }
  $sw.Stop()
  $last1 = (AuditTail 1)[0]; $eo = AuditParse $last1; if ($eo) { $detailLen = ([string]$eo.detail).Length }
  $all9 = AuditAll; $kept = $all9.Count; $avgLen = if ($kept) { [int](($all9 | ForEach-Object { $_.Length } | Measure-Object -Average).Average) } else { 0 }
  Note '9d: flood figures' ("requests sent=$total (a part of a pipelined burst is answered 'Server busy' and not audited), first rotation after $firstRotation requests, audit entries kept at the end=$kept, average line=$avgLen bytes")
  $rate = [int]($total / [Math]::Max(0.001, $sw.Elapsed.TotalSeconds))
  $perEntry = if ($total -gt 0) { [int](($maxSize) / [Math]::Max(1, $total)) } else { 0 }
  if ($evictedAt) {
    Broke ("9d: an agent pushed the owner's CLI audit entry out of every log file with $evictedAt malformed requests in " + [int]($evictMs / 1000) + " s (~$rate requests/s)") ("agent files total up to " + [int]($maxSize / 1KB) + ' KiB')
  } else {
    Held "9d: after $total malformed calls in $([int]$sw.Elapsed.TotalSeconds) s (~$rate calls/s) the owner's CLI entry is still in the owner log (audit-owner.jsonl), which agent lines never rotate" ("agent files audit.jsonl + audit.1 total " + [int]($maxSize / 1KB) + ' KiB')
  }
  Chk ((Probe) -ne $null -and (RunCli '--agents log --max 5 --folder CadenceTest').Code -eq 0) '9d: after the flood the CLI log view and probe still work' ''
  Reset-Agent
}


}
catch {
  Broke 'script aborted by an unexpected error' ($_.Exception.Message + ' @ ' + $_.InvocationInfo.ScriptLineNumber)
}
finally {
  Head 'cleanup and recovery'
  try { SwitchRaw 0 } catch { }
  foreach ($p in $Procs) { try { if (-not $p.HasExited) { $p.Kill() } } catch { } }
  Start-Sleep -Milliseconds 700
  # (10) recovery checks, before the cleanup removes the evidence
  try {
    $st = RunCli '--agents status --folder CadenceTest'; Chk ($st.Code -eq 0) 'recovery: --agents status works' (Clip $st.Out 100)
    $pr = Probe; Chk ($pr -ne $null) 'recovery: --probe --folder CadenceTest still parses' ('jobs=' + @($pr.jobs).Count)
    if ($pr) { $bad = @($pr.jobs | Where-Object { $_.id -in @($OwnerId, $LockId) -and $_.sync -ne 'InSync' }); Chk ($bad.Count -eq 0) 'recovery: the owner fixtures are still InSync' (($bad | ForEach-Object { $_.id + ':' + $_.sync }) -join ',') }
    Chk ((HashOf (TaskXml $ForeignName)) -eq $ForeignHash0) 'recovery: the foreign task is byte-identical after every attack' ''
    $dupIds = @($pr.jobs | Group-Object id | Where-Object { $_.Count -gt 1 -and $_.Name })
    if ($dupIds.Count) { Note 'recovery: duplicate job ids present before cleanup (copies from killed renames)' (($dupIds | ForEach-Object { $_.Name }) -join ',') }
  } catch { Broke 'recovery checks failed to run' $_.Exception.Message }
  # leak and purity scans over everything any server printed
  try {
    $hit = @($AllText | Where-Object { $_ -and ($_.Contains($Secret) -or $_.Contains($env:CADENCE_RT_ENVVAL)) })
    $audit = AuditAll; $ah = @($audit | Where-Object { $_.Contains($Secret) -or $_.Contains($env:CADENCE_RT_ENVVAL) })
    Chk (($hit.Count -eq 0) -and ($ah.Count -eq 0)) 'leak scan: planted environment values appear nowhere in stdout, stderr, CLI output or the audit log' ("stdout/stderr lines with a secret=$($hit.Count); audit lines=$($ah.Count); " + (Clip ($hit | Select-Object -First 1) 200))
    $stdoutLines = @($AllText | Where-Object { $_ -and -not $_.StartsWith('[server stderr]') -and -not $_.StartsWith('[cli stderr]') })
    $impure = @($stdoutLines | Where-Object { $o = Parse $_; -not ($o -and $o.jsonrpc -eq '2.0') })
    Chk ($impure.Count -eq 0) ("stdout purity: {0} lines read from servers, all JSON-RPC 2.0" -f $stdoutLines.Count) (($impure | Select-Object -First 2 | ForEach-Object { Clip $_ 80 }) -join ' | ')
    $stderrAll = ($AllText | Where-Object { $_ -and ($_.StartsWith('[server stderr]')) }) -join "`n"
    if ($stderrAll -match 'Unhandled|System\.\w*Exception|   at Cadence|Stack') { Note 'server stderr contains exception text' (Clip ([regex]::Match(($stderrAll -replace '\s+', ' '), '.{0,150}(Unhandled|Exception|   at Cadence).{0,200}').Value) 500) } else { Held 'server stderr shows no unhandled exception or stack trace' '' }
  } catch { Broke 'scan failed to run' $_.Exception.Message }
  # cleanup (CadenceTest only)
  try { WipeCT; try { $null = $svc.GetFolder('\').DeleteFolder('CadenceTest', 0) } catch { } } catch { }
  try { if (Test-Path $LocalRoot) { [IO.Directory]::Delete($LocalRoot, $true) } } catch { Note 'could not delete %LOCALAPPDATA%\CadenceTest' $_.Exception.Message }
  try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\CadenceTest', $false) } catch { }
  $left = @($Procs | Where-Object { try { -not $_.HasExited } catch { $false } })
  Chk ($left.Count -eq 0) 'cleanup: no process started by this script is still running' (($left | ForEach-Object { $_.Id }) -join ',')
  $after = SnapOthers
  Chk ((DiffMap $before $after) -eq '') ("cleanup: all {0} tasks outside \Cadence* unchanged (name and XML hash)" -f $after.Count) (DiffMap $before $after)
  Chk ((-not [bool](FolderOrNull '\CadenceTest')) -and (-not (Test-Path $LocalRoot)) -and (-not (Test-Path 'HKCU:\Software\CadenceTest'))) 'cleanup: \CadenceTest, %LOCALAPPDATA%\CadenceTest and HKCU\Software\CadenceTest are gone' ''
  Chk (([bool](FolderOrNull '\Cadence') -eq $cadFolderBefore) -and ((Test-Path (Join-Path $env:LOCALAPPDATA 'Cadence')) -eq $cadLocalBefore) -and ((Test-Path 'HKCU:\Software\Cadence') -eq $cadRegBefore)) 'cleanup: \Cadence, %LOCALAPPDATA%\Cadence and HKCU\Software\Cadence untouched' ''
  Write-Host ''
  Write-Host ("SUMMARY: HELD={0} BROKE={1} NOTE={2}" -f $script:nHeld, $script:nBroke, $script:nNote)
  try { $log = Join-Path ([IO.Path]::GetTempPath()) 'redteam-agent-last.txt'; [IO.File]::WriteAllLines($log, $Out.ToArray(), $utf8); Write-Host "full log: $log" } catch { }
}
if ($script:nBroke -gt 0) { exit 1 } else { exit 0 }

