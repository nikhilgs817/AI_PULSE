param([string]$Compiler='C:\Program Files (x86)\Inno Setup 6\ISCC.exe',[switch]$Complete)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cache=Join-Path $root 'build/signpath/uninstaller-cache'
$record=Join-Path $root 'build/signpath/uninstaller.json'
if(!$Complete -and (Test-Path -LiteralPath $record)){throw 'Use a fresh signing workspace'}
if($Complete -and !(Test-Path -LiteralPath (Join-Path $root 'build/signpath/approved-uninstaller/uninstaller.exe'))){throw 'Verified signed uninstaller is required'}
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$capture=Join-Path $PSScriptRoot 'capture-uninstaller.ps1'
$pwsh=(Get-Process -Id $PID).Path
# Single-quoted literal placeholders are expanded by Inno, not by PowerShell.
$stage=if($Complete){'Installer'}else{'Uninstaller'}
$signTool='/Saipulse-capture='+('$q{0}$q -NoProfile -NonInteractive -File $q{1}$q -Stage {2} -File $f' -f $pwsh,$capture,$stage)
$info=[Diagnostics.ProcessStartInfo]::new($Compiler)
$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
foreach($arg in @('/DSignPathCapture',('/DSignPathUninstallerDir='+$cache),$signTool,(Join-Path $root 'installer/windows.iss'))){$info.ArgumentList.Add($arg)}
$process=[Diagnostics.Process]::Start($info)
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
$process.WaitForExit()
$log=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
[IO.File]::WriteAllText((Join-Path $root 'build/signpath/prepare-uninstaller.log'),$log)
if($Complete){
    if($process.ExitCode -eq 0 -or !(Test-Path -LiteralPath (Join-Path $root 'build/signpath/installer-input/installer.exe'))){throw 'Expected installer capture failed; inspect prepare-uninstaller.log'}
    Write-Output 'Prepared installer containing the signed app and signed uninstaller for SignPath.'
    exit 0
}
if($process.ExitCode -eq 0 -or !(Test-Path -LiteralPath $record)){throw 'Expected capture stop did not create the uninstaller; inspect prepare-uninstaller.log'}
$saved=Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
if($saved.Sha256 -ne (Get-FileHash -LiteralPath (Join-Path $root 'build/signpath/uninstaller-input/uninstaller.exe') -Algorithm SHA256).Hash.ToLower()){throw 'Captured uninstaller mismatch'}
Write-Output 'Prepared generated uninstaller for SignPath; installer compilation intentionally stopped.'
