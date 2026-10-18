# Validation report

Release candidate: 0.1.0 preview. Results below distinguish completed tests from work not yet validated.

## Completed locally

- Windows and Linux x64 self-contained builds compile.
- Nine built-in data/backup/privacy tests pass: sample deduplication, request replay deduplication, Codex cumulative counters, Claude cache categories, retention boundaries, integrity-verified backup/restore, export beyond 20,000 rows with spreadsheet-formula escaping, content filtering and invalid endpoint rejection.
- Real Ollama native non-streaming and streaming inference succeeds through the observation gateway.
- Concurrent Qwen generation and embedding succeeds; both models are simultaneously resident.
- Three observed Qwen requests recorded exactly 75 input and 45 output tokens; decode rates matched provider counts and durations.
- A nonexistent model's HTTP 404 was preserved.
- Windows GPU load/local/shared memory are exposed on the test system.
- A signed PawnIO driver plus elevated diagnostic exposes CPU Tctl/Tdie and NVMe temperatures. GPU VR SoC is available; GPU core/hotspot temperature is not exposed by this collector on this machine.
- The isolated elevated sensor helper connects to the unprivileged dashboard. A live compact screenshot shows CPU temperature at 46 °C and GPU VR SoC at 33 °C alongside both loaded models.
- Actual screenshots cover Compact, Overview, Hardware, Models and Settings, with the approved ECG branding.
- GitHub's Ubuntu 24.04 runner successfully compiled and launched the GUI under Xvfb and passed the data tests. Its first DEB test caught missing archive directory entries, corrected before release.

## Test hardware

Windows 11 Pro; AMD Ryzen AI Max+ 395 / Radeon 8060S; approximately 128 GB unified memory; Ollama 0.40.5. These observations do not guarantee all sensor types on other machines.

## Remaining limitations

- No physical Ubuntu GPU/sensor test has been performed on this Windows development host.
- Full Kubernetes node/pod/GPU-exporter ingestion is future work; named remote Ollama discovery is implemented.
- No cloud billing/quota connector or universal Claude Desktop Chat/Cowork usage reader.
- No OpenTelemetry ingest, live queue/KV-cache metrics or time-to-first-token metric in this preview.
- Alerts are in-app; native desktop toasts are not implemented.
- Long-duration soak testing and manufacturer-calibrated temperature comparison are not yet complete.
- Windows public signing requires a publisher identity/certificate; the initial package is unsigned.

Do not interpret a working screenshot or compiler success as completion of an unperformed hardware/installer test. CI and release results should be appended with their actual outcomes.
