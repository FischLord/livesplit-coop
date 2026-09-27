param([Parameter(Mandatory=$true)][string]$LiveSplitPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$live=(Resolve-Path -LiteralPath $LiveSplitPath).Path
[Environment]::CurrentDirectory=$live
$env:PATH=(Join-Path $live 'x64')+';'+$env:PATH
foreach($dll in @((Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $live 'UpdateManager.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll'))){[Reflection.Assembly]::LoadFrom($dll)|Out-Null}
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll',(Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll'))
Add-Type -Path (Join-Path $root 'tests\NativeTests.cs') -ReferencedAssemblies $refs
$start=New-Object System.Diagnostics.ProcessStartInfo
$start.FileName=(Get-Command node.exe).Source
$start.Arguments='"'+(Join-Path $root 'relay\test\native-fixture.mjs')+'"'
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true
$process=[Diagnostics.Process]::Start($start)
try {
    $endpoint=$process.StandardOutput.ReadLine()
    if($endpoint -notmatch '^ws://127\.0\.0\.1:\d+/coop$'){throw 'Local test relay failed to start.'}
    [NativeTests]::Run($endpoint)
} finally { if(!$process.HasExited){$process.Kill()};$process.Dispose() }
