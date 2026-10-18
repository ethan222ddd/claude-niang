param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $root 'build' }
New-Item -ItemType Directory -Force $OutputRoot | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$refs = @(
  "$wpf\PresentationCore.dll", "$wpf\PresentationFramework.dll", "$wpf\WindowsBase.dll",
  'System.Xaml.dll', 'System.Net.Http.dll', 'System.Web.Extensions.dll', 'System.Drawing.dll', 'System.dll', 'System.Core.dll'
) | ForEach-Object { "/r:$_" }

$exe = Join-Path $OutputRoot 'ClaudeNiang.exe'
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$exe" @refs (Join-Path $root 'src\ClaudeNiang.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Copy-Item (Join-Path $root 'assets\claude-niang.png') $OutputRoot -Force
Copy-Item (Join-Path $root 'assets\quotes.json') $OutputRoot -Force
Write-Host "Built $exe"
