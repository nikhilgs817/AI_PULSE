# AIPULSE 0.1.0 Preview

A compact native dashboard for local AI workloads, with the cyan-to-violet ECG wordmark.

## Included

- Always-on-top compact view with a row for every loaded Ollama model; expanded hardware, models, agents, history, alerts and settings pages.
- CPU/RAM/disk/network/process metrics, GPU load and allocations, and available hardware sensors.
- Exact Ollama request usage through the local observation gateway, plus supported Codex and Claude Code session metadata.
- Embedded SQLite, 90-day history, seven daily and thirteen weekly verified backup points, restore and CSV export.
- Windows setup EXE and portable ZIP, Ubuntu DEB and Linux AppImage, each bundling the runtime and database engine.
- Named remote Ollama endpoints for future k3s-hosted models.

See [installation](INSTALLATION.md), the [illustrated user guide](USER-GUIDE.md) and [validation report](VALIDATION.md).

## Scope and limitations

Ollama request token counts cover traffic sent through `http://127.0.0.1:11435`; direct traffic to Ollama is not counted. Codex/Claude monitoring relies on supported local usage records and does not promise universal desktop chat visibility. Full Kubernetes node/pod metrics are a planned extension.

Hardware sensors depend on device, driver and permissions. The Radeon 8060S test system exposes GPU VR SoC, not GPU core/hotspot temperature through this collector. Windows deep sensors use an optional signed PawnIO driver and a session-scoped elevated helper. No physical Ubuntu GPU test has been performed.

This preview is unsigned. Application Control policies may require a policy-approved or appropriately signed build. No model weights, personal databases, usage exports or credentials are included in downloads.
