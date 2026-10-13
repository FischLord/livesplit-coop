# Generates docs/images/<en|de>/*.png: the coop window and status line with example data only (no network).
# Needs a built dist\LiveSplit.Coop.dll (scripts\build.ps1) and a LiveSplit folder for LiveSplit.Core.dll.
# Run: powershell -NoProfile -STA -ExecutionPolicy Bypass -File scripts\render-doc-screenshots.ps1 -LiveSplitPath <LiveSplit folder>
param([Parameter(Mandatory=$true)][string]$LiveSplitPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$live=(Resolve-Path -LiteralPath $LiveSplitPath.Trim().TrimEnd('"')).Path
[Environment]::CurrentDirectory=$live
$env:PATH=(Join-Path $live 'x64')+';'+$env:PATH
$dlls=@((Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $live 'UpdateManager.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll'))
foreach($dll in $dlls){[void][Reflection.Assembly]::LoadFrom($dll)}
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Security.dll','System.Web.Extensions.dll')+$dlls
Add-Type -Path (Join-Path $root 'scripts\DocScreenshots.cs') -ReferencedAssemblies $refs
$images=Join-Path $root 'docs\images'
[DocScreenshots]::Run($images)
Write-Output "Screenshots written to $images"
