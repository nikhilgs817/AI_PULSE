AIPULSE 0.2.0 makes the desktop widget much smaller and adds the requested navigation, search and history controls.

### Changes

- Compact horizontal layout: 560 × 132 logical pixels. Vertical layout: 260 × 300. Switch orientation from the small header.
- Loaded models move into an expandable dropdown; every model is available in a bounded, scrollable list.
- Smaller compact branding and controls; full-size branding returns when expanded.
- Larger bold metric labels: 12-point vertical and 11-point horizontal.
- Expanded Minimize, Maximize/Restore and Close controls on Windows and Ubuntu, with header double-click maximization.
- Drag the window from any non-interactive surface. Buttons, search and scrollbars remain usable.
- Collapsible icon sidebar and persistent search in Hardware, Sensors and Agents.
- Hardware now contains device specifications; Sensors owns live readings, process activity and deep-sensor access.
- New Token page separates on-prem Ollama from tools, with source/model breakdowns, exact input/output/cache/reasoning counts, selectable periods and CSV export.
- Larger 15-point overview-card headings and a fix for crashes when clicking the already-selected tab.
- History retention supports 7–365 days, with 90 days as the shipping default. Imports, exports, cleanup and backup rotation follow the configured period.
- Sensor access shows missing-driver/disconnected/connected states and a direct reconnect action. Optional startup permission requests remain off by default. Invalid GPU-load readings are rejected.
- New white ECG desktop icon on a blue–violet–magenta gradient with transparent rounded corners and seven Windows icon sizes.

### Downloads

Windows: `AI-Pulse-Setup-0.2.0-x64.exe` or `AI-Pulse-0.2.0-win-x64-portable.zip`.

Ubuntu/Linux: `ai-pulse_0.2.0_amd64.deb` or `AI-Pulse-0.2.0-x86_64.AppImage`.

The runtime and SQLite engine are included. Close AIPULSE with Ctrl+Q before upgrading. Personal history remains in the existing data folder. The compact layout replaces the older resizable widget; pin and retention preferences are preserved.

[Illustrated guide](https://github.com/nikhilgs817/AI_PULSE/blob/main/docs/USER-GUIDE.md) · [Installation](https://github.com/nikhilgs817/AI_PULSE/blob/main/docs/INSTALLATION.md) · [Validation results](https://github.com/nikhilgs817/AI_PULSE/blob/main/docs/VALIDATION.md)

### Validation and boundaries

21 data tests cover privacy, accounting, retention and restore. 41 UI checks include 12 synthetic Ollama models, both orientations, dropdown scrolling, sidebar navigation and search focus during refresh. Real installed-app Ollama tests passed streaming, simultaneous generation/embedding and exact provider token counts. Synthetic UI fixtures do not measure model performance.

Windows deep sensors still require the optional signed PawnIO driver and administrator approval for the isolated helper each session. GPU VR SoC is not GPU core temperature. Exact Ollama tokens require traffic through port 11435. Full Kubernetes node/pod collection remains planned. Physical Ubuntu GPU testing remains outstanding.

This preview is unsigned. Device Application Control policies may require an administrator-approved or signed build. Do not disable Windows security protections to install it.
