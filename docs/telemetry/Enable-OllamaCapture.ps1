param(
  [string]$App=(Join-Path $PSScriptRoot '../../AiPulse.exe'),
  [string]$DataDir=(Join-Path $env:LOCALAPPDATA 'AI Pulse'),
  [string]$OllamaExecutable=(Join-Path $env:LOCALAPPDATA 'Programs/Ollama/ollama.exe'),
  [switch]$RestartOllama,
  [switch]$Disable
)
$ErrorActionPreference='Stop'
$App=(Resolve-Path -LiteralPath $App).Path
$DataDir=[IO.Path]::GetFullPath($DataDir)
$configFile=Join-Path $DataDir 'usage-relay.json'
$rollbackFile=Join-Path $DataDir 'relay/setup-rollback.json'
$runPath='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupLink=Join-Path ([Environment]::GetFolderPath('Startup')) 'Ollama.lnk'
$savedStartupLink=Join-Path $DataDir 'relay/Ollama-startup.lnk'
if(!$RestartOllama){throw 'This change restarts Ollama and unloads its running models. Re-run with -RestartOllama when your jobs are idle. Close AIPULSE first. No model files will be removed.'}
if(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'AiPulse.exe' -and $_.ExecutablePath -eq $App -and $_.CommandLine -notmatch '--usage-relay' }){throw 'Quit AIPULSE with Ctrl+Q before changing its capture configuration.'}
$OllamaExecutable=(Resolve-Path -LiteralPath $OllamaExecutable).Path
$ollamaFolder=Split-Path -Parent $OllamaExecutable
$config=$null
if($Disable){
  if(!(Test-Path -LiteralPath $rollbackFile)){throw 'No setup rollback file exists; no settings changed.'}
  $rollback=Get-Content -LiteralPath $rollbackFile -Raw | ConvertFrom-Json
}else{
  if(Test-Path -LiteralPath $configFile){throw 'Capture is already configured. Disable it before changing ports.'}
  foreach($port in 11436){if(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue){throw "Private backend port $port is occupied; no changes made."}}
  New-Item -ItemType Directory -Path (Join-Path $DataDir 'relay') -Force | Out-Null
  $run=Get-ItemProperty -LiteralPath $runPath
  $rollback=[pscustomobject]@{OllamaRun=$run.Ollama;OllamaExecutable=$OllamaExecutable;StartupLink=(Test-Path -LiteralPath $startupLink);CreatedUtc=[DateTime]::UtcNow.ToString('O')}
  $rollback | ConvertTo-Json | Set-Content -LiteralPath $rollbackFile -Encoding UTF8
  $keyBytes=New-Object byte[] 32
  $rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($keyBytes);$rng.Dispose()
  $config=[ordered]@{ListenPort=11434;BackendPort=11436;LaunchBackend=$true;OllamaExecutable=$OllamaExecutable;ReadKey=([BitConverter]::ToString($keyBytes)).Replace('-','')}
}
# Stop only this installed Ollama distribution and this app's relay, after explicit consent.
$relay=Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'AiPulse.exe' -and $_.ExecutablePath -eq $App -and $_.CommandLine -match '--usage-relay' }
foreach($p in $relay){Stop-Process -Id $p.ProcessId -ErrorAction SilentlyContinue}
$ollamaProcesses=Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('ollama.exe','ollama app.exe','ollama_llama_server.exe') -and $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($ollamaFolder+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) }
foreach($p in $ollamaProcesses){Stop-Process -Id $p.ProcessId -ErrorAction SilentlyContinue}
Start-Sleep -Seconds 2
if($Disable){
  Remove-ItemProperty -LiteralPath $runPath -Name 'AIPULSE Usage Relay' -ErrorAction SilentlyContinue
  if($rollback.OllamaRun){Set-ItemProperty -LiteralPath $runPath -Name 'Ollama' -Value $rollback.OllamaRun}
  if($rollback.StartupLink -and (Test-Path -LiteralPath $savedStartupLink)){
    if(Test-Path -LiteralPath $startupLink){throw 'An Ollama startup shortcut already exists; the saved original is retained for manual review.'}
    Move-Item -LiteralPath $savedStartupLink -Destination $startupLink
  }
  if(Test-Path -LiteralPath $configFile){Move-Item -LiteralPath $configFile -Destination ($configFile+'.disabled-'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))}
  Start-Process -FilePath (Join-Path $ollamaFolder 'ollama app.exe') -WindowStyle Hidden
  Write-Host 'Original Ollama startup restored. Launch AIPULSE again. Retained usage metadata is preserved.'
}else{
  try{
    if($rollback.StartupLink){
      # Literal, verified single-file move; preserve the user's original startup shortcut.
      if([IO.Path]::GetFullPath($savedStartupLink) -ne (Join-Path $DataDir 'relay/Ollama-startup.lnk')){throw 'Unexpected startup backup destination'}
      Move-Item -LiteralPath $startupLink -Destination $savedStartupLink
    }
    # BOM-free JSON is shared with the cross-platform .NET reader.
    [IO.File]::WriteAllText($configFile,($config | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Remove-ItemProperty -LiteralPath $runPath -Name 'Ollama' -ErrorAction SilentlyContinue
    $arguments='--usage-relay --data-dir "'+$DataDir+'"'
    Set-ItemProperty -LiteralPath $runPath -Name 'AIPULSE Usage Relay' -Value ('"'+$App+'" '+$arguments)
    Start-Process -FilePath $App -ArgumentList $arguments -WindowStyle Hidden
    $ready=$false
    for($attempt=0;$attempt -lt 20;$attempt++){
      Start-Sleep -Milliseconds 500
      try{if((Invoke-RestMethod 'http://127.0.0.1:11434/health' -TimeoutSec 2).service -eq 'AI Pulse observation gateway' -and (Invoke-RestMethod 'http://127.0.0.1:11434/api/version' -TimeoutSec 2).version){$ready=$true;break}}catch{}
    }
    if(!$ready){throw 'Capture did not become ready. Run this script with -Disable -RestartOllama to restore the saved configuration.'}
    Write-Host 'Persistent capture is ready on the normal Ollama port 11434. Launch AIPULSE. Clients must keep using 11434; 11436 is private and bypasses capture. Models reload on their next request.'
  }catch{
    $failure=$_
    try{& $PSCommandPath -App $App -DataDir $DataDir -OllamaExecutable $OllamaExecutable -Disable -RestartOllama}catch{Write-Warning 'Automatic rollback did not finish; the saved original startup configuration remains under the local data folder relay/setup-rollback.json.'}
    throw $failure
  }
}
