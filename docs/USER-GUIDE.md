# Using AI Pulse

## Compact mode

![Compact dashboard](images/compact.png)

The fixed top strip shows GPU temperature when exposed (otherwise an explicitly labeled GPU VR SoC sensor when available), GPU utilization, local GPU allocation, physical memory used and CPU temperature. A dash means no reliable reading.

Compact mode shows five hardware readings, **Active agents**, **Tokens today**, and a **▾ Loaded models** control. The vertical layout uses tight 28-pixel rows. Click it to reveal every resident model, including host, state, model-reported memory and last observed decode speed. Close it to return to the small widget. The list scrolls within a panel no more than 300 pixels high, so many models do not take over your screen.

![Loaded-model dropdown](images/compact-models.png)

The dropdown screenshot above is a UI test using clearly named synthetic model entries. It checks scrolling without loading model weights.

Use **↕ / ↔** in the small header to switch between **horizontal (560 × 142 logical pixels)** and **vertical (260 × 256)** layouts. The choice survives restarts. These dimensions exclude native window borders; physical pixels depend on display scaling.

Metric labels use bold 12-point text vertically and 11-point text horizontally, with brighter contrast against the dark background.

**Active agents** counts sessions reporting Working or Recent activity within the last five minutes. Completed, interrupted and quiet sessions are excluded; this is recent session activity, not proof of current generation. When agent import is disabled the count shows a dash.

**Tokens today** shows observed input plus output since midnight in your local time zone, and resets at the next local day. Cached input and reasoning are already included. Hover for the exact count and on-prem/tool breakdown; sources can overlap when a tool uses the monitored Ollama gateway. The Token page retains its independent sections for detailed analysis.

![Vertical compact dashboard](images/compact-vertical.png)

Drag any non-interactive part of the window—including metric labels, readings, cards and blank space—to move it. Buttons, text fields, dropdowns and scrollbars retain their normal interactions. The small circle pin control toggles always-on-top; its tooltip describes the action. Position is saved on a mode switch or close.

**↗** or **Ctrl+D** opens the full dashboard and restores the larger ECG wordmark. **Compact ↙** returns to the widget. On Windows, close hides to the tray while collection continues; Linux close exits. **Ctrl+Q**, Settings **Quit AI Pulse**, or tray **Quit AI Pulse** stops collection.

Expanded mode includes standard **Minimize, Maximize/Restore and Close** controls on both Windows and Ubuntu. Double-click the non-interactive header to maximize or restore. Returning to compact mode leaves maximization and restores the small widget dimensions. Ubuntu window placement is managed by your desktop/compositor.

The sidebar has **Collapse menu / Expand menu** controls. Collapsed mode shows recognizable icons with tooltips and accessible names; expanded mode restores text labels.

![Collapsed sidebar](images/sidebar-collapsed.png)

Loaded models are different from agent sessions. Agent usage remains available on the expanded **Agents** page. Multiple agents can use the same model without duplicating its memory row.

## Overview

![Overview](images/overview.png)

The headline cards show GPU load, GPU core temperature availability, system memory and observed tokens in the last 24 hours. Their headings use the same 15-point semibold style as the other cards. The GPU chart plots minute-average samples. Unified-memory figures remain separate because they overlap. The connection panel explains token coverage.

## Hardware

![Hardware capacity and live usage](images/hardware-live.png)

Hardware pairs **capacity with current consumption**: total-host CPU utilization and logical processor count, GPU utilization, RAM used/total/available, local GPU memory allocated/capacity and shared GPU memory allocated/limit. GPU memory limits are OS/driver-reported; unavailable capacities show a dash. Shared and unified GPU memory overlap system RAM and must not be added to physical RAM usage.

Storage shows capacity, used space, available space and percent used for each fixed volume. Available space respects the current user's quota, so used plus available need not equal total capacity. Connected adapters appear by default; search includes disconnected adapters. Network adapters show connection status, link capacity in Mbps and current download/upload in MiB/s. Link capacity is the negotiated adapter speed, not a measured internet speed. Disconnected or warming-up counters show a dash.

Search filters hardware names, categories and specifications. Expand **Device specifications** for the full inventory. Logical processors are not physical-core counts; capacity means the OS-visible amount. Detailed temperatures, fans, power and clocks remain in Sensors.

## Sensors

![Live sensor search](images/sensors-search.png)

Sensors owns live measurements and sensor access. Its search filters device, sensor, unit, source and process names/PIDs. Hardware, Sensors and Agents each preserve their own search text and keyboard focus during live refreshes. No-match results are explicit.

Browse each exposed sensor with device, value, unit and source. This includes per-core load, clocks, temperatures, powers, fans and storage readings when supported. Top processes show CPU as a percentage of total host capacity and working-set memory. Working sets can overlap shared pages and should not be summed as exact physical usage.

A sensor library can provide more readings after deep access is enabled. GPU and CPU temperatures are not interchangeable. The app rejects invalid temperatures and out-of-range GPU-load values rather than displaying them as healthy. The sensor-access card identifies a missing PawnIO driver, disconnected helper or connected-but-unsupported CPU sensor. **Connect deep sensors** opens the Windows administrator prompt. GPU VR SoC remains distinct from GPU core temperature.

## Models

![Models and requests](images/models.png)

The loaded list comes from each Ollama endpoint's runtime state. The installed inventory lists model tags, quantization and on-disk model sizes. Aliases can refer to shared blobs; adding all listed sizes does not produce physical disk consumption.

Recent observed requests display input/output counts, decode rate, elapsed duration and outcome. Only gateway traffic is included. Direct Ollama clients, including clients that cannot change their server URL, may be visible as loaded models without request counts.

## Agents

![Agent resource consumption (synthetic process fixture)](images/agents-resources-fixture.png)

**Live app resource consumption** shows Codex, Claude and Ollama CPU usage, working-set memory and readable process count. Expand an app for each process name, PID, CPU and memory. Totals include descendants, including helper and runner processes, without the Sensors top-25 limit. Search also matches app/process names and PIDs.

CPU is a percentage of the entire host, sampled about every six seconds. New processes show **Warming up** until there are two samples; samples older than 15 seconds show unavailable. Memory sums working sets, which can share pages. These are **local app totals shared across sessions**, not per-conversation allocations or remote/cloud server usage. Per-session CPU/RAM and per-process GPU allocation are not exposed. Process identity is discovered from standard executable names and readable parent relationships; custom wrappers without a named app ancestor and protected processes may be missing. No command-line arguments or prompt text are collected. App CPU/memory/count metrics follow the normal history retention; per-PID details remain live only.

The screenshot above uses an explicitly synthetic process fixture to demonstrate totals and expandable details; it is not measured model performance.

Search by source, model, session ID or recorded status. Search covers all sessions in the configured retention window before displaying the newest 100 matches.

Supported local Codex and Claude Code session records supply model identity, token categories and last activity. This importer follows source formats and may need updates when those tools change. It reads metadata; it does not persist conversation contents. Claude Desktop Chat/Cowork coverage is not implied by Claude Code support.

“Working” is based on an observed Codex lifecycle event. “Recent activity” is weaker evidence. After inactivity, unresolved sessions show **Quiet / unknown**. A running process is not proof that an agent is generating.

## Token

![Separate on-prem and tool token usage](images/token-split.png)

This screenshot uses explicitly synthetic token records for layout verification; it is not an inference benchmark or bill.

Choose the last 24 hours or 7/30/90/180/365 days, limited by your retention setting. The page shows two independent sections:

- **On-prem models:** Ollama token usage observed through the AIPULSE gateway, grouped by model. Direct requests to port 11434 and remote endpoints used only for discovery are outside this coverage.
- **Tools & other usage:** Codex and Claude Code metadata from supported local session files, grouped by source and model. A source identifies the tool, not the model's hosting location. Claude Desktop Chat/Cowork usage may not be available.

Each section shows exact input/output totals and a model table containing input, output, cache reads, cache writes, reasoning and total tokens. The input summary also shows uncached input. Cache reads and writes are **subsets of input**; reasoning is a **subset of output**. Total is input plus output. Zero means no recorded tokens in that field, which may reflect a provider that does not expose it. Incomplete/failed observations are counted and explicitly flagged; any reported partial counts remain included.

Usage records are events, not necessarily individual requests. A tool routed through the Ollama gateway may produce both tool and on-prem observations, so the page does not add the two sections into a combined consumption total. It does not infer cloud invoices, subscription allowances or dollar costs.

All aggregates cover the entire selected period, including more than 20,000 records. **Export selected period CSV** exports the same time range and all token categories to the local exports folder. Narrow windows let the detailed tables scroll horizontally. Remote k3s discovery alone does not supply token counts; an instrumented runtime or gateway is still required.

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
