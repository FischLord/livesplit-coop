param([Parameter(Mandatory=$true)][string]$LiveSplitPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$live=(Resolve-Path -LiteralPath $LiveSplitPath.Trim().TrimEnd('"')).Path
$output=Join-Path $root '.local\clipboard-tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$test=Join-Path $output 'ClipboardTests.exe'
& $compiler /nologo /target:exe "/out:$test" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $root 'tests\ClipboardTests.cs')
if($LASTEXITCODE -ne 0){throw 'Clipboard test build failed.'}
# Holds the real Windows clipboard briefly from a separate hidden process; never replaces its contents.
& $test (Join-Path $root 'dist\LiveSplit.Coop.dll') $live
if($LASTEXITCODE -ne 0){throw 'Clipboard regression tests failed.'}
