param([string]$Runtime='win-x64',[string]$Dotnet='dotnet',[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_CLI_HOME=Join-Path $root 'tools/dotnet-home'
$output=if($OutputDirectory){[System.IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root "build/$Runtime"}
& $Dotnet publish (Join-Path $root 'src/AiPulse/AiPulse.csproj') -c Release -r $Runtime --self-contained true -o $output
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output
$docsOutput=Join-Path $output 'docs'
New-Item -ItemType Directory -Path $docsOutput -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'docs') | Copy-Item -Destination $docsOutput -Recurse -Force
Write-Host "Self-contained app published to $output"
