# Architecture and development

The Windows sensor helper uses a user SID access-control list plus mutual process-ID checks. It intentionally does not use `PipeOptions.CurrentUserOnly`, which also requires matching elevation levels ([Microsoft documentation](https://learn.microsoft.com/dotnet/api/system.io.pipes.pipeoptions)). The elevated helper cannot receive commands and connects with anonymous impersonation level.

## Components

```text
Hardware (Windows or Linux) ───────────┐
Optional elevated sensor helper ──────┤
Ollama residency discovery ───────────┤
Local request observation gateway ────┼─> normalized readings / usage
Codex & Claude Code metadata readers ┘              │
                                 ┌─────────────────┴─────────────┐
                            Avalonia UI                      SQLite
                        compact + full views         history / alerts / backups
```

The UI uses Avalonia and C#/.NET 10. SQLite is in-process via Microsoft.Data.Sqlite. Windows hardware uses OS counters plus LibreHardwareMonitor; Linux uses procfs/hwmon/DRM. The gateway is Kestrel bound to loopback. A single storage lock serializes writes/backup snapshots, and a process lock protects each data root from duplicate app instances.

`AgentUsage` reads supported JSONL files incrementally with persisted byte offsets and accounting state. It never stores raw lines. `Gateway` forwards response bytes without reserializing them and retains only allowlisted final usage. Neither integration claims visibility into unobserved requests.

The Windows deep-sensor helper is a separate elevated invocation of the same binary. It sends hardware data through a random named pipe restricted to the same user. It accepts no command payload. It terminates when the parent process exits. The main UI and HTTP receiver do not elevate.

## Build

Install the .NET 10 SDK on the development machine. Dependency versions are declared in the project and lockfile. NuGet packages are stored under `packages/nuget` in the project.

```powershell
./scripts/build.ps1 -Runtime win-x64
./scripts/build.ps1 -Runtime linux-x64
```

The PowerShell wrapper copies documentation/notices into the published folder. On Linux, run `dotnet publish` and copy those documents as shown in the CI workflow. Self-contained publishes include both .NET and ASP.NET runtime dependencies and native SQLite libraries.

Use Inno Setup to compile `installer/windows.iss`. Run `python3 scripts/package_linux.py` to generate a DEB; it writes executable permissions explicitly even when run on Windows. `scripts/package-appimage.sh` uses AppImage's official build tooling on Linux.

## Tests and diagnostics

```powershell
./build/win-x64/AiPulse.exe --self-test --data-dir ./build/test-data
./build/win-x64/AiPulse.exe --diagnose --data-dir ./build/diagnostic
./build/win-x64/AiPulse.exe --collect --seconds 30 --data-dir ./build/collector-test
```

`--self-test` checks deduplication, provider accounting, retention and a real SQLite backup/restore. `--diagnose` writes hardware readings to `diagnostics.json` and exits. `--collect` runs collection for a bounded duration without opening a window. See the dedicated Ollama test guide for real inference traffic.

For an intermittent desktop crash, add `--diagnostic-stack` to retain the exception's call stack in the local `startup-error.txt`. This is opt-in; review the file before sharing because stack traces can contain local source paths. It does not enable remote reporting.

For UI captures, `--capture` saves the rendered window after startup. `--expanded` opens the full dashboard and `--page Models` selects a page. `--exit-after N` supports bounded GUI smoke testing; with capture enabled, the countdown follows the capture delay. Screenshots must remain clearly identified as live captures or test fixtures.

## Release checklist

1. Build both self-contained targets and run tests.
2. Validate clean installation without a developer runtime, and upgrade/uninstall without deleting personal history.
3. Exercise desktop UI on Windows and a Linux desktop/X11 test environment; test real Linux hardware separately.
4. Verify final packages include app libraries, documentation, license notices and no user data/secrets.
5. Publish checksums and document exactly which platform/hardware combinations were tested.
6. Use trusted publisher signing for a production Windows release. Preview builds must be labeled unsigned.

The GitHub workflow builds packages and runs Linux tests; virtual graphics do not validate native GPU telemetry. Runtime/provider adapters are expected to need maintenance when driver or source-app formats change.

## UI regression checks (0.2.0)

`--ui-test --data-dir <isolated test folder>` runs real Avalonia controls and saves screenshots/results. Never point it at your personal data directory: it changes test settings. `tests/ui_fixture.py` provides 12 explicitly synthetic Ollama models on loopback port 11436. Configure the isolated app's `OllamaUrl` accordingly and add `--expect-models 12` to verify discovery, every model row, bounded scrolling and scroll preservation. This is a UI/protocol simulation, not a model-performance benchmark.

Both platform CI jobs run the checks. They also cover both compact orientations, restored full-size header, Maximize/Restore, returning from maximized to compact, preserving the active header during input, sidebar collapse/expand, search focus across refresh, independent tab searches, empty results and the one-year history selector. Native pointer dragging and blank-header double-click are separately checked on Windows. Real Ollama inference uses the separate smoke test. Start with a fresh test settings file; the UI checks intentionally finish with 365-day retention.
