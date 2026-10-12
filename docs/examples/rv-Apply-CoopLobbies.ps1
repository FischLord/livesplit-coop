param([Parameter(Mandatory=$true)][ValidateSet('Host','Viewer')][string]$Role)
# Handles legacy update steps (graph-category, clipboard, diagnostics) that older packages left pending.
# The Coop DLL of this package already contains all of those fixes, so this script NEVER copies a DLL.
# Only the layout part of the graph/category patch (graph colour, rainbow, "PB +/-" column) is still applied.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$updates=Join-Path $PSScriptRoot 'Updates'
$utf8=New-Object Text.UTF8Encoding($true)
function Save-Json($object,[string]$path,[int]$depth=6) { [IO.File]::WriteAllText($path,($object | ConvertTo-Json -Depth $depth),$utf8) }
function Read-Json([string]$path) { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
$changed=$false

# 1. graph-category.json: layouts only, no DLL.
$graph=Join-Path $updates 'graph-category.json'
if(Test-Path -LiteralPath $graph) {
    $metadata=Read-Json $graph
    if($Role -in @($metadata.pendingRoles)) {
        $exe=Join-Path $root ('Anwendung\'+$Role+'\LiveSplit.exe')
        if(Get-Process LiveSplit -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe}) { throw 'Bitte diesen Timer schliessen, dann erneut starten.' }
        $backup=Join-Path $PSScriptRoot 'GraphCategoryFix\backup'
        [void][IO.Directory]::CreateDirectory($backup)
        $layouts=if($Role -eq 'Viewer'){@('Viewer.lsl')}else{@('Host-Valley.lsl','Host-Snow.lsl','Host-Bay.lsl','Host-Finish.lsl')}
        foreach($name in $layouts) {
            $path=Join-Path $root ('Layouts\'+$name)
            if(!(Test-Path -LiteralPath $path)){continue}
            $saved=Join-Path $backup $name
            if(!(Test-Path -LiteralPath $saved)){Copy-Item -LiteralPath $path -Destination $saved}
            $doc=New-Object Xml.XmlDocument;$doc.PreserveWhitespace=$true;$doc.Load($path)
            $color=$doc.SelectSingleNode('/Layout/Components/Component[Path="LiveSplit.Graph.dll"]/Settings/GraphColor')
            if($color){$color.InnerText='FFEAF3FA'}
            $rainbow=$doc.SelectSingleNode('/Layout/Settings/UseRainbowColor')
            if(!$rainbow){$rainbow=$doc.CreateElement('UseRainbowColor');[void]$doc.Layout.Settings.AppendChild($rainbow)}
            $rainbow.InnerText='True'
            foreach($column in $doc.SelectNodes('/Layout/Components/Component[Path="LiveSplit.Splits.dll"]/Settings/Columns/Settings[Type="SegmentDelta" or Type="SegmentDeltaorSegmentTime" or Type="Delta" or Type="DeltaorSplitTime"]')) {
                foreach($field in @('Name','Type','Comparison')) {
                    $node=$column.SelectSingleNode($field)
                    if(!$node){$node=$doc.CreateElement($field);[void]$column.AppendChild($node)}
                    $node.InnerText=@{Name='PB +/-';Type='Delta';Comparison='Personal Best'}[$field]
                }
            }
            $doc.Save($path)
        }
        $metadata.pendingRoles=@($metadata.pendingRoles | Where-Object {$_ -ne $Role})
        Save-Json $metadata $graph
        $changed=$true
        Write-Host ('Graph- und Kategorie-Layoutanpassung fuer '+$Role+' angewendet (ohne DLL-Austausch).')
    }
}

# 2. diagnostics.json: the new DLL already contains the diagnostics, only clear the pending flag.
$diag=Join-Path $updates 'diagnostics.json'
if(Test-Path -LiteralPath $diag) {
    $metadata=Read-Json $diag
    if($Role -in @($metadata.pendingRoles)) {
        $metadata.pendingRoles=@($metadata.pendingRoles | Where-Object {$_ -ne $Role})
        Save-Json $metadata $diag
        $changed=$true
    }
}

# 3. clipboard-fix.json: would copy an old DLL over both roles; retire it for good.
$clip=Join-Path $updates 'clipboard-fix.json'
if(Test-Path -LiteralPath $clip) {
    Move-Item -LiteralPath $clip -Destination ($clip+'.superseded-by-coop-lobbies') -Force
    $changed=$true
}

# 4. Record the installed component in the version file (only when it exists).
$versionPath=Join-Path $PSScriptRoot 'coop-version.json'
$installed=Join-Path $root ('Anwendung\'+$Role+'\Components\LiveSplit.Coop.dll')
if($changed -and (Test-Path -LiteralPath $versionPath) -and (Test-Path -LiteralPath $installed)) {
    $version=Read-Json $versionPath
    $version | Add-Member -NotePropertyName sha256 -NotePropertyValue (Get-FileHash -LiteralPath $installed).Hash -Force
    $version | Add-Member -NotePropertyName localPatch -NotePropertyValue 'coop-lobbies.1' -Force
    $version | Add-Member -NotePropertyName diagnosticsPendingRoles -NotePropertyValue @() -Force
    Save-Json $version $versionPath
}
