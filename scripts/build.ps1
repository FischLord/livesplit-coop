param([Parameter(Mandatory=$true)][string]$LiveSplitPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
# 'C:\LiveSplit\' passed through powershell.exe arrives as C:\LiveSplit" (the backslash escapes the quote).
$LiveSplitPath=$LiveSplitPath.Trim().TrimEnd('"')
$live=(Resolve-Path -LiteralPath $LiveSplitPath).Path
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(!(Test-Path -LiteralPath $compiler)){throw 'Windows .NET Framework compiler not found.'}
$output=Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Security.dll','System.Web.Extensions.dll',(Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $live 'UpdateManager.dll'))
$arguments=@('/nologo','/target:library','/optimize+','/warnaserror+',('/out:'+(Join-Path $output 'LiveSplit.Coop.dll')))
$arguments+= $refs | ForEach-Object { '/reference:'+$_ }
$arguments+= (Get-ChildItem -LiteralPath (Join-Path $root 'component') -Filter '*.cs').FullName
& $compiler $arguments
if($LASTEXITCODE -ne 0){throw 'Component build failed.'}
Write-Output ('Built '+(Join-Path $output 'LiveSplit.Coop.dll'))
