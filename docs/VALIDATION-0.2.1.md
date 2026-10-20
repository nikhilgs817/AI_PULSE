# Validation report — 0.2.1

## Final build

Source `92a5965a90db7c30eb7cdaddf9fe84afba03f815` passed [Windows and Ubuntu CI run 39315549733](https://github.com/nikhilgs817/AI_PULSE/actions/runs/39315549733). Both platforms passed **29 data checks and 49 UI checks**. CI also passed Windows installer/uninstaller checks, Ubuntu DEB installation/removal, AppImage extraction/launch, bundled-runtime tests and real Linux CPU/RAM persistence.

Results: [Windows data](validation/windows-data-v021.txt), [Windows UI](validation/windows-ui-v021.txt), [Ubuntu data](validation/linux-data-v021.txt), [Ubuntu UI](validation/linux-ui-v021.txt). The Windows data report was repeated successfully using the final installed executable.

## What the checks cover

- Seven compact readings: tight 28-pixel vertical rows, active sessions, local-calendar-day tokens, both orientations and the bounded dropdown containing all 12 synthetic Ollama models.
- Active-session counting beyond the 100-row list, calendar-day boundaries, exclusion of future records and cache/reasoning subsets counted once.
- Hardware capacity alongside live utilization, disk used/available columns and correct network rate/link-speed units. Detailed sensor access remains on Sensors.
- App/process ownership, nearest named ancestor, child processes beyond the top-25 display, duplicate prevention, process-ID reuse, initial CPU warm-up and stale resource values.
- Search across refresh and tab changes, including delayed text events assigned to the originating tab; existing maximize/restore, navigation, retention, privacy, accounting and backup regressions.

Earlier intermediate runs exposed a delayed-search event bug and a Windows fixture-startup race. The tab binding was fixed, and the harness now waits for the endpoint and discovered models. Only the final successful run supplies the released binaries.

## Installed Windows validation

The standard 0.2.1 installer upgraded the existing app. All **1,765 usage records** in the verified pre-upgrade snapshot remained present, SQLite integrity was `ok`, and the 90-day setting was retained. The installed executable reports version `0.2.1+92a5965a90db7c30eb7cdaddf9fe84afba03f815`.

Native inspection confirmed RAM used/total/available, GPU local allocation/capacity, shared allocation/limit, CPU/GPU load, storage usage and per-adapter network traffic. Agents displayed live Claude, Codex and Ollama CPU, summed working-set memory and process counts. These values also persisted under stable app metric IDs. Resources are app totals shared across sessions; they are not per-conversation estimates.

After the owner approved the helper, fresh CPU temperature readings resumed (approximately 45–59 °C during validation). No Windows security policy was changed. The app was left running in its smaller vertical compact view with CPU temperature, active sessions and today's tokens visible.

## Real Ollama checks

All seven [installed-app Ollama checks](validation/ollama-windows-v021.json) passed: non-streaming, streaming, concurrent generation/embedding, two resident models, preserved HTTP 404, exact accounting and provider-matched decode rate. Three Qwen requests recorded exactly **75 input and 45 output tokens**. Existing `qwen3:8b` and `nomic-embed-text:latest` models were used; no weights were downloaded, and test residency expires after five minutes.

During that test, observed Ollama summed working set rose from about **178 MiB to 6,238 MiB**, its readable process count rose from **3 to 7**, and observed GPU load reached **65%**. These are sampled measurements on this machine, not a throughput benchmark or exact per-model resource allocation.

## Screenshots and limits

The Hardware and vertical compact screenshots are from the installed Windows app. The Agents detail screenshot uses synthetic process values explicitly identified in the guide. Other fixture screenshots demonstrate model rows and token accounting without claiming model performance.

Physical Ubuntu GPU/temperature tests remain unperformed; Linux package and desktop checks run in CI. Protected processes can make app totals incomplete. Memory working sets can share pages. Cloud-host CPU/RAM and per-session/per-process GPU allocations are not exposed. GPU shared/unified memory overlaps system RAM.

The installer and portable archive were verified against CI checksums. The portable archive contains the runtime, SQLite and referenced screenshots, with no personal databases, settings or logs. Release files are unsigned preview builds.

See [0.2.0 validation](VALIDATION-0.2.0.md) for the previous baseline.

The [0.2.1 preview release](https://github.com/nikhilgs817/AI_PULSE/releases/tag/v0.2.1) is published with the Windows installer/portable ZIP, Ubuntu DEB/AppImage and two checksum manifests. All six uploaded GitHub digests match the locally verified CI artifacts. Publication used [run 39366354963](https://github.com/nikhilgs817/AI_PULSE/actions/runs/39366354963), which checks the successful build, matching draft source and every asset digest before publishing.
