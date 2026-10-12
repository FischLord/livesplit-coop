param([Parameter(Mandatory=$true)][string]$LiveSplitPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$live=(Resolve-Path -LiteralPath $LiveSplitPath.Trim().TrimEnd('"')).Path
[Environment]::CurrentDirectory=$live
$env:PATH=(Join-Path $live 'x64')+';'+$env:PATH
$dlls=@((Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $live 'UpdateManager.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll'))
foreach($dll in $dlls){[void][Reflection.Assembly]::LoadFrom($dll)}
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Security.dll','System.Web.Extensions.dll')+$dlls
$work=Join-Path $root '.local\lobby-tests';[void][IO.Directory]::CreateDirectory($work)
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=(Get-Command node.exe).Source
$start.Arguments='"'+(Join-Path $root 'relay-cloudflare\test\native-fixture.mjs')+'"'
$start.WorkingDirectory=Join-Path $root 'relay-cloudflare'
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true
$relay=[Diagnostics.Process]::Start($start)
try {
    Add-Type -Path (Join-Path $root 'tests\LobbyTests.cs') -ReferencedAssemblies $refs
    $endpoint=$relay.StandardOutput.ReadLine()
    if($endpoint -notmatch '^ws://127\.0\.0\.1:\d+/coop$'){throw 'Local Cloudflare relay did not start: '+$endpoint}
    [LobbyTests]::Run($endpoint,$work)
    Write-Output 'ALL LOBBY TESTS PASSED'
} finally {
    if(!$relay.HasExited){& taskkill /pid $relay.Id /T /F | Out-Null}
    $relay.Dispose()
}
