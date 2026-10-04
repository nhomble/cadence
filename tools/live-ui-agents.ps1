# Phase 23 live UI Automation check of the real app (quiet mode, CadenceTest folder only): the Agents pill, the in-window resume panel, the Orange note after an agent
# resumed, Agent activity, the per-job lock and the stale-save panel, driven with a real MCP session ("Cadence Test Agent") and checked against the CLI.
# No window is shown (CADENCE_TEST_QUIET=1) and the foreground window must not change. Run after build.cmd: powershell -File tools\live-ui-agents.ps1
$ErrorActionPreference = 'Stop'
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_DISABLE_GUI_ERRORS = '1'
$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'obj\artifacts\bin\Cadence.App\release_win-x64\Cadence.exe'
$scratch = [IO.Path]::GetTempPath()
$script:fails = 0
$script:started = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "PASS $name" } else { "FAIL $name -- $detail"; $script:fails++ }
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class Fg { [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); }'
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Prop($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
function AndC($a, $b) { New-Object System.Windows.Automation.AndCondition($a, $b) }
function ById($root, [string]$id) { $root.FindFirst($TS::Descendants, (Prop $AE::AutomationIdProperty $id)) }
function ByName($root, [string]$name) { $root.FindFirst($TS::Descendants, (Prop $AE::NameProperty $name)) }
function Pressed($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function WaitFor([scriptblock]$cond, [int]$seconds = 40) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) { try { if (& $cond) { return $true } } catch { } ; Start-Sleep -Milliseconds 300 }
    return $false
}

# ---- registry (read-only helpers; the test folder only) ----
function AgentKey { [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\CadenceTest\Agents') }
function AgentValue([string]$name) { $k = AgentKey; if ($null -eq $k) { return $null }; try { return $k.GetValue($name) } finally { $k.Dispose() } }
function ProductKeyExists { $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Cadence'); $r = $null -ne $k; if ($k) { $k.Dispose() }; $r }
function DeleteTestKey { try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\CadenceTest', $false) } catch { } }

# ---- the CLI (one-shot, never through a pipeline) ----
function RunCli([string]$arguments) {
    $f = Join-Path $scratch ('cli-' + [guid]::NewGuid().ToString('N') + '.txt')
    $p = Start-Process -FilePath $exe -ArgumentList ($arguments + ' --folder CadenceTest') -Wait -NoNewWindow -PassThru -RedirectStandardOutput $f
    $null = $p.Handle
    $text = if (Test-Path -LiteralPath $f) { [IO.File]::ReadAllText($f) } else { '' }
    [IO.File]::Delete($f)
    [pscustomobject]@{ Exit = $p.ExitCode; Text = $text }
}

# ---- a tiny MCP client: System.Diagnostics.Process, UTF-8 bytes on the base stream (no BOM) ----
function Start-Mcp([string]$client) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = '--mcp --folder CadenceTest'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $p = [System.Diagnostics.Process]::Start($psi)
    $null = $p.Handle
    $script:started.Add($p)
    $s = [pscustomobject]@{ Proc = $p; Id = 0 }
    $null = Rpc $s 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = $client; version = '1.0' } }
    Send $s @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    $s
}
function Send($s, $obj) {
    $bytes = [Text.Encoding]::UTF8.GetBytes((($obj | ConvertTo-Json -Depth 20 -Compress)) + "`n")
    $st = $s.Proc.StandardInput.BaseStream
    $st.Write($bytes, 0, $bytes.Length)
    $st.Flush()
}
function Rpc($s, [string]$method, $params) {
    $s.Id++
    $id = $s.Id
    Send $s @{ jsonrpc = '2.0'; id = $id; method = $method; params = $params }
    $until = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $until) {
        $t = $s.Proc.StandardOutput.ReadLineAsync()
        if (-not $t.Wait(20000)) { throw "no answer to $method" }
        $o = $t.Result | ConvertFrom-Json
        if ($o.id -eq $id) { return $o }
    }
    throw "no answer to $method"
}
function Tool($s, [string]$name, $arguments) { (Rpc $s 'tools/call' @{ name = $name; arguments = $arguments }).result }
function Stop-Mcp($s) {
    try { $s.Proc.StandardInput.Close() } catch { }
    if (-not $s.Proc.WaitForExit(5000)) { $s.Proc.Kill() }
}

$cmd = 'C:\Windows\System32\cmd.exe'
$yesterday = (Get-Date).AddDays(-1)
$cron = '{0} {1} {2} {3} *' -f $yesterday.Minute, $yesterday.Hour, $yesterday.Day, $yesterday.Month
$jobName = 'Cadence Test Agent Job'
$app = $null
$mcp = $null
$productBefore = ProductKeyExists
$localProductBefore = Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Cadence')
try {
    & (Join-Path $PSScriptRoot 'clean-test-tasks.ps1') | Out-Null
    DeleteTestKey
    $fgBefore = [Fg]::GetForegroundWindow()

    # ---- start the real app, quiet mode, test folder ----
    $env:CADENCE_FOLDER = 'CadenceTest'
    $env:CADENCE_TEST_QUIET = '1'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false
    $app = [System.Diagnostics.Process]::Start($psi)
    $null = $app.Handle
    $script:started.Add($app)
    $win = $null
    $null = WaitFor { $script:win = $AE::RootElement.FindFirst($TS::Children, (Prop $AE::ProcessIdProperty $app.Id)); $null -ne $script:win } 30
    Check 'the app window exists (UI Automation sees it)' ($null -ne $win)
    $pill = $null
    $null = WaitFor { $script:pill = ById $win 'AgentsPill'; $null -ne $script:pill } 30
    Check 'the Agents pill is there' ($null -ne $pill)
    Check 'pill says "Agents allowed" when nothing is set' ($pill.Current.Name -eq 'Agents allowed') $pill.Current.Name
    Check 'reading created no registry key' ($null -eq (AgentKey))

    # ---- click the pill: suspend, immediately, from the app ----
    Pressed $pill
    $null = WaitFor { (AgentValue 'Suspended') -eq 1 } 10
    Check 'the switch is on in HKCU\Software\CadenceTest\Agents' ((AgentValue 'Suspended') -eq 1)
    Check 'recorded as the app' ((AgentValue 'SuspendedBy') -eq 'app') ([string](AgentValue 'SuspendedBy'))
    $null = WaitFor { $script:pill = ById $win 'AgentsPill'; $script:pill.Current.Name -eq 'Agents suspended' } 10
    Check 'the pill now says "Agents suspended"' ($pill.Current.Name -eq 'Agents suspended') $pill.Current.Name
    Check 'its tooltip says it was suspended in the app' ($pill.Current.HelpText -like 'Suspended in the app at*') $pill.Current.HelpText
    $st = RunCli '--agents status'
    Check 'CLI --agents status agrees (suspended)' ($st.Text -match 'suspended' -and $st.Text -match 'Cadence app') $st.Text.Substring(0, [Math]::Min(300, $st.Text.Length))

    # ---- an agent session: refused while suspended ----
    $mcp = Start-Mcp 'Cadence Test Agent'
    $r = Tool $mcp 'cadence_save_job' @{ name = $jobName; schedule = @{ cron = @($cron) }; program = @{ path = $cmd; arguments = '/d /c "echo agent-ok"' } }
    Check 'while suspended the agent save is refused' ($r.isError -and $r.structuredContent.error.code -eq 'suspended') ($r | ConvertTo-Json -Depth 6 -Compress)
    $g = Tool $mcp 'cadence_get_status' @{}
    Check 'cadence_get_status shows suspended by app' ($g.structuredContent.agents.suspended -eq $true -and $g.structuredContent.agents.suspendedBy -eq 'app') ($g.structuredContent | ConvertTo-Json -Depth 6 -Compress)

    # ---- resume through the pill: a panel INSIDE the window asks first ----
    Pressed $pill
    $panel = $null
    $null = WaitFor { $script:panel = (ByName $win 'Let AI agents change your schedules again?'); $null -ne $script:panel } 10
    Check 'the question is a panel inside the window' ($null -ne $panel)
    Check 'it asks "Let AI agents change your schedules again?"' ($panel.Current.Name -eq 'Let AI agents change your schedules again?') $panel.Current.Name
    $windows = @($AE::RootElement.FindAll($TS::Children, (Prop $AE::ProcessIdProperty $app.Id)))
    Check 'no second window of the app exists' ($windows.Count -eq 1) ([string]$windows.Count)
    Pressed (ByName $win 'Cancel')
    $null = WaitFor { $null -eq ((ByName $win 'Let AI agents change your schedules again?')) } 10
    Check 'Cancel leaves agents suspended' ((AgentValue 'Suspended') -eq 1)
    Pressed $pill
    $null = WaitFor { $script:panel = (ByName $win 'Let AI agents change your schedules again?'); $null -ne $script:panel } 10
    Pressed (ByName $win 'Resume')
    $null = WaitFor { (AgentValue 'Suspended') -eq 0 } 10
    Check 'Resume turns the switch off' ((AgentValue 'Suspended') -eq 0)
    Check 'the resume by the app shows no Orange note' ($null -eq (ById $win 'AgentNoteText'))
    $null = WaitFor { $script:pill = ById $win 'AgentsPill'; $script:pill.Current.Name -eq 'Agents allowed' } 10
    Check 'the pill is back to "Agents allowed"' ($pill.Current.Name -eq 'Agents allowed') $pill.Current.Name

    # ---- the agent works again; its row shows who updated it ----
    $r = Tool $mcp 'cadence_save_job' @{ name = $jobName; schedule = @{ cron = @($cron) }; program = @{ path = $cmd; arguments = '/d /c "echo agent-ok"' } }
    Check 'after resume the agent save works' ((-not $r.isError) -and $r.structuredContent.outcome -eq 'created') ($r | ConvertTo-Json -Depth 6 -Compress)
    $jobId = $r.structuredContent.job.jobId
    $line = $null
    $null = WaitFor { $script:line = ById $win 'UpdatedLine'; $null -ne $script:line -and $script:line.Current.Name -like '*Cadence Test Agent*' } 45
    Check 'the list row shows "Updated ... by Cadence Test Agent"' ($null -ne $line -and $line.Current.Name -like 'Updated*by Cadence Test Agent') ([string]$line.Current.Name)

    # ---- the agent suspends itself with a reason: the tooltip names it and says why ----
    $r = Tool $mcp 'cadence_suspend_agents' @{ reason = 'phase 23 check' }
    Check 'the agent suspended itself' (-not $r.isError) ($r | ConvertTo-Json -Depth 6 -Compress)
    $null = WaitFor { $script:pill = ById $win 'AgentsPill'; $script:pill.Current.Name -eq 'Agents suspended' -and $script:pill.Current.HelpText -like '*Cadence Test Agent*' } 45
    Check 'the pill tooltip names the agent and the reason' ($pill.Current.HelpText -like '*AI agent Cadence Test Agent*' -and $pill.Current.HelpText -like '*phase 23 check*') $pill.Current.HelpText
    $st = RunCli '--agents status --json'
    Check 'CLI status agrees: suspended by the agent' ($st.Text -match 'agent:Cadence Test Agent') $st.Text.Substring(0, [Math]::Min(400, $st.Text.Length))

    # ---- the agent turns agents back on: the Orange note ----
    $r = Tool $mcp 'cadence_resume_agents' @{ reason = 'phase 23 check done' }
    Check 'the agent resumed agents' (-not $r.isError) ($r | ConvertTo-Json -Depth 6 -Compress)
    $note = $null
    $null = WaitFor { $script:note = ById $win 'AgentNoteText'; $null -ne $script:note } 45
    Check 'the Orange note appears' ($null -ne $note)
    Check 'it names the agent' ($note.Current.Name -like 'AI agents were turned back on by AI agent Cadence Test Agent at *') ([string]$note.Current.Name)
    Check 'its tooltip has the reason and the advice' ($note.Current.HelpText -like '*phase 23 check done*' -and $note.Current.HelpText -like '*check your agent''s tool permissions*') ([string]$note.Current.HelpText)
    $st = RunCli '--agents status --json'
    Check 'CLI status agrees: resumed by the agent' ($st.Text -match 'agent:Cadence Test Agent' -and $st.Text -match '"suspended"\s*:\s*false') $st.Text.Substring(0, [Math]::Min(400, $st.Text.Length))

    # ---- opening the menu acknowledges the note ----
    $menuButton = ById $win 'MenuButton'
    if ($null -eq $menuButton) { $menuButton = ByName $win 'Menu' }
    Pressed $menuButton
    $null = WaitFor { $null -eq (ById $win 'AgentNoteText') } 10
    Check 'opening the menu dismisses the note' ($null -eq (ById $win 'AgentNoteText'))
    $root = $AE::RootElement
    $pidC = Prop $AE::ProcessIdProperty $app.Id
    $item = $null
    $null = WaitFor { $script:item = $root.FindFirst($TS::Descendants, (AndC $pidC (Prop $AE::AutomationIdProperty 'SuspendAgentsItem'))); $null -ne $script:item } 10
    Check 'the menu has "Suspend AI agents"' ($null -ne $item)
    Check 'the app remembered the acknowledgement (HKCU\Software\CadenceTest)' ((WaitFor { $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\CadenceTest'); $v = if ($k) { $k.GetValue('AgentNoteSeenAt') } ; if ($k) { $k.Dispose() }; $null -ne $v } 10))

    # ---- Agent activity panel ----
    $act = $root.FindFirst($TS::Descendants, (AndC $pidC (Prop $AE::AutomationIdProperty 'AgentActivityItem')))
    Pressed $act
    $ap = $null
    $null = WaitFor { $script:ap = (ByName $win 'Agent activity'); $null -ne $script:ap } 10
    Check 'Agent activity opens as a panel in the window' ($null -ne $ap)
    $rows = @()
    $null = WaitFor { $script:rows = @($ap.FindAll($TS::Descendants, (Prop $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like 'turned agents back on' -or $_.Current.Name -like 'saved a schedule' }); $script:rows.Count -ge 2 } 10
    $allText = @($win.FindAll($TS::Descendants, (Prop $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name })
    Check 'it lists what the agent did (saved a schedule, turned agents back on)' (($allText -contains 'saved a schedule') -and ($allText -contains 'turned agents back on') -and ($allText -contains 'suspended agents')) (($allText -join ' | '))
    Pressed (ByName $win 'Close agent activity')
    $null = WaitFor { $null -eq ((ByName $win 'Agent activity')) } 10
    Check 'Close closes it' ($null -eq ((ByName $win 'Agent activity')))

    # ---- lock the job in its editor; the agent is then refused ----
    $rowBtn = ByName $win $jobName
    Pressed $rowBtn
    $menuBtn = $null
    $null = WaitFor { $script:menuBtn = ByName $win 'Schedule menu'; $null -ne $script:menuBtn } 15
    Check 'the editor opened with its ⋯ menu' ($null -ne $menuBtn)
    $meta = $null
    $null = WaitFor { $script:meta = ById $win 'MetaLine'; $null -ne $script:meta } 10
    Check 'the editor shows who created and updated it' ($null -ne $meta -and $meta.Current.Name -like '*by Cadence Test Agent*') ([string]$meta.Current.Name)
    Check 'the pill is in the editor too' ($null -ne (ById $win 'AgentsPill'))
    Pressed $menuBtn
    $lockItem = $null
    $null = WaitFor { $script:lockItem = ById $win 'AgentLockItem'; $null -ne $script:lockItem } 10
    Pressed $lockItem
    $null = WaitFor { $script:meta = ById $win 'MetaLine'; $script:meta.Current.Name -like '*Locked for AI agents*' } 20
    Check 'the lock was saved and the line says so' ($meta.Current.Name -like '*Locked for AI agents*') ([string]$meta.Current.Name)
    $rev = (Tool $mcp 'cadence_get_job' @{ jobId = $jobId }).structuredContent.revision
    $r = Tool $mcp 'cadence_save_job' @{ jobId = $jobId; expectedRevision = $rev; name = $jobName; schedule = @{ cron = @($cron) }; program = @{ path = $cmd; arguments = '/d /c "echo changed"' } }
    Check 'the agent is refused as locked' ($r.isError -and $r.structuredContent.error.code -eq 'locked') ($r | ConvertTo-Json -Depth 6 -Compress)
    # ---- unlock again, then a stale save: an agent changes the job while the editor is open ----
    Pressed $menuBtn
    $null = WaitFor { $script:lockItem = ById $win 'AgentLockItem'; $null -ne $script:lockItem } 10
    Pressed $lockItem
    $null = WaitFor { $script:meta = ById $win 'MetaLine'; $script:meta.Current.Name -notlike '*Locked for AI agents*' } 20
    Check 'unlocking is a save too, and the line no longer says locked' ($meta.Current.Name -notlike '*Locked for AI agents*') ([string]$meta.Current.Name)
    $rev = (Tool $mcp 'cadence_get_job' @{ jobId = $jobId }).structuredContent.revision
    $r = Tool $mcp 'cadence_save_job' @{ jobId = $jobId; expectedRevision = $rev; name = $jobName; schedule = @{ cron = @($cron) }; program = @{ path = $cmd; arguments = '/d /c "echo agent-changed-it"' } }
    Check 'unlocked: the agent can change the job again' ((-not $r.isError) -and $r.structuredContent.outcome -eq 'updated') ($r | ConvertTo-Json -Depth 6 -Compress)
    $nameBox = $win.FindFirst($TS::Descendants, (AndC (Prop $AE::NameProperty 'Name') (Prop $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit))))
    $nameBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Cadence Test Agent Job 2')
    $saveBtn = ByName $win 'Save'
    $null = WaitFor { $script:saveBtn = ByName $win 'Save'; $script:saveBtn.Current.IsEnabled } 10
    Check 'Save is enabled after my edit' ($saveBtn.Current.IsEnabled)
    Pressed $saveBtn
    $stale = $null
    $null = WaitFor { $script:stale = (ByName $win 'Changed while you were editing'); $null -ne $script:stale } 20
    Check 'the stale save asks in a panel inside the window' ($null -ne $stale)
    Check 'its title says it was changed while editing' ($stale.Current.Name -eq 'Changed while you were editing') ([string]$stale.Current.Name)
    $texts = @($win.FindAll($TS::Descendants, (Prop $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name })
    Check 'it names who changed it' (($texts -join ' ') -like '*changed * by Cadence Test Agent while you were editing*') (($texts -join ' | '))
    Check 'it offers Reload and re-apply my change / Keep editing' ($null -ne (ByName $win 'Reload and re-apply my change') -and $null -ne (ByName $win 'Keep editing'))
    Pressed (ByName $win 'Reload and re-apply my change')
    $null = WaitFor { $null -eq ((ByName $win 'Changed while you were editing')) } 10
    $status = $null
    $null = WaitFor { $script:status = ById $win 'Status'; $null -ne $script:status -and $script:status.Current.Name -like 'Reloaded*' } 15
    Check 'the reloaded editor says my change is on top of the agent''s version' ($null -ne $status -and $status.Current.Name -like 'Reloaded. Your change is applied on top of Cadence Test Agent*') ([string]$status.Current.Name)
    $nameBox = $win.FindFirst($TS::Descendants, (AndC (Prop $AE::NameProperty 'Name') (Prop $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit))))
    Check 'my new name is still in the form' ($nameBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'Cadence Test Agent Job 2')
    $saveBtn = ByName $win 'Save'
    Pressed $saveBtn
    $null = WaitFor { $script:status = ById $win 'Status'; $null -ne $script:status -and $script:status.Current.Name -like 'Saved*' } 20
    Check 'saving the merged version works' ($null -ne $status -and $status.Current.Name -like 'Saved*') ([string]$status.Current.Name)
    $g = Tool $mcp 'cadence_get_job' @{ jobId = $jobId }
    $gj = $g.structuredContent | ConvertTo-Json -Depth 8 -Compress
    Check 'the job now has my name and the agent''s program' ($gj -like '*Cadence Test Agent Job 2*' -and $gj -like '*agent-changed-it*') $gj
}
catch {
    "FAIL script error -- $($_.Exception.Message)"
    $script:fails++
}
finally {
    foreach ($p in $script:started) { try { if (-not $p.HasExited) { try { $p.StandardInput.Close() } catch { }; if (-not $p.WaitForExit(2000)) { $p.Kill() } } } catch { } }
    Start-Sleep -Milliseconds 500
    try { & (Join-Path $PSScriptRoot 'clean-test-tasks.ps1') | Out-Null } catch { "cleanup: $($_.Exception.Message)" }
    DeleteTestKey
    $fgAfter = [Fg]::GetForegroundWindow()
    Check 'the foreground window never changed' ($fgBefore -eq $fgAfter) "$fgBefore -> $fgAfter"
    Check 'HKCU\Software\Cadence is exactly as before' ($productBefore -eq (ProductKeyExists))
    Check '%LOCALAPPDATA%\Cadence is exactly as before' ($localProductBefore -eq (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Cadence')))
    $left = @(Get-Process -Name Cadence -ErrorAction SilentlyContinue | Where-Object { $ids = $script:started | ForEach-Object { $_.Id }; $ids -contains $_.Id })
    Check 'no Cadence.exe that this script started is still running' ($left.Count -eq 0) ([string]$left.Count)
    "failures: $script:fails"
}
