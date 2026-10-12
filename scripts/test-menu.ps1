param([Parameter(Mandatory=$true)][string]$LiveSplitPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$live=(Resolve-Path -LiteralPath $LiveSplitPath).Path
[Environment]::CurrentDirectory=$live
$env:PATH=(Join-Path $live 'x64')+';'+$env:PATH
foreach($file in @((Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $live 'UpdateManager.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll'))){[void][Reflection.Assembly]::LoadFrom($file)}
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll',(Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll'))
Add-Type -Path (Join-Path $root 'tests\MenuTests.cs') -ReferencedAssemblies $refs
$output=Join-Path $root '.local\menu-tests';[void][IO.Directory]::CreateDirectory($output)
$clipboardOutput=Join-Path $root '.local\clipboard-tests';[void][IO.Directory]::CreateDirectory($clipboardOutput)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe ('/out:'+(Join-Path $clipboardOutput 'ClipboardTests.exe')) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $root 'tests\ClipboardTests.cs')
if($LASTEXITCODE -ne 0){throw 'Clipboard lock helper build failed'}
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=(Get-Command node.exe).Source
$start.Arguments='"'+(Join-Path $root 'relay\test\native-fixture.mjs')+'"'
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true
$relay=[Diagnostics.Process]::Start($start)
try {
    $endpoint=$relay.StandardOutput.ReadLine()
    if($endpoint -notmatch '^ws://127\.0\.0\.1:\d+/coop$'){throw 'Local test relay did not start'}
    [MenuTests]::Run($endpoint,$output)
} finally {if(!$relay.HasExited){$relay.Kill()};$relay.Dispose()}
