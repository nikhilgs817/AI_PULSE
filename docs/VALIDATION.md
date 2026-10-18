# Validation report — 0.2.0

## Local Windows checks

- The updated self-contained executable runs on the Minisforum Ryzen AI Max+ 395 / Radeon 8060S machine.
- 17 data checks cover accounting, privacy, 90/365-day retention, year-old backup restoration, search before the 100-session limit, replay on increased retention, reduced-retention cleanup and invalid sensor values.
- UI checks cover both compact orientations, full-size expanded branding, collapsible navigation, persistent search focus/text, no-match states and the one-year selector.
- A loopback Ollama protocol fixture exposes 12 synthetic models: every row is rendered, the dropdown height is bounded and its scroll position survives refresh. These fixture models do not run inference.
- Native Windows pointer input moved the expanded window from the body by 50 pixels horizontally and 30 vertically. Search typing preserved focus through live refreshes.
- Icon generation checks corner alpha and embeds seven Windows icon resolutions.

Current platform CI results, installed-app Ollama tests and sensor permission checks are recorded below after they complete. Physical Ubuntu GPU/temperature tests remain unperformed. Missing sensors stay unavailable; GPU VR SoC is not a GPU-core-temperature substitute.

See the [previous preview's validation](VALIDATION-0.1.0.md) for the initial Windows/Ubuntu package, real Ollama and sensor-helper baseline. Historical results do not replace current-release checks.
