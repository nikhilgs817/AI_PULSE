# Validation report — 0.2.2

## Released build

Source `d5ea477392ee6298b2ad188494d10fc7525388d7` passed [Windows and Ubuntu CI run 39369763954](https://github.com/nikhilgs817/AI_PULSE/actions/runs/39369763954). Both platforms passed **36 data checks and 56 UI checks**, along with Windows installer/uninstaller, Ubuntu DEB installation/removal, AppImage extraction/launch, bundled-runtime and Linux CPU/RAM persistence checks.

Results: [Windows data](validation/windows-data-v022.txt), [Windows UI](validation/windows-ui-v022.txt), [Ubuntu data](validation/linux-data-v022.txt), [Ubuntu UI](validation/linux-ui-v022.txt). The Windows data checks were repeated successfully using the final installed executable.

## New checks and retained coverage

- Rich Overview sections, shared trend ranges and detail navigation; explicit missing-data and stale-collector states.
- Wi-Fi adapter identity, filter-driver alias exclusion, byte-counter warm-up/reset and Mbps conversion. UI fixtures distinguish a 600 Mbps link from measured 8.39 Mbps download / 16.78 Mbps upload, and check both history series.
- Chart date/time labels across midnight and multi-day windows, local timezone conversion, shared time bounds, aggregation intervals and missing-period gaps.
- Source filtering before request-list limits, so other tools cannot crowd observed Ollama requests out of the Overview.
- Existing compact layouts, loaded-model dropdown, daily token accounting, hardware capacity/use, process ownership, search, window controls, retention, privacy and backup regressions.

An intermediate UI run exposed a null text value in the test harness. The harness was corrected before the final successful run. The released binaries come only from the final successful build above.

## Installed Windows validation

The standard installer upgraded the existing app to `0.2.2+d5ea477392ee6298b2ad188494d10fc7525388d7`. All **1,868 usage records** in the verified pre-upgrade snapshot remained present. SQLite integrity was `ok`, and the configured 90-day retention was preserved.

Native inspection confirmed the expanded Overview, dated chart axes, capacity/use cards, paired utilization charts, temperature history, app resources and coverage status. Real Wi-Fi download/upload counters were live, with a separately reported **1,201 Mbps link rate**. That link rate is not an internet speed test. Windows filter aliases were excluded from the new network samples.

After the owner approved the deep sensor helper, fresh CPU temperature readings resumed (approximately 42–49 °C during the final screenshots). GPU-core temperature was unavailable and remained a dash; another sensor such as GPU VR SoC is not silently relabelled as core temperature. The app was left running on Overview. No Windows security policy was changed.

## Real Ollama checks

All seven [installed-app Ollama checks](validation/ollama-windows-v022.json) passed: non-streaming, streaming, concurrent generation/embedding, two resident models, preserved HTTP 404, exact accounting and provider-matched decode rate. Three Qwen requests recorded exactly **75 input and 45 output tokens**. Existing `qwen3:8b` and `nomic-embed-text:latest` models were used; no weights were downloaded. Test model residency expires after five minutes.

These are integration and accounting checks, not a model performance benchmark. Direct requests that bypass the monitoring gateway are outside its request-token coverage.

## Screenshots and limits

The guide includes fresh installed-Windows Overview and Wi-Fi screenshots, an Ubuntu CI desktop screenshot, and an explicitly labelled synthetic Wi-Fi fixture. Older fixture images remain labelled in the guide. Synthetic series demonstrate layout, conversion and independent link/traffic values, not measured hardware performance.

Physical Ubuntu Wi-Fi, GPU and temperature validation remains unperformed; Ubuntu package, desktop and fixture checks passed in CI. Protected processes can make app totals incomplete, and working sets can share pages. Cloud-host CPU/RAM and per-process GPU allocations are not exposed. Shared/unified GPU allocations overlap system RAM.

The [central hub / Host Agent architecture](FLEET-ARCHITECTURE.md) is a documented next stage. This release does **not** install boot-time host services, pair remote hosts, synchronize an offline outbox or provide the fleet host dropdown.

## Published artifacts

The [0.2.2 preview release](https://github.com/nikhilgs817/AI_PULSE/releases/tag/v0.2.2) contains the Windows installer/portable ZIP, Ubuntu DEB/AppImage and two checksum manifests. All six GitHub asset digests independently matched the locally verified CI artifacts. [Publication run 39472106536](https://github.com/nikhilgs817/AI_PULSE/actions/runs/39472106536) also verifies the successful build, matching source and uploaded digests before publishing.

The portable archive bundles the runtime and SQLite and was checked for absence of personal databases, settings and logs. Release files remain unsigned preview builds. Repository screenshots and this validation report were updated after the binary build; the source identity above identifies the released executable.

See [0.2.1 validation](VALIDATION-0.2.1.md) for the previous baseline and [monitoring research](MONITORING-RESEARCH.md) for the official documentation informing the Overview and fleet design.
