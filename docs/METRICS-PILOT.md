# Local VictoriaMetrics pilot

This pilot runs alongside AIPULSE with a separate database. It does not migrate your history, install services, or expose an endpoint to the LAN. The standalone application remains usable without VictoriaMetrics.

Create a fresh data directory under the project's build folder. Put this in its settings.json:

```json
{"MetricsPilotPort":18429,"EnableGateway":false,"ReadAgentUsage":false,"OllamaUrl":"http://127.0.0.1:1"}
```

Run a candidate AIPULSE build with `--collect --seconds 300 --data-dir <pilot-directory>`. Verify `http://127.0.0.1:18429/metrics` reports freshness and supported numeric measurements. It exposes no host names, requests, prompts or session records. Leave this port unset for ordinary use.

Use separately obtained, verified VictoriaMetrics and vmagent binaries. From the repository root, start the following in separate terminals (paths are relative to the repository):

```text
victoria-metrics-prod -httpListenAddr=127.0.0.1:18428 -storageDataPath=build/vm-pilot/store -retentionPeriod=1
vmagent-prod -httpListenAddr=127.0.0.1:18430 -promscrape.config=docs/metrics-pilot-scrape.yml -remoteWrite.url=http://127.0.0.1:18428/api/v1/write -remoteWrite.tmpDataPath=build/vm-pilot/queue -remoteWrite.maxDiskUsagePerURL=1GB
```

In `http://127.0.0.1:18428/vmui`, query `aipulse_cpu_utilization_percent` and `aipulse_sample_fresh`. Stop only the pilot VictoriaMetrics process, check vmagent's pending bytes, restart VictoriaMetrics with the same storage directory, then verify the outage's samples arrive. Missing or stale sensors must not become zero. The queue can drop old samples at its cap; it is not an exact token ledger.

Before replacing any existing fleet code, measure CPU, memory, bytes written and disk growth over representative use. Compare coverage and timestamps against AIPULSE, and test a real LAN disconnect and host reboot. Select an always-on LAN hub before configuring cross-machine transport and authentication. Existing usage records remain in SQLite.

Reference: [vmagent persistent buffering](https://docs.victoriametrics.com/victoriametrics/vmagent/#on-disk-persistence).

## Initial Windows result

On the Minisforum, verified Windows archives for v1.155.0 were used with the isolated loopback setup. vmagent attempted to forward samples while the backend was unavailable. After startup, two RAM samples timestamped before backend startup were present in VictoriaMetrics. This verifies retry and backfill across a short outage with the agent kept running. It does not establish disk persistence across an agent restart, LAN security, or sustained CPU, memory and storage costs.
