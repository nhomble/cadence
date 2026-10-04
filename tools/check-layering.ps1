# Layering check. Exits 1 with a message on any violation. Rules: docs\REFACTOR-modules.md (a).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$errors = New-Object System.Collections.Generic.List[string]

# Allowed project references (by project name). Every project of the target layout is declared here, also those that do not exist yet,
# so a later phase only adds a project and its csproj. An existing project that is not in this table is an error.
# "Any SDK is a module boundary": Cron, Mcp, Mvvm and TestKit are SDK-shaped and reference no other Cadence project.
$allowed = @{
    'Cadence.Cron'             = @()
    'Cadence.Mcp'              = @()
    'Cadence.Mvvm'             = @()
    'Cadence.TestKit'          = @()
    'Cadence.Core'             = @('Cadence.Cron')
    'Cadence.History'          = @('Cadence.Core')
    'Cadence.Scheduler'        = @('Cadence.Core')
    'Cadence.Agent'            = @('Cadence.Core', 'Cadence.Scheduler', 'Cadence.History', 'Cadence.Mcp')
    'Cadence.Presentation'     = @('Cadence.Core', 'Cadence.Scheduler', 'Cadence.History', 'Cadence.Agent', 'Cadence.Mvvm')
    'Cadence.App'              = @('Cadence.Core', 'Cadence.Scheduler', 'Cadence.History', 'Cadence.Agent', 'Cadence.Presentation', 'Cadence.Mvvm')
    'Cadence.Demo'             = @('Cadence.Core', 'Cadence.Scheduler', 'Cadence.History', 'Cadence.Agent', 'Cadence.Presentation')
    'Cadence.Tests'            = @('Cadence.TestKit', 'Cadence.Cron', 'Cadence.Core', 'Cadence.Scheduler', 'Cadence.History', 'Cadence.Mcp', 'Cadence.Agent', 'Cadence.Mvvm', 'Cadence.Presentation', 'Cadence.Demo')
    'Cadence.App.Tests'        = @('Cadence.TestKit', 'Cadence.App', 'Cadence.Presentation', 'Cadence.Demo', 'Cadence.Core', 'Cadence.Scheduler', 'Cadence.History', 'Cadence.Agent')
    # Deliberately without Cadence.Mcp: the test MCP client must be independent of the server code (PLAN rule 8; see the grep rule below).
    'Cadence.IntegrationTests' = @('Cadence.TestKit', 'Cadence.Core', 'Cadence.Scheduler', 'Cadence.History', 'Cadence.Agent', 'Cadence.Presentation')
    # Dev-only screenshot tool (not shipped): references the App, Presentation and the Demo data.
    'Cadence.Render'           = @('Cadence.App', 'Cadence.Presentation', 'Cadence.Demo', 'Cadence.Mvvm', 'Cadence.Core', 'Cadence.Scheduler', 'Cadence.History', 'Cadence.Agent')
}

# TargetFramework per project, exact. No multi-targeting.
$tfms = @{
    'Cadence.Cron'             = 'net10.0'
    'Cadence.Mcp'              = 'net10.0'
    'Cadence.Mvvm'             = 'net10.0'
    'Cadence.TestKit'          = 'net10.0'
    'Cadence.Core'             = 'net10.0'
    'Cadence.History'          = 'net10.0'
    'Cadence.Scheduler'        = 'net10.0-windows'
    'Cadence.Agent'            = 'net10.0-windows'
    'Cadence.Presentation'     = 'net10.0-windows'
    'Cadence.App'              = 'net10.0-windows'
    'Cadence.Demo'             = 'net10.0-windows'
    'Cadence.Tests'            = 'net10.0-windows'
    'Cadence.App.Tests'        = 'net10.0-windows'
    'Cadence.IntegrationTests' = 'net10.0-windows'
    'Cadence.Render'           = 'net10.0-windows'
}

# WPF / Windows Forms only where listed (UseWPF / UseWindowsForms); a WPF FrameworkReference only in the adapter tests.
$wpfAllowed = @('Cadence.App', 'Cadence.Render')
$frameworkRefAllowed = @('Cadence.App.Tests')

# No cross-project source includes (final rule, no exceptions): a Compile/Page/None/EmbeddedResource/Content item whose Include starts with '..' is an error.
# Sources are shared through a project reference, never by linking a file from another project.

$projects = Get-ChildItem -Path $root -Recurse -Filter *.csproj -File |
    Where-Object { $_.FullName -notlike (Join-Path $root 'obj\*') -and $_.FullName -notlike (Join-Path $root 'bin\*') }

foreach ($p in $projects) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($p.Name)
    $rel = $p.FullName.Substring($root.Length + 1)
    [xml]$xml = Get-Content -LiteralPath $p.FullName -Raw
    if ($xml.SelectNodes('//PackageReference').Count -gt 0) {
        $errors.Add("$rel contains a PackageReference (NuGet packages are not allowed anywhere)")
    }
    if (-not $allowed.ContainsKey($name)) {
        $errors.Add("$rel is not a known project (add it to tools\check-layering.ps1)")
        continue
    }
    $refs = @($xml.SelectNodes('//ProjectReference') | ForEach-Object {
        [System.IO.Path]::GetFileNameWithoutExtension(($_.GetAttribute('Include') -replace '\\', '/'))
    })
    foreach ($r in $refs) {
        if ($allowed[$name] -notcontains $r) { $errors.Add("$rel references $r, which is not allowed by the layering rules") }
    }
    # Target framework: exact, single.
    if ($xml.SelectNodes('//TargetFrameworks').Count -gt 0) { $errors.Add("$rel uses <TargetFrameworks> (multi-targeting is not allowed)") }
    $tfmNode = $xml.SelectSingleNode('//TargetFramework')
    $tfmText = if ($tfmNode) { $tfmNode.InnerText.Trim() } else { '' }
    if ($tfmText -ne $tfms[$name]) { $errors.Add("$rel TargetFramework must be exactly $($tfms[$name]) (found '$tfmText')") }
    # WPF only where listed.
    foreach ($prop in 'UseWPF', 'UseWindowsForms') {
        $node = $xml.SelectSingleNode("//$prop")
        if ($node -and $node.InnerText.Trim() -eq 'true' -and $wpfAllowed -notcontains $name) { $errors.Add("$rel sets $prop, which only $($wpfAllowed -join ', ') may do") }
    }
    if ($xml.SelectNodes('//FrameworkReference').Count -gt 0 -and $frameworkRefAllowed -notcontains $name) {
        $errors.Add("$rel has a FrameworkReference, which only $($frameworkRefAllowed -join ', ') may have")
    }
    # No cross-project source includes.
    foreach ($kind in 'Compile', 'Page', 'None', 'EmbeddedResource', 'Content') {
        foreach ($item in $xml.SelectNodes("//$kind")) {
            $inc = $item.GetAttribute('Include')
            if (-not $inc.StartsWith('..')) { continue }
            $errors.Add("$rel has <$kind Include=`"$inc`">: sources are shared through a project reference, never by including a file from another project")
        }
    }
}

# Pattern lists. Core (and Cron) must stay pure.
$patterns = @(
    @{ Name = 'System.Windows';  Rx = 'System\.Windows' },
    @{ Name = 'Microsoft.Win32'; Rx = 'Microsoft\.Win32' },
    @{ Name = 'DllImport';       Rx = 'DllImport' },
    @{ Name = 'System.IO.File';  Rx = 'System\.IO\.File\b' },
    @{ Name = 'dynamic';         Rx = '\bdynamic\s' },
    # [CP-A] The original patterns only caught fully-qualified names: `using System.IO;` + `File.ReadAllText` passed.
    @{ Name = 'File/Directory IO'; Rx = '\b(File|Directory|FileInfo|DirectoryInfo|FileStream)\s*[.(]' },
    @{ Name = 'LibraryImport';     Rx = 'LibraryImport' },
    @{ Name = 'Process';           Rx = '\bProcess\s*\.\s*(Start|GetCurrentProcess|GetProcess)' },
    @{ Name = 'Environment';       Rx = '\bEnvironment\s*\.\s*(GetEnvironmentVariable|GetFolderPath|ExpandEnvironmentVariables|MachineName|UserName|UserDomainName)' },
    # Core is deterministic: "now" and the time zone are always parameters.
    @{ Name = 'clock';             Rx = '\bDateTime(Offset)?\s*\.\s*(Now|UtcNow|Today)\b' },
    @{ Name = 'local time zone';   Rx = '\bTimeZoneInfo\s*\.\s*Local\b' }
)
# [Review R] Cadence.Agent (the MCP tools) and Cadence.Mcp (the protocol) must stay BCL-only and headless: no WPF, no late-bound COM, and nothing that can write to stdout
# (stdout is the protocol channel; logs go through StderrLog). Phase 20 may add Microsoft.Win32 for the policy store, so that is not banned.
$agentPatterns = @(
    @{ Name = 'System.Windows';  Rx = 'System\.Windows' },
    @{ Name = 'dynamic';         Rx = '\bdynamic\s' },
    @{ Name = 'stdout writer';   Rx = '\bConsole\s*\.\s*(Write|WriteLine|Out\b|In\b|Read|ReadLine|OpenStandardInput\s*\(\s*\)\s*\.\s*Write)' },
    @{ Name = 'ConsoleOut/AttachConsole/MessageBox'; Rx = '\b(ConsoleOut|AttachConsole|MessageBox)\b' }
)
# Presentation (view models, UI logic, CLI text) has no WPF, no Win32, no COM, no processes, no console. The compiler already refuses WPF types (no framework reference).
$presentationPatterns = @(
    @{ Name = 'System.Windows (other than Input)'; Rx = 'System\.Windows\.(?!Input\b)' },
    @{ Name = 'Microsoft.Win32'; Rx = 'Microsoft\.Win32' },
    @{ Name = 'DllImport';       Rx = 'DllImport' },
    @{ Name = 'LibraryImport';   Rx = 'LibraryImport' },
    @{ Name = 'ComImport';       Rx = 'ComImport' },
    @{ Name = 'Process.Start';   Rx = '\bProcess\s*\.\s*Start' },
    @{ Name = 'Console';         Rx = '\bConsole\s*\.' },
    @{ Name = 'MessageBox';      Rx = 'MessageBox' },
    @{ Name = 'Registry';        Rx = '\bRegistry\b' }
)

function Get-SourceFiles([string]$dir) {
    Get-ChildItem -Path $dir -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notlike (Join-Path $dir 'obj\*') -and $_.FullName -notlike (Join-Path $dir 'bin\*') }
}

# Applies a pattern list to every .cs file of a source directory (skipped silently while the directory does not exist yet).
function Test-Bans([string]$dirRel, [string]$label, $pats, [bool]$skipComments) {
    $dir = Join-Path $root $dirRel
    if (-not (Test-Path $dir)) { return }
    foreach ($f in (Get-SourceFiles $dir)) {
        $text = if ($skipComments) { (Get-Content -LiteralPath $f.FullName | Where-Object { $_.TrimStart() -notmatch '^(//|///)' }) -join "`n" } else { Get-Content -LiteralPath $f.FullName -Raw }
        foreach ($pat in $pats) {
            if ($text -match $pat.Rx) { $script:errors.Add("$($f.FullName.Substring($root.Length + 1)) uses '$($pat.Name)', which $label must not use") }
        }
    }
}
Test-Bans 'src\Cadence.Core' 'Cadence.Core' $patterns $false
Test-Bans 'src\Cadence.Cron' 'Cadence.Cron' $patterns $false
Test-Bans 'src\Cadence.Agent' 'Cadence.Agent' $agentPatterns $true
Test-Bans 'src\Cadence.Mcp' 'Cadence.Mcp' $agentPatterns $true
Test-Bans 'src\Cadence.Presentation' 'Cadence.Presentation' $presentationPatterns $true

# Cron confinement: only the adapter file in Core may mention the Cadence.Cron module (Cadence.Core.Cron does not match), so the engine is replaceable by editing one file.
$srcDir = Join-Path $root 'src'
$cronAdapter = Join-Path $root 'src\Cadence.Core\Cron\CronSyntax.cs'
foreach ($f in (Get-SourceFiles $srcDir)) {
    if ($f.FullName -eq $cronAdapter -or $f.FullName -like (Join-Path $root 'src\Cadence.Cron\*')) { continue }
    if ((Get-Content -LiteralPath $f.FullName -Raw) -match '\bCadence\.Cron\b') {
        $errors.Add("$($f.FullName.Substring($root.Length + 1)) mentions Cadence.Cron; only src\Cadence.Core\Cron\CronSyntax.cs may")
    }
}

# MCP client independence: the integration tests' MCP client must not use the server's code.
$itDir = Join-Path $root 'tests\Cadence.IntegrationTests'
if (Test-Path $itDir) {
    foreach ($f in (Get-SourceFiles $itDir)) {
        if ((Get-Content -LiteralPath $f.FullName -Raw) -match '\bCadence\.Mcp\b') {
            $errors.Add("$($f.FullName.Substring($root.Length + 1)) mentions Cadence.Mcp; the test MCP client must be independent of the server code")
        }
    }
}

if ($errors.Count -gt 0) {
    [Console]::Error.WriteLine('LAYERING CHECK FAILED:')
    foreach ($e in $errors) { [Console]::Error.WriteLine('  ' + $e) }
    exit 1
}
Write-Host ("Layering check OK ({0} projects, no PackageReference, no cross-project sources, target frameworks exact, WPF only in App/Render, Core/Cron pure, Agent/Mcp headless, Presentation WPF-free)" -f @($projects).Count)
exit 0
