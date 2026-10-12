param([Parameter(Mandatory=$true)][string]$LiveSplitPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$live=(Resolve-Path -LiteralPath $LiveSplitPath).Path
foreach($file in @((Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $live 'UpdateManager.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll'))){[void][Reflection.Assembly]::LoadFrom($file)}
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Web.Extensions.dll',(Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll'))
Add-Type -Path (Join-Path $root 'tests\DiagnosticsTests.cs') -ReferencedAssemblies $refs
[DiagnosticsTests]::Run((Join-Path $root ('.local\diagnostics-tests\'+[Guid]::NewGuid().ToString('N'))))
