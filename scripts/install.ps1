[CmdletBinding()]
param([ValidateSet('Install','Uninstall')][string]$Action='Install')
# Run by double-clicking Install.cmd / Uninstall.cmd. From an AI agent's sandboxed terminal the
# startup registry entry would only exist inside that sandbox and never fire at login.
$ErrorActionPreference='Stop'
$dest=Join-Path $env:LOCALAPPDATA 'Programs\ClaudeNiang'
$exe=Join-Path $dest 'ClaudeNiang.exe'
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName='ClaudeNiang'
$shortcut=Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Niang.lnk'
function Stop-Widget {
    if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe -ArgumentList '--exit' -Wait }
    Start-Sleep -Milliseconds 600
    Get-Process ClaudeNiang -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -ieq $exe } | Stop-Process -Force
}
if ($Action -eq 'Uninstall') {
    Remove-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue
    Stop-Widget
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    Write-Output 'Claude Niang uninstalled: startup entry, Start menu shortcut and files removed.'
    return
}
Stop-Widget
& (Join-Path $PSScriptRoot 'build.ps1') -OutputRoot $dest
New-Item -ItemType Directory -Path (Join-Path $dest 'scripts') -Force | Out-Null
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $dest 'scripts') -Force
Set-Content -LiteralPath (Join-Path $dest 'Uninstall.cmd') -Encoding ASCII -Value @(
    '@echo off',
    'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" -Action Uninstall',
    'pause'
)
New-ItemProperty -Path $runKey -Name $runName -Value ('"'+$exe+'"') -PropertyType String -Force | Out-Null
$shell=New-Object -ComObject WScript.Shell
$lnk=$shell.CreateShortcut($shortcut)
$lnk.TargetPath=$exe
$lnk.WorkingDirectory=$dest
$lnk.Description='Claude Niang usage widget'
$lnk.Save()
Start-Process -FilePath $exe -WorkingDirectory $dest
Write-Output ('Installed: '+$exe)
Write-Output 'She appears at the bottom-right of the Claude window. Click her to see your usage.'
Write-Output ('Uninstall: '+(Join-Path $dest 'Uninstall.cmd'))
