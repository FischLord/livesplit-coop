param([string]$Profile,[switch]$PrepareOnly)
$ErrorActionPreference='Stop'
try {
    $root=$PSScriptRoot
    if(!$PrepareOnly -and (Get-Process LiveSplit -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $root 'LiveSplit.exe')})){throw 'Dieses LiveSplit-Paket ist bereits offen. Erst schliessen, dann Profil wechseln.'}
    $package=Get-Content -LiteralPath (Join-Path $root 'Paket.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $access=Get-Content -LiteralPath (Join-Path $root 'Zugang.private.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if(!$Profile) {
        Write-Host ('RV There Yet - Coop '+$access.role)
        for($i=0;$i -lt $package.profiles.Count;$i++){Write-Host ('  '+($i+1)+': '+$package.profiles[$i].title)}
        if($package.profiles.Count -eq 1){$Profile=$package.profiles[0].id}
        else {
            $answer=Read-Host 'Auswahl (Enter = 1)';if(!$answer){$answer='1'}
            $number=0
            if(![int]::TryParse($answer,[ref]$number) -or $number -lt 1 -or $number -gt $package.profiles.Count){throw 'Ungueltige Auswahl.'}
            $Profile=$package.profiles[$number-1].id
        }
    }
    $selected=@($package.profiles | Where-Object {$_.id -eq $Profile})
    if($selected.Count -ne 1){throw 'Profil nicht gefunden.'}
    $selected=$selected[0]
    # Resolve within this portable package; no path from the original PC is reused.
    function PackagePath([string]$relative) {
        $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
        if(!$path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Datei liegt ausserhalb des Pakets.'}
        return $path
    }
    $layoutPath=PackagePath $selected.layout;$runPath=PackagePath $selected.run
    Add-Type -AssemblyName System.Security
    $protected=[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($access.key),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $layout=New-Object Xml.XmlDocument;$layout.Load($layoutPath)
    $settings=$layout.SelectSingleNode('/Layout/Components/Component[Path="LiveSplit.Coop.dll"]/Settings')
    if(!$settings){throw 'Coop-Komponente fehlt im Layout.'}
    foreach($entry in @{Address=$access.address;Room=$access.room;Role=$access.role;ProtectedKey=$protected}.GetEnumerator()) {
        $node=$settings.SelectSingleNode($entry.Key)
        if(!$node){$node=$layout.CreateElement($entry.Key);[void]$settings.AppendChild($node)}
        $node.InnerText=$entry.Value
    }
    foreach($node in $layout.SelectNodes('//ScriptPath')) {
        $node.InnerText=PackagePath ('Components\'+[IO.Path]::GetFileName($node.InnerText))
        if(!(Test-Path -LiteralPath $node.InnerText)){throw 'Autosplitter-Datei fehlt.'}
    }
    $layout.Save($layoutPath)
    $run=New-Object Xml.XmlDocument;$run.Load($runPath)
    $pathNode=$run.SelectSingleNode('/Run/LayoutPath')
    if(!$pathNode){$pathNode=$run.CreateElement('LayoutPath');[void]$run.DocumentElement.AppendChild($pathNode)}
    $pathNode.InnerText=$layoutPath;$run.Save($runPath)
    if($PrepareOnly){Write-Output ('Prepared '+$selected.id+' ('+$access.role+')');exit 0}
    Write-Host 'In LiveSplit: Rechtsklick -> Coop: Connect.'
    if($access.role -eq 'host'){Write-Host 'Erst bei Host connected den Spiel-Run starten. Nach dem Lauf Splits speichern.'}
    else{Write-Host 'Following host = verbunden. Der Host steuert Start, Splits und Ende.'}
    Start-Process -FilePath (Join-Path $root 'LiveSplit.exe') -WorkingDirectory $root -ArgumentList ('-s "'+$runPath+'" -l "'+$layoutPath+'"')
} catch {
    Write-Host ('Fehler: '+$_.Exception.Message) -ForegroundColor Red
    if(!$PrepareOnly){[void](Read-Host 'Enter zum Schliessen')}
    exit 1
}
