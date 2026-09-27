param([Parameter(Mandatory=$true)][string]$LiveSplitPath,[Parameter(Mandatory=$true)][string]$LayoutPath,[switch]$KeepOpen,[string]$RelayAddress,[string]$RoomsFile)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$source=(Resolve-Path -LiteralPath $LiveSplitPath).Path
$layoutSource=(Resolve-Path -LiteralPath $LayoutPath).Path
$session=Join-Path $root ('.local\trio-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $session -Force | Out-Null
$originals=Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.lss','.lsl' -or $_.Name -eq 'settings.cfg' } | Get-FileHash
$originals | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $session 'original-hashes.json') -Encoding UTF8
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$arguments=@('/nologo','/target:library','/optimize+',('/out:'+(Join-Path $session 'LiveSplit.Coop.UiTest.dll')))
$arguments+=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Web.Extensions.dll',(Join-Path $source 'LiveSplit.Core.dll'),(Join-Path $source 'UpdateManager.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll')) | ForEach-Object { '/reference:'+$_ }
$arguments+=(Join-Path $root 'tests\UiDriver.cs')
& $compiler $arguments
if($LASTEXITCODE -ne 0){throw 'Test driver build failed.'}
$relay=$null;$roomName='test';$hostKey='h'*32;$viewerKey='v'*32
if($RelayAddress -or $RoomsFile) {
    if(!$RelayAddress -or !$RoomsFile -or $RelayAddress -notmatch '^wss://'){throw 'External tests require a WSS address and private rooms file.'}
    $roomConfig=@(Get-Content -LiteralPath $RoomsFile -Raw | ConvertFrom-Json)[0]
    $endpoint=$RelayAddress;$roomName=$roomConfig.name;$hostKey=$roomConfig.hostKey;$viewerKey=$roomConfig.viewerKey
} else {
$relayStart=New-Object Diagnostics.ProcessStartInfo
$relayStart.FileName=(Get-Command node.exe).Source
$relayStart.Arguments='"'+(Join-Path $root 'relay\test\native-fixture.mjs')+'"'
$relayStart.UseShellExecute=$false;$relayStart.CreateNoWindow=$true;$relayStart.RedirectStandardOutput=$true
$relay=[Diagnostics.Process]::Start($relayStart)
$endpoint=$relay.StandardOutput.ReadLine()
if($endpoint -notmatch '^ws://127\.0\.0\.1:\d+/coop$'){throw 'Relay startup failed.'}
}
Add-Type -AssemblyName System.Security
$copies=@();$processes=@();$checks=New-Object Collections.Generic.List[string]
$script:commandId=0
function Read-State([int]$index) { Get-Content -LiteralPath (Join-Path $copies[$index] 'ui-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json }
function Command([int]$index,[string]$action,[double]$seconds=0,[string]$name='capture') {
    $script:commandId++
    $json=@{id=$script:commandId;action=$action;seconds=$seconds;name=$name} | ConvertTo-Json -Compress
    $file=Join-Path $copies[$index] 'ui-command.json';$temp=$file+'.tmp'
    [IO.File]::WriteAllText($temp,$json);Move-Item -LiteralPath $temp -Destination $file -Force
    $limit=[DateTime]::UtcNow.AddSeconds(8)
    do { Start-Sleep -Milliseconds 100;try{$s=Read-State $index}catch{continue};if($s.commandId -eq $script:commandId){if($s.error){throw $s.error};return} }while([DateTime]::UtcNow -lt $limit)
    throw "Command timeout: $index $action"
}
function Wait-Condition([scriptblock]$condition,[string]$description,[int]$timeout=10) {
    $until=[DateTime]::UtcNow.AddSeconds($timeout)
    do { try{if(& $condition){$checks.Add($description);Write-Output ('PASS: '+$description);return}}catch{};Start-Sleep -Milliseconds 120 }while([DateTime]::UtcNow -lt $until)
    throw ('Condition failed: '+$description)
}
try {
    for($i=0;$i -lt 3;$i++) {
        $folder=Join-Path $session ('player-'+$i);New-Item -ItemType Directory -Path $folder | Out-Null;$copies+=,$folder
        Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.dll','.exe','.config' } | Copy-Item -Destination $folder
        foreach($sub in @('Components','x64','x86','Resources')) { if(Test-Path -LiteralPath (Join-Path $source $sub)){Copy-Item -LiteralPath (Join-Path $source $sub) -Destination $folder -Recurse} }
        Copy-Item -LiteralPath (Join-Path $root 'dist\LiveSplit.Coop.dll'),(Join-Path $session 'LiveSplit.Coop.UiTest.dll') -Destination (Join-Path $folder 'Components')
        [IO.File]::WriteAllText((Join-Path $folder 'window-index.txt'),[string]$i)
        $config=New-Object Xml.XmlDocument;$config.Load((Join-Path $source 'settings.cfg'))
        foreach($node in @($config.SelectNodes('//RecentSplits|//RecentLayouts|//ActiveAutoSplitters|//RaceProviderPlugins'))){$node.RemoveAll()}
        foreach($node in $config.SelectNodes('//GlobalHotkeysEnabled|//WarnOnReset')){$node.InnerText='False'}
        foreach($node in $config.SelectNodes('//SplitKey|//ResetKey|//SkipKey|//UndoKey|//PauseKey|//SwitchComparisonPrevious|//SwitchComparisonNext')){$node.InnerText=''}
        $config.Save((Join-Path $folder 'settings.cfg'))
        $run=New-Object Xml.XmlDocument
        $run.LoadXml('<Run version="1.7.0"><GameIcon/><GameName>Coop UI Test</GameName><CategoryName>SIMULATED TEST</CategoryName><Metadata><Run id=""/><Platform usesEmulator="False"/><Region/><Variables/><CustomVariables/></Metadata><Offset>00:00:00</Offset><AttemptCount>0</AttemptCount><AttemptHistory/><Segments/><AutoSplitterSettings/></Run>')
        for($n=1;$n -le 5;$n++) {
            $segment=$run.CreateElement('Segment');$name=if($n -eq 5){'Finish'}else{'Checkpoint '+$n}
            $time=[TimeSpan]::FromSeconds(10*$n).ToString('c')
            $segment.InnerXml='<Name>'+ $name +'</Name><Icon/><SplitTimes><SplitTime name="Personal Best"><RealTime>'+$time+'</RealTime></SplitTime></SplitTimes><BestSegmentTime><RealTime>00:00:08</RealTime></BestSegmentTime><SegmentHistory/>'
            [void]$run.SelectSingleNode('/Run/Segments').AppendChild($segment)
        }
        $run.Save((Join-Path $folder 'test.lss'))
        $layout=New-Object Xml.XmlDocument;$layout.Load($layoutSource)
        foreach($c in @($layout.SelectNodes('/Layout/Components/Component[Path="LiveSplit.ScriptableAutoSplit.dll" or Path="LiveSplit.WorldRecord.dll" or Path="LiveSplit.Coop.dll"]'))){[void]$c.ParentNode.RemoveChild($c)}
        $role=if($i -eq 0){'host'}else{'viewer'};$key=if($i -eq 0){$hostKey}else{$viewerKey}
        $protected=[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($key),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
        $coop=$layout.CreateElement('Component');$coop.InnerXml='<Path>LiveSplit.Coop.dll</Path><Settings><Version>1</Version><Address>'+[Security.SecurityElement]::Escape($endpoint)+'</Address><Room>'+[Security.SecurityElement]::Escape($roomName)+'</Room><Role>'+$role+'</Role><ProtectedKey>'+$protected+'</ProtectedKey></Settings>'
        [void]$layout.Layout.Components.PrependChild($coop)
        $driver=$layout.CreateElement('Component');$driver.InnerXml='<Path>LiveSplit.Coop.UiTest.dll</Path><Settings/>';[void]$layout.Layout.Components.AppendChild($driver)
        $layout.Save((Join-Path $folder 'test.lsl'))
        $p=Start-Process -FilePath (Join-Path $folder 'LiveSplit.exe') -ArgumentList '-s test.lss -l test.lsl' -WorkingDirectory $folder -PassThru
        $processes+=,$p
    }
    Write-Output ('SESSION: '+$session)
    @($processes | ForEach-Object { @{id=$_.Id;path=$_.Path} }) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $session 'processes.json')
    Wait-Condition { (Test-Path -LiteralPath (Join-Path $copies[0] 'ui-state.json')) -and (Test-Path -LiteralPath (Join-Path $copies[1] 'ui-state.json')) -and (Test-Path -LiteralPath (Join-Path $copies[2] 'ui-state.json')) } 'Three real LiveSplit windows loaded the component' 35
    for($i=0;$i -lt 3;$i++){Command $i connect}
    Wait-Condition { (Read-State 1).label -match 'Following host' -and (Read-State 2).label -match 'Following host' } 'Both viewers follow the host'
    Wait-Condition { (Read-State 1).formHeight -gt 400 -and (Read-State 2).formHeight -gt 400 } 'Viewer dashboards keep their full height'
    Command 0 start
    Wait-Condition { (Read-State 1).snapshot.phase -eq 'Running' -and (Read-State 2).snapshot.phase -eq 'Running' } 'Start reaches both windows'
    Command 0 split 9
    Command 0 split 21
    Wait-Condition { (Read-State 1).snapshot.index -eq 2 -and (Read-State 2).snapshot.index -eq 2 } 'Two checkpoints reach both windows'
    $h=Read-State 0
    Wait-Condition { (Read-State 1).snapshot.segments[1].splitRT -eq $h.snapshot.segments[1].splitRT -and (Read-State 2).snapshot.segments[0].pbRT -eq 100000000 } 'Exact split values and PB comparisons agree'
    Command 0 pause
    Wait-Condition { (Read-State 1).snapshot.phase -eq 'Paused' -and (Read-State 2).snapshot.phase -eq 'Paused' } 'Pause freezes both windows'
    for($i=0;$i -lt 3;$i++){Command $i capture 0 'paused'}
    Command 0 pause
    Command 2 network-drop
    Wait-Condition { (Read-State 2).label -match 'OFFLINE' } 'Disconnected viewer visibly reports offline'
    Command 0 split 31
    Wait-Condition { (Read-State 2).label -match 'Following host' -and (Read-State 2).snapshot.index -eq 3 } 'Viewer reconnects and recovers missed checkpoint' 12
    Command 0 skip
    Wait-Condition { (Read-State 1).snapshot.index -eq 4 -and $null -eq (Read-State 1).snapshot.segments[3].splitRT } 'Skipped checkpoint stays empty'
    Command 0 undo
    Wait-Condition { (Read-State 2).snapshot.index -eq 3 } 'Undo restores the correct checkpoint'
    Command 0 split 39
    Command 0 split 49
    Wait-Condition { (Read-State 1).snapshot.phase -eq 'Ended' -and (Read-State 2).snapshot.phase -eq 'Ended' } 'All three windows finish'
    $h=Read-State 0
    Wait-Condition { (Read-State 1).snapshot.realTicks -eq $h.snapshot.realTicks -and (Read-State 2).snapshot.realTicks -eq $h.snapshot.realTicks } 'Final times agree exactly in 100 ns ticks'
    for($i=0;$i -lt 3;$i++){Command $i capture 0 'finished'}
    Command 2 disconnect
    Wait-Condition { (Read-State 2).snapshot.phase -eq 'NotRunning' -and [IO.Path]::GetFileName((Read-State 2).runFile) -eq 'test.lss' } 'Disconnect restores the viewer original run'
    Command 2 connect
    Wait-Condition { (Read-State 2).snapshot.phase -eq 'Ended' -and (Read-State 2).snapshot.realTicks -eq $h.snapshot.realTicks } 'Joining after finish retrieves the final result'
    Command 0 reset
    Wait-Condition { (Read-State 1).snapshot.phase -eq 'NotRunning' -and (Read-State 2).snapshot.index -eq -1 } 'Reset reaches both viewers'
    foreach($item in $originals){if((Get-FileHash -LiteralPath $item.Path).Hash -ne $item.Hash){throw ('Original changed: '+$item.Path)}}
    $checks.Add('Original splits, layouts and settings hashes unchanged')
    $checks | Set-Content -LiteralPath (Join-Path $session 'checks.txt') -Encoding UTF8
    Write-Output ('Checks passed: '+$checks.Count)
} finally {
    if(!$KeepOpen){foreach($p in $processes){if(!$p.HasExited){$p.Kill()}};if($relay -and !$relay.HasExited){$relay.Kill()}}
    Write-Output ('Artifacts: '+$session)
}
