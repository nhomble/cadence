# McpClient.psm1 - a tiny MCP (stdio) client for Cadence's agent server. Windows PowerShell 5.1, BCL only.
#
#   Import-Module .\tools\McpClient.psm1
#   $s = Start-CadenceMcp -Exe .\bin\Cadence.exe -Folder CadenceTest
#   (Invoke-CadenceMcp -Session $s -Method 'tools/list').result.tools | ForEach-Object name
#   Invoke-CadenceTool -Session $s -Name cadence_list_jobs -Arguments @{}
#   Stop-CadenceMcp -Session $s
#
# Rules this module follows (Review R): the server is started with System.Diagnostics.Process (CreateNoWindow, no shell),
# requests are written as UTF-8 bytes straight to StandardInput.BaseStream (no BOM, never a PowerShell pipeline into the exe),
# every read has a hard timeout, and Stop-CadenceMcp always ends the child (close stdin, wait, then kill) and checks it is gone.
# Use it only with --folder CadenceTest unless you really mean to manage your own schedules.

Set-StrictMode -Version 2.0

$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:AllSessions = New-Object System.Collections.Generic.List[object]

function Start-CadenceMcp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Exe,
        [string]$Folder = 'CadenceTest',
        [string]$ClientName = 'cadence-acceptance',
        [string]$ClientTitle = 'Cadence Acceptance Client',
        [string]$ProtocolVersion = '2025-11-25',
        [string]$ExtraArgs = '',
        [int]$TimeoutSec = 20,
        [switch]$NoInitialize
    )
    if (-not (Test-Path -LiteralPath $Exe)) { throw "Cadence.exe not found: $Exe" }
    $userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    if (-not $env:DOTNET_ROOT -and (Test-Path -LiteralPath $userDotnet)) { $env:DOTNET_ROOT = $userDotnet }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Resolve-Path -LiteralPath $Exe).Path
    $a = '--mcp'
    if ($Folder) { $a += ' --folder ' + $Folder }
    if ($ExtraArgs) { $a += ' ' + $ExtraArgs }
    $psi.Arguments = $a
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = $script:Utf8
    $psi.StandardErrorEncoding = $script:Utf8
    $psi.EnvironmentVariables['DOTNET_DISABLE_GUI_ERRORS'] = '1'
    if ($env:DOTNET_ROOT) { $psi.EnvironmentVariables['DOTNET_ROOT'] = $env:DOTNET_ROOT }
    $p = [System.Diagnostics.Process]::Start($psi)
    [void]$p.Handle
    $s = [pscustomobject]@{
        Process = $p; Stdin = $p.StandardInput.BaseStream; Pending = $null; NextId = 1
        StderrTask = $p.StandardError.ReadToEndAsync(); Stderr = ''; ClientTitle = $ClientTitle; Pid = $p.Id; Stopped = $false
    }
    $script:AllSessions.Add($s)
    if (-not $NoInitialize) {
        try {
            $ci = [ordered]@{ name = $ClientName; version = '1.0' }
            if ($ClientTitle) { $ci.title = $ClientTitle }
            $r = Invoke-CadenceMcp -Session $s -Method 'initialize' -TimeoutSec $TimeoutSec -Params @{ protocolVersion = $ProtocolVersion; capabilities = @{}; clientInfo = $ci }
            if (-not $r -or -not ($r.PSObject.Properties.Name -contains 'result')) { throw 'initialize did not return a result' }
            Send-CadenceMcpNotification -Session $s -Method 'notifications/initialized'
            $s | Add-Member -NotePropertyName InitializeResult -NotePropertyValue $r.result
        } catch {
            Stop-CadenceMcp -Session $s -Force | Out-Null
            throw
        }
    }
    $s
}

function Send-CadenceMcpRaw {
    # Writes exact text (plus a newline unless -NoNewline) as UTF-8 bytes to the server's stdin stream.
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Text, [switch]$NoNewline)
    $t = $Text; if (-not $NoNewline) { $t += "`n" }
    $b = $script:Utf8.GetBytes($t)
    $Session.Stdin.Write($b, 0, $b.Length)
    $Session.Stdin.Flush()
}

function Read-CadenceMcpLine {
    # One stdout line, or $null on timeout; '' + Session.Closed when the stream ended.
    param([Parameter(Mandatory)]$Session, [int]$TimeoutSec = 15)
    if ($null -eq $Session.Pending) { $Session.Pending = $Session.Process.StandardOutput.ReadLineAsync() }
    if (-not $Session.Pending.Wait($TimeoutSec * 1000)) { return $null }
    $l = $Session.Pending.Result
    $Session.Pending = $null
    if ($null -eq $l) { return [string]::Empty }
    $l
}

function Send-CadenceMcpNotification {
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Method, $Params)
    $m = [ordered]@{ jsonrpc = '2.0'; method = $Method }
    if ($null -ne $Params) { $m.params = $Params }
    Send-CadenceMcpRaw -Session $Session -Text (ConvertTo-Json -InputObject $m -Compress -Depth 20)
}

function Invoke-CadenceMcp {
    <# Sends one request and reads lines until the reply with the same id. Returns the parsed JSON-RPC response object
       (has .result or .error). Throws on timeout or when the server closed its output. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Method, $Params, [int]$TimeoutSec = 15)
    $id = $Session.NextId; $Session.NextId = $id + 1
    $m = [ordered]@{ jsonrpc = '2.0'; id = $id; method = $Method }
    if ($null -ne $Params) { $m.params = $Params }
    Send-CadenceMcpRaw -Session $Session -Text (ConvertTo-Json -InputObject $m -Compress -Depth 20)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ($true) {
        $left = [int][Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalSeconds)
        if ($left -le 0) { throw "MCP request '$Method' (id $id) timed out after $TimeoutSec s" }
        $l = Read-CadenceMcpLine -Session $Session -TimeoutSec $left
        if ($null -eq $l) { throw "MCP request '$Method' (id $id) timed out after $TimeoutSec s" }
        if ($l -eq '') { throw "MCP server closed its output while waiting for '$Method'" }
        $o = $null
        try { $o = $l | ConvertFrom-Json } catch { continue }
        if ($o -and ($o.PSObject.Properties.Name -contains 'id') -and [string]$o.id -eq [string]$id) { return $o }
    }
}

function Invoke-CadenceTool {
    <# tools/call. Returns the parsed structuredContent. Throws when the call is a JSON-RPC error; for a tool error
       (isError) it throws too unless -AllowError, which returns the structuredContent (with .error.code) instead. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Name, $Arguments = @{}, [int]$TimeoutSec = 25, [switch]$AllowError, [switch]$Raw)
    $r = Invoke-CadenceMcp -Session $Session -Method 'tools/call' -TimeoutSec $TimeoutSec -Params ([ordered]@{ name = $Name; arguments = $Arguments })
    if ($Raw) { return $r }
    if ($r.PSObject.Properties.Name -contains 'error') { throw ("JSON-RPC error {0}: {1}" -f $r.error.code, $r.error.message) }
    $res = $r.result
    $sc = $null
    if ($res.PSObject.Properties.Name -contains 'structuredContent') { $sc = $res.structuredContent }
    if ($res.isError -and -not $AllowError) {
        $txt = ''; if ($res.content) { $txt = [string]$res.content[0].text }
        throw ("tool {0} failed: {1}" -f $Name, $txt)
    }
    $sc
}

function Stop-CadenceMcp {
    <# Closes stdin, waits up to 5 s for exit, kills the process if it is still there, and returns
       @{ Exited; ExitCode; Killed; Stderr; Gone }. Gone = the process id is no longer alive. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Session, [switch]$Force, [int]$WaitSec = 5)
    $p = $Session.Process
    $exited = $false; $killed = $false; $code = $null
    try { if (-not $Force) { $Session.Stdin.Close() } } catch { }
    try {
        if ($Force) { $exited = $p.HasExited } else { $exited = $p.WaitForExit($WaitSec * 1000) }
        if (-not $exited) { try { $p.Kill() } catch { }; $killed = $true; [void]$p.WaitForExit(5000) }
        if ($p.HasExited) { $code = $p.ExitCode }
    } catch { }
    $err = ''
    try { if ($Session.StderrTask.Wait(3000)) { $err = [string]$Session.StderrTask.Result } } catch { }
    $Session.Stderr = $err
    $Session.Stopped = $true
    $gone = $false; try { $gone = $p.HasExited } catch { }
    [void]$script:AllSessions.Remove($Session)
    [pscustomobject]@{ Exited = $exited; ExitCode = $code; Killed = $killed; Stderr = $err; Gone = $gone }
}

function Stop-AllCadenceMcp {
    # Safety net for a finally block: ends every session this module started that is still open.
    $open = $script:AllSessions.ToArray()
    foreach ($sess in $open) {
        try { $null = Stop-CadenceMcp -Session $sess -Force }
        catch { try { $sess.Process.Kill() } catch { } }
    }
}

Export-ModuleMember -Function Start-CadenceMcp, Invoke-CadenceMcp, Invoke-CadenceTool, Stop-CadenceMcp, Stop-AllCadenceMcp, Send-CadenceMcpRaw, Send-CadenceMcpNotification, Read-CadenceMcpLine
