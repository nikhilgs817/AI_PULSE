# Using AI Pulse

## Compact mode

![Compact dashboard](images/compact.png)

The fixed top strip shows GPU temperature when exposed (otherwise an explicitly labeled GPU VR SoC sensor when available), GPU utilization, local GPU allocation, physical memory used and CPU temperature. A dash means no reliable reading.

Compact mode starts with just five readings and a **▾ Loaded models** control. Click it to reveal every resident model, including host, state, model-reported memory and last observed decode speed. Close it to return to the small widget. The list scrolls within a panel no more than 300 pixels high, so many models do not take over your screen.

![Loaded-model dropdown](images/compact-models.png)

The dropdown screenshot above is a UI test using clearly named synthetic model entries. It checks scrolling without loading model weights.

Use **↕ / ↔** in the small header to switch between **horizontal (560 × 132 logical pixels)** and **vertical (260 × 300)** layouts. The choice survives restarts. These dimensions exclude native window borders; physical pixels depend on display scaling.

![Vertical compact dashboard](images/compact-vertical.png)

Drag any non-interactive part of the window—including metric labels, readings, cards and blank space—to move it. Buttons, text fields, dropdowns and scrollbars retain their normal interactions. The small circle pin control toggles always-on-top; its tooltip describes the action. Position is saved on a mode switch or close.

**↗** or **Ctrl+D** opens the full dashboard and restores the larger ECG wordmark. **Compact ↙** returns to the widget. On Windows, close hides to the tray while collection continues; Linux close exits. **Ctrl+Q**, Settings **Quit AI Pulse**, or tray **Quit AI Pulse** stops collection.

The sidebar has **Collapse menu / Expand menu** controls. Collapsed mode shows recognizable icons with tooltips and accessible names; expanded mode restores text labels.

![Collapsed sidebar](images/sidebar-collapsed.png)

Loaded models are different from agent sessions. Agent usage remains available on the expanded **Agents** page. Multiple agents can use the same model without duplicating its memory row.

## Overview

![Overview](images/overview.png)

The headline cards show GPU load, GPU core temperature availability, system memory and observed tokens in the last 24 hours. The GPU chart plots minute-average samples. Unified-memory figures remain separate because they overlap. The connection panel explains token coverage.

## Hardware

![Hardware search and sensor access](images/hardware-search.png)

Use the persistent search field in **Hardware** or **Sensors** to filter by device, sensor, unit or source. Hardware also filters its top-process list by name or PID. Search keeps its focus during live updates and remembers its text when you change tabs. No-match results are explicit.

Browse each exposed sensor with device, value, unit and source. This includes per-core load, clocks, temperatures, powers, fans and storage readings when supported. Top processes show CPU as a percentage of total host capacity and working-set memory. Working sets can overlap shared pages and should not be summed as exact physical usage.

A sensor library can provide more readings after deep access is enabled. GPU and CPU temperatures are not interchangeable. The app rejects invalid temperatures and out-of-range GPU-load values rather than displaying them as healthy. The sensor-access card identifies a missing PawnIO driver, disconnected helper or connected-but-unsupported CPU sensor. **Connect deep sensors** opens the Windows administrator prompt. GPU VR SoC remains distinct from GPU core temperature.

## Models

![Models and requests](images/models.png)

The loaded list comes from each Ollama endpoint's runtime state. The installed inventory lists model tags, quantization and on-disk model sizes. Aliases can refer to shared blobs; adding all listed sizes does not produce physical disk consumption.

Recent observed requests display input/output counts, decode rate, elapsed duration and outcome. Only gateway traffic is included. Direct Ollama clients, including clients that cannot change their server URL, may be visible as loaded models without request counts.

## Agents

Search by source, model, session ID or recorded status. Search covers all sessions in the configured retention window before displaying the newest 100 matches.

Supported local Codex and Claude Code session records supply model identity, token categories and last activity. This importer follows source formats and may need updates when those tools change. It reads metadata; it does not persist conversation contents. Claude Desktop Chat/Cowork coverage is not implied by Claude Code support.

“Working” is based on an observed Codex lifecycle event. “Recent activity” is weaker evidence. After inactivity, unresolved sessions show **Quiet / unknown**. A running process is not proof that an agent is generating.

## History and alerts

Choose from the ranges available within your configured retention period. **Settings → History retention** supports 7–365 days, with **90 days as the default**. Selecting 365 enables the one-year history view. Raw samples stay for seven days; minute aggregates, request records and sessions follow your selected period. Historical charts show means; stored min/max values preserve peaks. The first run cannot reconstruct hardware data from before installation.

**Export usage CSV** writes the full configured retention period of usage metadata into the exports folder, without the recent-request table's 20,000-row query cap. Text fields that spreadsheet programs could interpret as formulas are escaped. Headline totals query the full selected period.

Alerts cover sustained temperature thresholds, memory pressure, low data-drive space and failed backups. The default temperature warning is a user-configurable informational threshold, not a manufacturer safety limit. Alerts are displayed in the dashboard; native OS toast delivery is not implemented in this preview. No alert shuts down workloads or changes fan speeds.

## Settings

![Settings](images/settings.png)

- **Local Ollama server:** upstream target, normally port 11434.
- **Remote endpoints:** one `Name | URL` per line, with unique names. This currently discovers remote Ollama models, not their host sensors or Kubernetes pod metrics.
- **Read local usage metadata:** enables/disables future local log ingestion; existing retained records remain until retention/removal.
- **Launch at sign-in:** registers a per-user startup entry. It does not create a privileged service.
- **History retention:** 7–365 days, default 90. Increasing cannot recover deleted hardware readings; available older agent records are re-imported with deduplication. Reducing removes older live data during the next hourly cleanup. Longer retention and its backups use more disk space.
- **Ask for deep-sensor permission at launch:** optional, off by default. Windows still requires your approval; it does not silently elevate or install a privileged service.
- **Temperature warning:** threshold sustained for 30 seconds.
- **Back up now / Restore:** create a verified snapshot or choose one to restore. Restore restarts the app after confirmation and retains the old database.

Remote endpoints should be on a private network or protected transport. URLs with embedded credentials are rejected. This preview does not yet provide credential-managed remote connections.

## Troubleshooting

- **No models:** check Ollama is running and that a model is actually loaded, not just installed.
- **Tokens missing:** verify the client uses port 11435; restart AI Pulse if the gateway reports its port is occupied. Never assume a model's tokens are known from GPU usage.
- **CPU temperature missing:** follow the deep sensor instructions. Sensors still unavailable after that may be unsupported.
- **Stale readings:** the collector has stopped producing fresh samples. Restart the app and inspect the diagnostic/log files.
- **No new history while asleep:** collection cannot run when the computer is asleep/off. Gaps are expected; backups catch up after resume.
- **App already running:** open the shortcut again to bring its existing window forward, or use the tray Open action. One process per data directory prevents concurrent collectors from duplicating history.
- **Model traffic fails after closing AI Pulse:** clients configured to use its gateway need AI Pulse running. Change the client back to the direct Ollama endpoint for rollback.
