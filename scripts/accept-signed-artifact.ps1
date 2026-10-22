param([Parameter(Mandatory)][ValidateSet('app','uninstaller','installer')][string]$Role)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$files=switch($Role){'app'{@('AiPulse.exe','AiPulse.dll')};'uninstaller'{@('uninstaller.exe')};'installer'{@('installer.exe')}}
$incoming=Join-Path $root "build/signpath/$Role-signed"
$original=Join-Path $root "build/signpath/$Role-input"
$report=& (Join-Path $PSScriptRoot 'verify-signatures.ps1') -Directory $incoming -ExpectedFiles $files
foreach($name in $files){
    & python (Join-Path $PSScriptRoot 'signing_payload.py') (Join-Path $original $name) (Join-Path $incoming $name)
    if($LASTEXITCODE -ne 0){throw 'SignPath response changed executable payload'}
}
$destination=switch($Role){'app'{Join-Path $root 'build/win-x64'};'uninstaller'{Join-Path $root 'build/signpath/approved-uninstaller'};'installer'{Join-Path $root 'build/releases'}}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach($name in $files){
    $finalName=if($Role -eq 'installer'){"AI-Pulse-Setup-$env:AIPULSE_VERSION-x64.exe"}else{$name}
    Copy-Item -LiteralPath (Join-Path $incoming $name) -Destination (Join-Path $destination $finalName) -Force
}
[IO.File]::WriteAllText((Join-Path $root "build/signpath/$Role-verification.json"),($report -join "`n"))
Write-Output "Verified and accepted signed $Role files."
