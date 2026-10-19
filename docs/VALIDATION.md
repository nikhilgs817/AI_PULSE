# Validation report — 0.2.0

## Local Windows checks

- The updated self-contained executable runs on the Minisforum Ryzen AI Max+ 395 / Radeon 8060S machine.
- 21 data checks cover accounting, privacy, 90/365-day retention, year-old backup restoration, search before the 100-session limit, replay on increased retention, reduced-retention cleanup and invalid sensor values. Token checks cover more than 20,000 records, source separation, cache/reasoning subsets, incomplete records and time ranges.
- 41 UI checks cover both compact orientations, full-size expanded branding, collapsible navigation, persistent search focus/text, no-match states, the one-year selector, larger overview headings, separate Hardware/Sensors contents and exact Token page totals.
- Expanded Maximize/Restore and returning from a maximized dashboard to compact mode pass the real-window checks. Ubuntu CI runs these with Openbox inside Xvfb, in addition to Linux CPU/RAM and package installation tests.
- A loopback Ollama protocol fixture exposes 12 synthetic models: every row is rendered, the dropdown height is bounded and its scroll position survives refresh. These fixture models do not run inference.
- Native Windows pointer input moved the expanded window from the body by 50 pixels horizontally and 30 vertically. Search typing preserved focus through live refreshes.
- Native double-clicks in blank header space maximize and restore the dashboard. Header replacement is queued until pointer processing finishes.
- A captured NullReferenceException traced the earlier crash to clicking an already-selected navigation tab: the handler cleared the scroll container without rebuilding the unchanged page. The handler now preserves the current page and explicitly rebuilds on a page change. Repeated-click regression checks pass. UI failures now preserve a nonzero process exit code, and CI independently rejects failed result lines.
- The Token screenshot uses three clearly named synthetic usage records. Aggregation tests verify exact 1,500 on-prem and 1,950 tool tokens without adding cache/reasoning subsets twice. This fixture is not an inference benchmark.
- Icon generation checks corner alpha and embeds seven Windows icon resolutions.

The Windows 0.2.0 installer upgraded the existing installation successfully. A verified pre-upgrade snapshot preserved 1,570 usage records; the existing data directory and 90-day preference were retained. The installed executable reports version 0.2.0.

All seven [real Ollama checks](validation/ollama-windows-v020.json) passed from the installed app: non-streaming, streaming, concurrent generation/embedding, two resident models, preserved HTTP 404, exact token accounting and provider-matched decode rate. Three Qwen requests recorded exactly **75 input and 51 output tokens**. No model weights were downloaded.

After the owner approved the isolated helper on October 19, CPU temperature was recorded continuously at approximately 46–48 °C. PawnIO was already installed; no security policy was changed. Deep-sensor permission remains session-scoped.

The final release source, `d0d5478779737868c40ae121cc58dfeae03ea95e`, passed both Windows and Ubuntu CI in [run 39224124168](https://github.com/nikhilgs817/AI_PULSE/actions/runs/39224124168). Both platforms passed 21 data and 41 UI assertions. CI also verifies Windows installer/uninstaller behavior, Ubuntu DEB installation/removal, bundled-runtime launches, Linux CPU/RAM persistence and AppImage extraction/launch. An intentionally failing local UI check returned exit code 1, confirming that assertion failures fail the process.

The final Windows build was installed and inspected with real records on the Token page. Native repeated selections of Token and Hardware remained stable. Every one of the 1,570 pre-upgrade usage records was still present, SQLite integrity was `ok`, and the 90-day preference was retained. Installed application code matches the tested local build.

See the machine-readable [Windows UI results](validation/windows-ui-v020.txt), [Windows data results](validation/windows-data-v020.txt), [Ubuntu UI results](validation/linux-ui-v020.txt) and [Ubuntu data results](validation/linux-data-v020.txt). [Ubuntu Token screenshot](images/token-ubuntu.png) uses the same synthetic records as Windows.

Physical Ubuntu GPU/temperature tests remain unperformed. Missing sensors stay unavailable; GPU VR SoC is not a GPU-core-temperature substitute.

See the [previous preview's validation](VALIDATION-0.1.0.md) for the initial Windows/Ubuntu package, real Ollama and sensor-helper baseline. Historical results do not replace current-release checks.
