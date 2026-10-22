$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$roles=@{}
foreach($role in @('app','uninstaller','installer')){
    $roles[$role]=Get-Content -LiteralPath (Join-Path $root "build/signpath/$role-verification.json") -Raw | ConvertFrom-Json
}
$packageHashes=@{}
foreach($name in @("AI-Pulse-Setup-$env:AIPULSE_VERSION-x64.exe","AI-Pulse-$env:AIPULSE_VERSION-win-x64-portable.zip")){
    $packageHashes[$name]=(Get-FileHash -LiteralPath (Join-Path $root "build/releases/$name") -Algorithm SHA256).Hash.ToLower()
}
@{
    Schema=1;Provider='SignPath';Repository=$env:GITHUB_REPOSITORY;Source=$env:GITHUB_SHA;
    BuildRunId=$env:GITHUB_RUN_ID;Version=$env:AIPULSE_VERSION;CertificateThumbprint=$env:SIGNPATH_CERTIFICATE_THUMBPRINT;
    Files=$roles;Packages=$packageHashes
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'build/releases/SIGNING-VERIFICATION.json') -Encoding utf8
