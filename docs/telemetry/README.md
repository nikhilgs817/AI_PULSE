# Usage setup

These profiles configure observation without bundling a model, changing API credentials or replacing your existing monitoring system. Use AIPULSE 0.3.5 or newer on every fleet role.

## Quick verification

Start AIPULSE, open **Token**, and check each source's state and last observation. The default OTLP/HTTP receiver is `http://127.0.0.1:4319`; its `/health` endpoint identifies the service. The gateway is `http://127.0.0.1:11435`. A healthy listener does not prove that a client is using it.

For a local diagnostic report, run the installed executable with `--usage-diagnostics --data-dir "YOUR DATA FOLDER"`. It writes `usage-diagnostics.json` locally with engine version, effective paths, latest records and query error codes. Keep that file private unless you review its host/path metadata.

## Ollama: choose one capture route

For explicit client routing, leave Ollama at 11434 and point compatible clients to AIPULSE at 11435. OpenAI clients use `http://127.0.0.1:11435/v1`. This observes only routed calls. It does not rewrite every other client's configuration.

For Windows default-port capture, quit AIPULSE with Ctrl+Q and run the supplied `Enable-OllamaCapture.ps1` with the installed app path and `-RestartOllama`. Example in PowerShell:

```powershell
& '.\docs\telemetry\Enable-OllamaCapture.ps1' -App '.\AiPulse.exe' -RestartOllama
```

The setup is per user, requires installed Ollama, restarts its processes and unloads resident models. It preserves model files and the original startup shortcut/settings in the **local data folder**. It starts the relay at sign-in and gives only the managed backend a process-scoped `OLLAMA_HOST=127.0.0.1:11436`. Clients keep their ordinary 11434 address. Launch AIPULSE again to import captured metadata. Do not point clients to 11436 or independently start the Ollama tray while capture owns its ports.

To restore the original Ollama startup, quit AIPULSE and run the same command with `-Disable -RestartOllama`. Original startup configuration and retained usage are preserved. The relay owns its backend process: stopping/replacing the relay requires an idle workload and one backend restart. For upgrades, quit the dashboard, disable capture with the old setup, install the update and re-enable it. Do not copy over running relay binaries.

On Linux the relay mode is available, but the Windows setup script is not. Configure the existing Ollama service to bind private loopback 11436, then configure one unprivileged relay at 11434 with `LaunchBackend=false` and an absolute app `--usage-relay --data-dir ...` command under your service manager. Keep the private `usage-relay.json` in the same root used by the desktop/host-agent. Generate a 32-byte random read key, retain 11434/11436 separation and restart the backend only when jobs are idle. Do not run a model server as root or expose its private port. An automatic Linux relay installer has not been validated.

## Claude Code terminal sessions

The Windows and shell launchers enable logs for **only the new Claude Code process**, using HTTP/JSON. They preserve existing persistent settings and keep prompt/tool content off. A running session must be relaunched through the profile to inherit it. Existing supported JSONL sessions continue importing without telemetry setup.

```powershell
& '.\docs\telemetry\Start-ClaudeWithTelemetry.ps1'
```

```sh
sh docs/telemetry/start-claude-with-telemetry.sh
```

The launchers replace the log exporter destination for that child process only. If your organization already requires another exporter, route through its Collector instead of the launcher. Do not overwrite managed settings, disable corporate policy, or copy credentials into these files.

## Claude Desktop: Code and Cowork are different

**Code tab:** configure native telemetry in the managed settings that actually reach that local/SSH/cloud environment. A prepared metadata-only `claude-code-managed-settings.example.json` is included; merge its `env` entries through your existing policy delivery method. It is an example, not an auto-installed policy. Desktop cloud sessions do not read a file placed on your PC. A VM's `127.0.0.1` means the VM, not your Windows desktop: use a reachable authenticated TLS Collector, or a Collector in the execution environment forwarding through the fleet agent. Confirm new request events arrive before calling the setup complete.

**Cowork:** official OTLP monitoring is a Team/Enterprise admin capability. Configure a reachable Collector in Cowork organization settings, use HTTP/JSON or HTTP/protobuf, keep content capture empty (`[]`) and start a new session. AIPULSE accepts official `cowork` / `api_request` usage metadata; it does not count user prompts or model text as token usage. For cloud sessions the endpoint must be reachable from Anthropic's environment. A loopback desktop address is unsuitable. The personal-plan cloud Cowork activity that has no exported/readable usage cannot be reconstructed from local process activity. Subscription percentages remain a separate concept.

For remote delivery, put a standard Collector with a valid TLS certificate and authentication in front of a local AIPULSE host-agent. Forward to that agent's loopback 4319, then use the existing authenticated fleet transport. Do not expose the embedded receiver directly, put authentication secrets in Git, weaken certificate validation or change account security settings. A hosted public Collector is optional infrastructure and is not silently deployed by AIPULSE.

## vLLM on a host

Use official **OpenTelemetry Collector Contrib 0.165.0** with `otelcol-vllm-local.yaml`. Set the actual vLLM target, keep the Collector beside the AIPULSE host-agent and run:

```sh
otelcol-contrib validate --config=otelcol-vllm-local.yaml
otelcol-contrib --config=otelcol-vllm-local.yaml
```

The first cumulative observation establishes a baseline; consume tokens after at least two scrapes to test the delta. `input`, `output`, `cacheRead` and prompt-source subsets appear under **Server / tool counters**, independently of request records. Older vLLM versions can expose fewer fields. Cached input and `input.*` source rows are subsets, so do not add them to input again. Model/engine/pod identities are retained in series keys.

## K3s / Kubernetes

Use the same metric profile with Kubernetes pod discovery (`otelcol-vllm-kubernetes.yaml`). Run **one** Collector for these targets; multiple collectors scraping the same series can introduce overlapping observation streams. Run a persistent AIPULSE host-agent beside it in the same pod/network namespace, allowing OTLP export to loopback without publishing a port. Supply the host-agent's normal enrolled `agent.json` privately, give its data/outbox a persistent volume and connect it to the existing HTTPS hub. Name this logical agent for the cluster's AI workloads; container hardware is not node hardware. Install native Host Agents separately for actual node resources.

Apply the read-only pod-discovery RBAC to the correct namespace/service account. The example defaults to `default`. Add this annotation to selected vLLM pod templates:

```yaml
metadata:
  annotations:
    aipulse.io/vllm: 'true'
spec:
  containers:
    - name: vllm
      ports:
        - name: metrics
          containerPort: 8000
```

Change the metric port to the deployed server's actual port. The Prometheus receiver maps discovery metadata to pod UID/resource identity and preserves metric labels. Keep Kubernetes credentials and fleet configuration outside the repository. RBAC grants pod get/list/watch only; it grants no secrets, exec, cluster writes or administrative access.

The shipped files are validated configuration profiles, not an automatically installed cluster stack. Before deployment, choose the namespace, signed/trusted images, persistent volume, hub reachability, workload target labels and network policy. The user's future cluster has not yet been connected, so pod discovery and real vLLM counters still need validation there.

## Retention and interruptions

Received usage follows the normal 90-day default / 365-day optional retention. The local relay's metadata survives a dashboard restart. An external Collector's example queue is memory-backed; for unattended recovery across Collector/pod restarts, enable its `file_storage` extension with a persistent volume and appropriate disk limits. No system can recover unobserved calls from a server that never exported historical counts.

See [the accounting design and source review](../TOKEN-COLLECTION.md) for normalization, coverage, deduplication and tested limits. See [Claude Code monitoring](https://code.claude.com/docs/en/monitoring-usage), [Desktop policy scope](https://code.claude.com/docs/en/desktop#managed-settings), and [Cowork monitoring](https://claude.com/docs/cowork/monitoring) for provider-side setup.
