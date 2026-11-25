# Known limitations

These are the limits of the current version that are known about. Most come from what the operating system, a driver or a service exposes, not from a missing feature. Where a number is missing, AI Pulse shows `—`, never zero.

[VALIDATION.md](VALIDATION.md) lists what was tested on real hardware.

## Install and platform

- **Unsigned Windows builds.** Windows SmartScreen warns on first launch. A device with Windows Application Control (Smart App Control or a company policy) can block the app even after the installer succeeds. In that case, use a build your administrator approves. Don't turn security protections off.
- **Windows first.** Linux works for monitoring, the Disk tree and Drive. Some sensors and the Recycle Bin delete are Windows-only. macOS is not supported.
- **Monitoring only.** AI Pulse is not a thermal safety controller. Alerts never stop, throttle or unload a model.

## Hardware and GPU

- **Sensors vary by hardware and driver.** Some temperatures, fans and power readings need the optional sensor helper with administrator rights. Others are never exposed by the vendor.
- **Per-process GPU memory on Windows.** Under the standard Windows (WDDM) driver model, NVIDIA does not report how much VRAM each process holds. The process list still shows which processes use the GPU.
- **No GPU error counters.** With `nvidia-smi` installed, AI Pulse reads per-process GPU memory and clock throttle reasons. It does not read ECC counts, PCIe link state or XID error events.
- **AMD and Intel GPUs** get load, memory and temperature where Windows or the driver exposes them. NVIDIA-only details, such as throttle reasons and per-process memory, are not available.

## Tokens and cost

- **Ollama traffic on 11434 is less exact.** Only requests sent through the gateway on 11435 are counted per request with exact splits. Direct traffic is seen through Ollama's own log, when that capture is on.
- **Claude limits need the status line.** Readings arrive after each Claude Code reply in a session started after **Connect**. The Claude desktop and web apps do not run the status line.
- **Claude cost needs telemetry.** Cost comes only from Claude Code sessions with telemetry turned on. Other Claude use (web, desktop chat, API calls from your own code) has no cost reading unless that code exports OpenTelemetry.
- **Lab API cost is an estimate.** It uses list prices from `lab-pricing.json`. Discounts, batch pricing and your plan are not applied.
- **ChatGPT limits.** Live limits need the `codex` command on your PATH. Otherwise they update from Codex session logs after each turn.

## Labs

- **Reports from the API need the window.** A lab stopped through the local API while AI Pulse runs only as a background collector (`--collect`) is saved without a report. Open the app and press **Rebuild** on that lab to make one.
- **Findings are rules, not proof.** Each finding points at something to try. It does not prove the cause.

## Bench and evals

- **Ollama only.** The load sweep and evals use Ollama's `/api/generate`. vLLM and OpenAI-style servers are not benched.
- **The load sweep is small on purpose.** It runs 1, 2, 4 and 8 concurrent requests, 8 per level, so that it finishes in a few minutes. Use a dedicated load tool for long soak tests.
- **Evals are string checks.** A case passes or fails on `contains`, `regex` and `not`. There is no model-graded scoring, and no tool or multi-turn cases.

## Disk

- **Delete is Windows-only.** It sends folders to the Recycle Bin. On Linux the Disk page shows sizes but offers no delete.
- **Protected folders.** Drive roots, system folders, your profile, Downloads and the app's own folders are never offered for delete, even when they are large. Delete their contents from Explorer instead.
- **Sizes are logical sizes.** Compressed, sparse and cloud-placeholder files (OneDrive "online-only") count at their full size. Folders you cannot read are skipped and marked as not readable.

## Google Drive

- **Your own OAuth client is required.** No Google client ships with the app. You set one up once, as described in [CONFIGURATION.md](CONFIGURATION.md#google-drive).
- **Testing sign-ins expire.** While your consent screen is in *Testing*, Google ends the sign-in after 7 days. Press Connect again.
- **Only files you own.** Shared files, and files in shared drives, don't count against your quota and are not listed. Files you own that sit outside My Drive appear under **Not in My Drive**.
- **Trash still counts.** Deleting moves items to Drive trash. They still use your storage until Drive empties the trash after 30 days, or you empty it yourself in Drive.
- **Google Photos and Gmail** share the same storage quota, but their space is not broken down here.

## Local AI

- **Optional and small.** Ask Pulse and the report analysis use a small local model (about 1 to 4 billion parameters). It explains numbers AI Pulse already measured. It can be wrong, and it never changes settings or runs commands.
- **Ollama required.** The assistant needs a local Ollama server. When it is off, reports still have their measured tables and rule-based findings.

## Fleet

- **Multi-machine fleet is a preview.** The hub and host agents pass CI. A long-running production fleet install has not been verified. See [CENTRAL-MONITORING.md](CENTRAL-MONITORING.md).
- **Kubernetes** monitoring beyond vLLM counters is planned, not built. See [K3S.md](K3S.md).
