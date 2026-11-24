# Sets up live Claude subscription usage in AI Pulse on Windows, in one step:
#   1. checks for Claude Code, Git and the .NET 10 SDK (installs the missing tools with winget),
#   2. downloads or updates the AI Pulse source (in its own folder; your own clones are not touched),
#   3. builds AI Pulse and installs it over your current AI Pulse (or into the default folder),
#   4. connects AI Pulse to Claude Code's status line (settings.json is backed up first),
#   5. starts AI Pulse.
# Run it again at any time to update. Undo the Claude Code change with:  AiPulse.exe --disconnect-claude-code
#
# One-line use (PowerShell):
#   irm https://raw.githubusercontent.com/nikhilgs817/AI_PULSE/fix/claude-usage-429/scripts/setup-claude-usage.ps1 | iex

& {
    $ErrorActionPreference = 'Stop'
    $Branch = if ($env:AIPULSE_BRANCH) { $env:AIPULSE_BRANCH } else { 'fix/claude-usage-429' }
    $RepoUrl = 'https://github.com/nikhilgs817/AI_PULSE'
    $Source = Join-Path $env:LOCALAPPDATA 'AI Pulse\source'
    $DefaultInstall = Join-Path $env:LOCALAPPDATA 'Programs\AI Pulse'

    function Step($text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Cyan }
    function Refresh-Path {
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    }
    function Ensure-Tool($command, $wingetId, $name) {
        if (Get-Command $command -ErrorAction SilentlyContinue) { return }
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw "$name is missing and winget is not available. Install $name, then run this again." }
        Write-Host "Installing $name with winget..."
        winget install --id $wingetId -e --accept-source-agreements --accept-package-agreements
        Refresh-Path
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "$name was installed but is not on PATH yet. Open a new PowerShell window and run this again." }
    }

    Step 'Checking Claude Code'
    $claudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
    if (-not (Test-Path -LiteralPath $claudeDir)) { throw "Claude Code settings folder not found ($claudeDir). Start Claude Code once, then run this again." }
    Write-Host "Claude Code settings: $claudeDir"

    Step 'Checking Git and the .NET 10 SDK'
    Ensure-Tool 'git' 'Git.Git' 'Git'
    Ensure-Tool 'dotnet' 'Microsoft.DotNet.SDK.10' '.NET 10 SDK'
    if (-not ((& dotnet --list-sdks) -match '^10\.')) {
        Write-Host 'Installing the .NET 10 SDK with winget...'
        winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
        Refresh-Path
        if (-not ((& dotnet --list-sdks) -match '^10\.')) { throw 'The .NET 10 SDK is still missing. Open a new PowerShell window and run this again.' }
    }

    Step "Getting AI Pulse ($Branch)"
    if (Test-Path -LiteralPath (Join-Path $Source '.git')) {
        git -C $Source fetch --depth 1 origin $Branch
        if ($LASTEXITCODE -ne 0) { throw 'git fetch failed.' }
        git -C $Source checkout -q -B $Branch FETCH_HEAD
        git -C $Source reset -q --hard FETCH_HEAD
    } else {
        New-Item -ItemType Directory -Path (Split-Path $Source) -Force | Out-Null
        git clone --depth 1 --branch $Branch $RepoUrl $Source
        if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }
    }
    Write-Host ('Source: ' + $Source + ' @ ' + (git -C $Source rev-parse --short HEAD))

    Step 'Building AI Pulse (first build downloads packages and takes a few minutes)'
    $buildOutput = Join-Path $Source 'build\win-x64'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Source 'scripts\build.ps1') -OutputDirectory $buildOutput
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. See the messages above.' }

    Step 'Installing'
    # Install where the running AI Pulse lives, so shortcuts and startup entries keep working.
    $running = @(Get-Process -Name AiPulse -ErrorAction SilentlyContinue)
    $install = $DefaultInstall
    $withPath = $running | Where-Object { $_.Path } | Select-Object -First 1
    if ($withPath) { $install = Split-Path -Parent $withPath.Path }
    elseif (Test-Path -LiteralPath (Join-Path ${env:ProgramFiles} 'AI Pulse\AiPulse.exe')) { $install = Join-Path ${env:ProgramFiles} 'AI Pulse' }
    if ($running.Count -gt 0) {
        Write-Host 'Closing AI Pulse so it can be updated...'
        foreach ($p in $running) { if ($p.MainWindowHandle -ne 0) { [void]$p.CloseMainWindow() } }
        Start-Sleep -Seconds 4
        Get-Process -Name AiPulse -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    try {
        Copy-Item -Path (Join-Path $buildOutput '*') -Destination $install -Recurse -Force
    } catch {
        throw "Could not write to $install ($($_.Exception.Message)). If AI Pulse runs as a Host Agent or hub service, stop the service and run this again."
    }
    $exe = Join-Path $install 'AiPulse.exe'
    Write-Host "Installed: $exe"

    Step 'Connecting AI Pulse to Claude Code'
    $result = & $exe --connect-claude-code 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Host $result.Trim()
    if ($code -ne 0) { throw 'Connecting to Claude Code failed. See the message above.' }

    Step 'Checking the status line the way Claude Code runs it (Git Bash)'
    $bash = @($env:CLAUDE_CODE_GIT_BASH_PATH, (Join-Path ${env:ProgramFiles} 'Git\bin\bash.exe'),
              (Join-Path $env:LOCALAPPDATA 'Programs\Git\bin\bash.exe')) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    $settings = Get-Content -Raw -LiteralPath (Join-Path $claudeDir 'settings.json') | ConvertFrom-Json
    $command = $settings.statusLine.command
    if ($bash -and $command) {
        # Sample input only; it carries no rate_limits, so the saved usage is left unchanged.
        $sample = '{"model":{"display_name":"setup check"},"workspace":{"current_dir":"' + ($HOME -replace '\\', '/') + '"}}'
        # Run the command from a file: Windows PowerShell 5.1 mangles quotes inside native arguments.
        $script = Join-Path $env:TEMP ('aipulse-statusline-check-' + [guid]::NewGuid().ToString('N') + '.sh')
        [IO.File]::WriteAllText($script, $command + "`n", (New-Object Text.UTF8Encoding $false))
        try { $check = $sample | & $bash ($script -replace '\\', '/') 2>&1 | Out-String }
        finally { Remove-Item -LiteralPath $script -ErrorAction SilentlyContinue }
        if ($LASTEXITCODE -eq 0 -and $check.Trim()) { Write-Host ('Status line works: ' + $check.Trim()) }
        else { Write-Warning "The status line did not answer as expected: $check" }
    } else {
        Write-Host 'Git Bash not found; skipping this check. Claude Code will run the status line itself.'
    }

    Step 'Starting AI Pulse'
    Start-Process -FilePath $exe

    Write-Host ''
    Write-Host 'Done.' -ForegroundColor Green
    Write-Host 'Send any message in Claude Code (sessions that are already open work too).'
    Write-Host 'The Claude meters on the Token page and in compact mode fill after that reply.'
    Write-Host 'If Windows blocks the unsigned AiPulse.exe, allow it or use a build approved by your administrator.'
}
