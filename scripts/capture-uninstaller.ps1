param([Parameter(Mandatory)][string]$File,[ValidateSet('Uninstaller','Installer')][string]$Stage='Uninstaller')
$ErrorActionPreference='Stop'
# Inno calls this only to prepare its generated uninstaller for external signing.
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cache=[IO.Path]::GetFullPath((Join-Path $root 'build/signpath/uninstaller-cache'))
$target=[IO.Path]::GetFullPath($File)
if($Stage -eq 'Installer' -and [IO.Path]::GetDirectoryName($target) -eq (Join-Path $root 'build/releases')){
    $destination=Join-Path $root 'build/signpath/installer-input'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $target -Destination (Join-Path $destination 'installer.exe')
    exit 23
}
if([IO.Path]::GetDirectoryName($target) -ne $cache){throw 'Unexpected Inno uninstaller path'}
if(!(Test-Path -LiteralPath $target -PathType Leaf)){throw 'Generated uninstaller missing'}
if($Stage -eq 'Installer'){
    $record=Get-Content -LiteralPath (Join-Path $root 'build/signpath/uninstaller.json') -Raw | ConvertFrom-Json
    if($record.Sha256 -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLower()){throw 'Inno generated a different uninstaller'}
    $approved=Join-Path $root 'build/signpath/approved-uninstaller/uninstaller.exe'
    & (Join-Path $PSScriptRoot 'verify-signatures.ps1') -Directory (Split-Path -Parent $approved) -ExpectedFiles @('uninstaller.exe')
    Copy-Item -LiteralPath $approved -Destination $target -Force
    exit 0
}
$inputDir=Join-Path $root 'build/signpath/uninstaller-input'
New-Item -ItemType Directory -Path $inputDir -Force | Out-Null
Copy-Item -LiteralPath $target -Destination (Join-Path $inputDir 'uninstaller.exe')
@{CacheFile=$target;Sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLower()} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'build/signpath/uninstaller.json') -Encoding utf8
# Stop compilation deliberately. A later compile consumes the verified signed cache.
exit 23
