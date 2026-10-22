param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string[]]$ExpectedFiles,
    [string]$Thumbprint=$env:SIGNPATH_CERTIFICATE_THUMBPRINT,
    [string]$Version=$env:AIPULSE_VERSION
)
$ErrorActionPreference='Stop'
if($Thumbprint -notmatch '^[a-fA-F0-9]{40}$'){throw 'Configure the approved SignPath certificate SHA-1 thumbprint (identifier, not signing algorithm)'}
$files=@(Get-ChildItem -LiteralPath $Directory -Force)
if($files.Count -ne $ExpectedFiles.Count -or @($files | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $ExpectedFiles }).Count){throw 'Unexpected signing response files'}
$report=@()
foreach($name in $ExpectedFiles){
    $path=Join-Path $Directory $name
    $signature=Get-AuthenticodeSignature -LiteralPath $path
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint){throw "Untrusted or unexpected signer: $name"}
    if(!$signature.TimeStamperCertificate){throw "Missing trusted timestamp: $name"}
    $eku=@($signature.SignerCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
    if('1.3.6.1.5.5.7.3.3' -notin $eku){throw "Certificate is not authorized for code signing: $name"}
    $metadata=(Get-Item -LiteralPath $path).VersionInfo
    if($metadata.ProductName.Trim() -ne 'AIPULSE' -or !$Version -or $metadata.ProductVersion.Trim() -ne $Version){throw "Unexpected product identity/version: $name"}
    $report+=@{Name=$name;Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLower();Signer=$signature.SignerCertificate.Subject;Thumbprint=$Thumbprint;TimestampSigner=$signature.TimeStamperCertificate.Subject}
}
$report | ConvertTo-Json -Depth 4 | Write-Output
