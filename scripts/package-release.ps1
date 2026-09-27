param([Parameter(Mandatory=$true)][string]$LiveSplitPath,[string]$Version='0.1.0-beta.2')
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+(-[a-z0-9.]+)?$'){throw 'Invalid release version.'}
$root=Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -LiveSplitPath $LiveSplitPath
if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'dist\LiveSplit.Coop.dll')).ProductVersion -ne $Version){throw 'Component version does not match release version.'}
if((Get-Content -LiteralPath (Join-Path $root 'relay\package.json') -Raw | ConvertFrom-Json).version -ne $Version){throw 'Relay version does not match release version.'}
$output=Join-Path $root ('dist\release-v'+$Version)
if(Test-Path -LiteralPath $output){throw 'Release output exists; refusing to mix release artifacts.'}
$component=Join-Path $output 'component';$relay=Join-Path $output 'relay'
New-Item -ItemType Directory -Path $component,$relay | Out-Null
function Include([string]$destination,[string]$relative) {
    $target=Join-Path $destination $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $target
}
# Explicit allowlists: never recurse through a working tree or a user's LiveSplit.
$docs=@('README.md','LICENSE','THIRD_PARTY.md','docs\quickstart.md','docs\setup.de.md','docs\deployment.md','docs\protocol.md','docs\portable.md','docs\verification.md',('docs\releases\v'+$Version+'.md'))
foreach($destination in @($component,$relay)){foreach($file in $docs){Include $destination $file}}
New-Item -ItemType Directory -Path (Join-Path $component 'Components') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'dist\LiveSplit.Coop.dll') -Destination (Join-Path $component 'Components')
foreach($file in @('compose.yaml','compose.tls.yaml','relay\Dockerfile','relay\package.json','relay\package-lock.json','relay\src\main.mjs','relay\src\server.mjs','relay\src\protocol.mjs','relay\src\create-room.mjs','relay\src\tls.mjs')){Include $relay $file}
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
function Archive([string]$folder,[string]$path) {
    $zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($file in Get-ChildItem -LiteralPath $folder -Recurse -File) {
            $name=$file.FullName.Substring($folder.Length+1).Replace('\','/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$name,[IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {$zip.Dispose()}
}
$componentZip=Join-Path $output ('LiveSplit.Coop-v'+$Version+'.zip')
$relayZip=Join-Path $output ('LiveSplit.Coop-relay-v'+$Version+'.zip')
Archive $component $componentZip
Archive $relay $relayZip
Get-FileHash -LiteralPath $componentZip,$relayZip | ForEach-Object {$_.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_.Path)} | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
Write-Output ('Public release artifacts: '+$output)
