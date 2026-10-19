# Validation report — 0.2.0

## Local Windows checks

- The updated self-contained executable runs on the Minisforum Ryzen AI Max+ 395 / Radeon 8060S machine.
- 17 data checks cover accounting, privacy, 90/365-day retention, year-old backup restoration, search before the 100-session limit, replay on increased retention, reduced-retention cleanup and invalid sensor values.
- UI checks cover both compact orientations, full-size expanded branding, collapsible navigation, persistent search focus/text, no-match states and the one-year selector.
- Expanded Maximize/Restore and returning from a maximized dashboard to compact mode pass the real-window checks. Ubuntu CI runs these with Openbox inside Xvfb, in addition to Linux CPU/RAM and package installation tests.
- A loopback Ollama protocol fixture exposes 12 synthetic models: every row is rendered, the dropdown height is bounded and its scroll position survives refresh. These fixture models do not run inference.
- Native Windows pointer input moved the expanded window from the body by 50 pixels horizontally and 30 vertically. Search typing preserved focus through live refreshes.
- Icon generation checks corner alpha and embeds seven Windows icon resolutions.

The Windows 0.2.0 installer upgraded the existing installation successfully. A verified pre-upgrade snapshot preserved 1,570 usage records; the existing data directory and 90-day preference were retained. The installed executable reports version 0.2.0.

All seven [real Ollama checks](validation/ollama-windows-v020.json) passed from the installed app: non-streaming, streaming, concurrent generation/embedding, two resident models, preserved HTTP 404, exact token accounting and provider-matched decode rate. Three Qwen requests recorded exactly **75 input and 51 output tokens**. No model weights were downloaded.

After the owner approved the isolated helper on October 19, CPU temperature was recorded continuously at approximately 46–48 °C. PawnIO was already installed; no security policy was changed. Deep-sensor permission remains session-scoped.

The initial 0.2.0 build passed both Windows and Ubuntu CI in [run 39173518066](https://github.com/nikhilgs817/AI_PULSE/actions/runs/39173518066). A final build includes the requested larger bold compact labels. Physical Ubuntu GPU/temperature tests remain unperformed. Missing sensors stay unavailable; GPU VR SoC is not a GPU-core-temperature substitute.

See the [previous preview's validation](VALIDATION-0.1.0.md) for the initial Windows/Ubuntu package, real Ollama and sensor-helper baseline. Historical results do not replace current-release checks.
