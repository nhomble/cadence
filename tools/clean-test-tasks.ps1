# Deletes every task in \CadenceTest\ (stopping running ones first), its subfolders, the folder itself, then %LOCALAPPDATA%\CadenceTest.
# The folder name is hard-coded on purpose: this script must never touch any other folder (every recursive step re-checks the path).
$ErrorActionPreference = 'Stop'
$folderName = 'CadenceTest'
$folderPath = '\' + $folderName

function Remove-TestFolderContents($folder, [string]$path) {
    if (-not ($path -eq $folderPath -or $path.StartsWith($folderPath + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw "Refusing to touch '$path': it is outside $folderPath"
    }
    $count = 0
    foreach ($t in @($folder.GetTasks(1))) {
        try { if ($t.State -eq 4) { $t.Stop(0) } } catch { }
        $folder.DeleteTask($t.Name, 0)
        $count++
    }
    foreach ($child in @($folder.GetFolders(0))) {
        $count += Remove-TestFolderContents $child ($path + '\' + $child.Name)
        $folder.DeleteFolder($child.Name, 0)
    }
    return $count
}

$svc = New-Object -ComObject 'Schedule.Service'
$svc.Connect()
$root = $svc.GetFolder('\')
$exists = $false
foreach ($f in $root.GetFolders(0)) { if ($f.Name -eq $folderName) { $exists = $true } }
if ($exists) {
    $folder = $svc.GetFolder($folderPath)
    $n = Remove-TestFolderContents $folder $folderPath
    $root.DeleteFolder($folderName, 0)
    Write-Host "Removed $n task(s) and folder $folderPath"
} else {
    Write-Host "No $folderPath folder to clean"
}
$dir = Join-Path $env:LOCALAPPDATA $folderName
if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
# [AG-P17] The agent policy of the test folder (hard-coded; refuses any other key).
$agentsKey = 'HKCU:\Software\' + $folderName + '\Agents'
if ($agentsKey -ne 'HKCU:\Software\CadenceTest\Agents') { throw "Refusing to touch registry key '$agentsKey'" }
if (Test-Path -LiteralPath $agentsKey) { Remove-Item -LiteralPath $agentsKey -Recurse -Force; Write-Host "Removed $agentsKey" }
# [AG-P20] The test folder's own parent key goes too, but only when nothing else lives in it (the app's window settings would).
$testKey = 'HKCU:\Software\' + $folderName
if ($testKey -ne 'HKCU:\Software\CadenceTest') { throw "Refusing to touch registry key '$testKey'" }
if (Test-Path -LiteralPath $testKey) {
    $item = Get-Item -LiteralPath $testKey
    if ($item.SubKeyCount -eq 0 -and $item.ValueCount -eq 0) { Remove-Item -LiteralPath $testKey -Force; Write-Host "Removed $testKey" }
}
exit 0
