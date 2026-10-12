param([Parameter(Mandatory=$true)][string]$LiveSplitPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$live=(Resolve-Path -LiteralPath $LiveSplitPath.Trim().TrimEnd('"')).Path
[Environment]::CurrentDirectory=$live
$env:PATH=(Join-Path $live 'x64')+';'+$env:PATH
$dlls=@((Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $live 'UpdateManager.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll'))
foreach($dll in $dlls){[void][Reflection.Assembly]::LoadFrom($dll)}
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Security.dll','System.Web.Extensions.dll')+$dlls
Add-Type -Path (Join-Path $root 'tests\RecorderTests.cs') -ReferencedAssemblies $refs
$work=Join-Path $root '.local\recorder-tests'
if(Test-Path -LiteralPath $work){Remove-Item -LiteralPath $work -Recurse -Force}
[void][IO.Directory]::CreateDirectory($work)
[RecorderTests]::Run($work)
Write-Output 'ALL RECORDER TESTS PASSED'
