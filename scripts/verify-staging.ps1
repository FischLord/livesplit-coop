param(
    [Parameter(Mandatory=$true)][string]$TimerRoot,
    [string]$CompareTo
)
$ErrorActionPreference='Stop'
$app=Join-Path $TimerRoot 'Anwendung\Host'
foreach($dll in @('LiveSplit.Core.dll','UpdateManager.dll','Components\LiveSplit.Coop.dll')){[void][Reflection.Assembly]::LoadFrom((Join-Path $app $dll))}
# The native livesplit_core.dll lives in x64\ next to the app; make it loadable for the run factory.
$env:PATH=(Join-Path $app 'x64')+';'+$env:PATH
$pack=[LiveSplit.Coop.ProfilePack]::Find($app)
if(!$pack){throw 'no pack'}
# Fingerprint of everything that counts as personal history: attempts, attempt history, all segment times.
function Fingerprint([string]$path){
    $x=New-Object Xml.XmlDocument;$x.Load($path)
    $parts=@($x.SelectSingleNode('/Run/AttemptCount').InnerText,$x.SelectSingleNode('/Run/AttemptHistory').OuterXml,$x.SelectSingleNode('/Run/Segments').OuterXml)
    return ($parts -join '|')
}
$failed=0
foreach($p in $pack.profiles){
    $splits=$pack.FullPath($p.splits)
    # With -CompareTo, the "before" state is the original file (read only), not the staging copy.
    if($CompareTo){ $before=Fingerprint (Join-Path $CompareTo $p.splits) } else { $before=Fingerprint $splits }
    [LiveSplit.Coop.ProfilePreparer]::Prepare($pack,$p,'host')
    $after=Fingerprint $splits
    $run=$null
    $stream=[IO.File]::OpenRead($splits)
    try { $run=(New-Object LiveSplit.Model.RunFactories.StandardFormatsRunFactory($stream,$splits)).Create((New-Object LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory)) } finally { $stream.Dispose() }
    $ok=($before -eq $after) -and $run.CategoryName -eq $p.category
    foreach($k in $p.variables.Keys){ if($run.Metadata.VariableValueNames[$k] -ne $p.variables[$k]){$ok=$false} }
    if($pack.Match($pack.game,$run.CategoryName,$run.Metadata.VariableValueNames) -ne $p){$ok=$false}
    if(!$ok){$failed++;Write-Output ('FAIL '+$p.splits)} else {Write-Output ('ok   '+$p.splits)}
}
if($failed){throw "$failed profiles failed"}
Write-Output ('ALL '+$pack.profiles.Count+' PROFILES OK')
