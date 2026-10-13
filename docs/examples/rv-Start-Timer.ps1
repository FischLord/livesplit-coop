param(
    [ValidateSet('Host','Viewer')][string]$Role='Host',
    [string]$Profile,
    [switch]$PrepareOnly
)
$ErrorActionPreference='Stop'
# Only the Host app ships in the package. The Viewer app is kept in sync from it on every start (both roles):
# every file whose content differs is copied, nothing is ever deleted in the Viewer.
# Personal files, logs and the Host-only autosplitter files are left out.
function Sync-ViewerApp([string]$source,[string]$target) {
    if(!(Test-Path -LiteralPath $source)){return}
    [void][IO.Directory]::CreateDirectory($target)
    $skipNames=@('settings.cfg','coop-local.json','rvthereyet.asl','asl-help')
    $sha=[Security.Cryptography.SHA256]::Create()
    $copied=0
    foreach($file in [IO.Directory]::EnumerateFiles($source,'*',[IO.SearchOption]::AllDirectories)) {
        $rel=$file.Substring($source.Length).TrimStart('\')
        $parts=$rel.Split('\')
        $name=$parts[$parts.Length-1].ToLowerInvariant()
        if($parts[0] -eq 'Logs' -and $parts.Length -gt 1){continue}
        if($name.EndsWith('.coop-lock')){continue}
        if($parts.Length -eq 1 -and $skipNames -contains $name){continue}
        if($parts.Length -eq 2 -and $parts[0] -eq 'Components' -and ($skipNames -contains $name -or $name.EndsWith('.asl'))){continue}
        $dst=Join-Path $target $rel
        $a=New-Object IO.FileInfo $file
        $b=New-Object IO.FileInfo $dst
        if($b.Exists -and $a.Length -eq $b.Length -and $a.LastWriteTimeUtc -eq $b.LastWriteTimeUtc){continue}
        if($b.Exists -and $a.Length -eq $b.Length) {
            $ha=[BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($file)))
            $hb=[BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($dst)))
            if($ha -eq $hb){continue}
        }
        try {
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $dst))
            [IO.File]::Copy($file,$dst,$true)
            $copied++
        } catch {
            Write-Host ('Hinweis: '+$rel+' im Viewer konnte nicht aktualisiert werden (ist der Viewer offen?).') -ForegroundColor Yellow
        }
    }
    if($copied){Write-Host ('Viewer-Programm abgeglichen: '+$copied+' Dateien.')}
}
# Files from an internet-downloaded ZIP carry the Mark of the Web (Zone.Identifier stream); .NET Framework then refuses to
# load the DLLs (0x80131515). Lift it for the whole timer folder (and the CMDs next to it) before anything is loaded.
function Unblock-TimerFolder([string]$timerRoot) {
    try {
        $sentinels=@()
        foreach($f in [IO.Directory]::EnumerateFiles($timerRoot,'*',[IO.SearchOption]::AllDirectories)) {
            $ext=[IO.Path]::GetExtension($f).ToLowerInvariant()
            if($ext -in '.dll','.exe','.ps1','.json','.cmd'){$sentinels+=$f}
        }
        $cmds=@(Get-ChildItem -LiteralPath (Split-Path -Parent $timerRoot) -Filter '*.cmd' -File -ErrorAction SilentlyContinue | ForEach-Object {$_.FullName})
        $blocked=@(($sentinels+$cmds) | Where-Object {Get-Item -LiteralPath $_ -Stream Zone.Identifier -ErrorAction SilentlyContinue})
        if(!$blocked.Count){return}
        $count=0
        foreach($f in (@(Get-ChildItem -LiteralPath $timerRoot -Recurse -File -ErrorAction SilentlyContinue)+$cmds)) {
            $path=if($f -is [string]){$f}else{$f.FullName}
            if(Get-Item -LiteralPath $path -Stream Zone.Identifier -ErrorAction SilentlyContinue){
                Unblock-File -LiteralPath $path -ErrorAction SilentlyContinue
                $count++
            }
        }
        Write-Host ('Windows-Sperre fuer heruntergeladene Dateien aufgehoben: '+$count+' Dateien')
    } catch {}
}
try {
    $root=Split-Path -Parent $PSScriptRoot
    Unblock-TimerFolder $root
    $app=Join-Path $root ('Anwendung\'+$Role)
    $exe=Join-Path $app 'LiveSplit.exe'
    if(Get-Process LiveSplit -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe}) {
        throw 'Dieser Timer ist bereits offen.'
    }
    Sync-ViewerApp (Join-Path $root 'Anwendung\Host') (Join-Path $root 'Anwendung\Viewer')
    # Legacy updates of older packages (graph/category layouts, clipboard, diagnostics) must never replace this package's Coop DLL.
    # Apply-CoopLobbies.ps1 applies only their layout part and retires the pending DLL steps.
    & (Join-Path $PSScriptRoot 'Apply-CoopLobbies.ps1') -Role $Role
    foreach($dll in @('LiveSplit.Core.dll','UpdateManager.dll','Components\LiveSplit.Coop.dll')){[void][Reflection.Assembly]::UnsafeLoadFrom((Join-Path $app $dll))}
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
