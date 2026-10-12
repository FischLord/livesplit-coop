param(
    [ValidateSet('Host','Viewer')][string]$Role='Host',
    [string]$Profile,
    [switch]$PrepareOnly
)
$ErrorActionPreference='Stop'
try {
    $root=Split-Path -Parent $PSScriptRoot
    $app=Join-Path $root ('Anwendung\'+$Role)
    $exe=Join-Path $app 'LiveSplit.exe'
    if(Get-Process LiveSplit -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe}) {
        throw 'Dieser Timer ist bereits offen.'
    }
    # Legacy updates of older packages (graph/category layouts, clipboard, diagnostics) must never replace this package's Coop DLL.
    # Apply-CoopLobbies.ps1 applies only their layout part and retires the pending DLL steps.
    & (Join-Path $PSScriptRoot 'Apply-CoopLobbies.ps1') -Role $Role
    foreach($dll in @('LiveSplit.Core.dll','UpdateManager.dll','Components\LiveSplit.Coop.dll')){[void][Reflection.Assembly]::LoadFrom((Join-Path $app $dll))}
    $pack=[LiveSplit.Coop.ProfilePack]::Find($app)
    if(!$pack){throw 'coop-profiles.json fehlt. Bitte das komplette Paket entpacken.'}
    $configPath=Join-Path $app 'settings.cfg'
    function Choose([string]$title,[string[]]$items,[int]$default=1) {
        Write-Host "`n$title"
        for($i=0;$i -lt $items.Count;$i++){Write-Host ('  {0}: {1}' -f ($i+1),$items[$i])}
        do {
            $answer=Read-Host ('Auswahl (Enter = '+$items[$default-1]+')')
            if(!$answer){$answer=[string]$default}
            $number=0
        } until([int]::TryParse($answer,[ref]$number) -and $number -ge 1 -and $number -le $items.Count)
        return $number
    }
    if($Role -eq 'Host') {
        $localPath=Join-Path $app 'coop-local.json'
        $local=[LiveSplit.Coop.LocalState]::Load($localPath)
        Write-Host ($pack.game+' - eigener Timer / Host')
        if($Profile) {
            $profile=$pack.ByKey($Profile)
            if(!$profile){throw ('Unbekanntes Profil: '+$Profile)}
        } else {
            $last=$pack.ByKey($local.lastProfile)
            $profile=$null
            while(!$profile) {
                $selection=New-Object 'System.Collections.Generic.Dictionary[string,string]'
                foreach($d in $pack.dimensions) {
                    $dimId=$d.id
                    $options=$d.options
                    $title=$d.DisplayLabel
                    $default=1
                    if($last){$index=0;foreach($o in $options){$index++;if($o.id -eq $last.select[$dimId]){$default=$index}}}
                    $items=@($options | ForEach-Object {$_.label})
                    $choice=Choose $title $items $default
                    $selection[$dimId]=$options[$choice-1].id
                }
                $profile=$pack.Resolve($selection)
                if(!$profile){Write-Host 'Fuer diese Kombination gibt es keine Splits.' -ForegroundColor Yellow}
            }
        }
        $local.lastProfile=$pack.Key($profile)
        [void]$local.Save($localPath)
        [void][LiveSplit.Coop.ProfilePreparer]::Prepare($pack,$profile,'host')
        $runPath=$pack.FullPath($profile.splits);$layoutPath=$pack.FullPath($profile.layout)
        [LiveSplit.Coop.ProfilePreparer]::RememberInSettings($configPath,$runPath,$pack.game,$profile.category)
        Write-Host ('RV There Yet? - Host. Profil: '+$profile.label+' ('+$profile.splits+')')
        Write-Host 'Lobby waehlst du im Timer: Rechtsklick > Coop...'
    } else {
        $layoutPath=$pack.FullPath($pack.viewerLayout)
        $runPath=Join-Path $root 'Splits\Viewer.lss'
        $layout=New-Object Xml.XmlDocument;$layout.Load($layoutPath)
        if($layout.SelectNodes('/Layout/Components/Component[Path="LiveSplit.ScriptableAutoSplit.dll" or Path="LiveSplit.AutoSplittingRuntime.dll"]').Count){throw 'Viewer-Layout darf keinen Autosplitter enthalten.'}
        $roleNode=$layout.SelectSingleNode('/Layout/Components/Component[Path="LiveSplit.Coop.dll"]/Settings/Role')
        if(!$roleNode){throw 'Coop-Komponente fehlt im Viewer-Layout.'}
        $roleNode.InnerText='viewer';$layout.Save($layoutPath)
        Write-Host 'RV Coop - Mitspieler. Lobby beitreten: Rechtsklick > Coop...'
    }
    if($PrepareOnly){Write-Output ('Vorbereitet: '+$runPath);exit 0}
    Start-Process -FilePath $exe -WorkingDirectory $app -ArgumentList ('-s "'+$runPath+'" -l "'+$layoutPath+'"')
} catch {
    Write-Host ('Fehler: '+$_.Exception.Message) -ForegroundColor Red
    exit 1
}
