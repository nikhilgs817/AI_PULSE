# Configuring AI Pulse

Most settings live on the **Settings** page. Everything is also in `settings.json` in the data folder, which you can edit while the app is closed.

For what each feature does, see [FEATURES.md](FEATURES.md). For install steps, see [INSTALLATION.md](INSTALLATION.md).

## Where AI Pulse keeps its files

| Platform | Data folder |
|---|---|
| Windows | `%LOCALAPPDATA%\AI Pulse` |
| Linux | `$XDG_DATA_HOME/ai-pulse`, or `~/.local/share/ai-pulse` |

Pass `--data-dir <folder>` to use another folder, for example to keep a test setup apart from your real one.

| File or folder | What it holds |
|---|---|
| `settings.json` | All settings |
| `data/pulse.db` | Readings, usage records, alerts (SQLite) |
| `backups/` | Compressed database snapshots, unless you choose another folder |
| `exports/` | CSV exports, lab reports, comparisons and bench reports |
| `labs.json` | The list of lab runs |
| `lab-pricing.json` | Per-model prices used for lab cost; created on first use |
| `evals/` | Your eval suites (`*.jsonl`) and their results |
| `drive-token.bin` | Google Drive sign-in, encrypted for your Windows user (owner-only file elsewhere); deleted by **Disconnect** |
| `logs/` | Error log |

## First run checklist

1. **Start the app.** The footer dot turns green and reads Live within a few seconds.
2. **Ollama tokens.** Point your Ollama clients at `http://127.0.0.1:11435` instead of `11434`. AI Pulse forwards every request to your real Ollama server and counts its tokens on the way. Direct traffic to 11434 is only seen through Ollama's own log, which is less exact.
3. **Claude limits.** On **Token → Coverage health**, press **Connect** on the *Claude status line* row. See [below](#claude-code-limits-and-cost).
4. **Claude cost (optional).** Start Claude Code with telemetry on. See [below](#claude-code-limits-and-cost).
5. **Budgets (optional).** Set them on **Settings**.
6. **Google Drive (optional).** Set up an OAuth client once. See [below](#google-drive).
7. **Local AI (optional).** Open **Assistant**, turn on local AI, pick a model and download it. See [ASSISTANT.md](ASSISTANT.md).

## Claude Code limits and cost

### Limits (5-hour and weekly use)

Claude Code can run a *status line* command after each reply. AI Pulse uses that to read your current limits from Claude Code itself.

- **Connect** on **Token → Coverage health** adds this entry to `~/.claude/settings.json` and keeps a backup of the old file:

  ```json
  {"statusLine": {"type": "command", "command": "\"C:/…/AiPulse.exe\" --claude-quota-statusline"}}
  ```

- If you already have a status line, AI Pulse wraps it instead of replacing it. Your command still runs and prints as before.
- The change takes effect in **new** Claude Code sessions. The row turns to Receiving after the first reply.
- To undo, restore the backup or remove the `statusLine` entry.

### Cost

Claude Code reports a cost on each API request when its telemetry is on. AI Pulse receives it on `127.0.0.1:4319` (the `TelemetryPort` setting).

- Windows: `docs\telemetry\Start-ClaudeWithTelemetry.ps1`
- Linux and macOS: `docs/telemetry/start-claude-with-telemetry.sh`

These scripts set telemetry only for the Claude session they start. They also turn prompt and tool logging off. To turn it on for every session, set the same variables yourself:

```text
CLAUDE_CODE_ENABLE_TELEMETRY=1
OTEL_LOGS_EXPORTER=otlp
OTEL_EXPORTER_OTLP_LOGS_PROTOCOL=http/json
OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://127.0.0.1:4319/v1/logs
```

[TOKEN-COLLECTION.md](TOKEN-COLLECTION.md) covers Claude Desktop, Cowork and managed settings.

### ChatGPT / Codex

Codex session logs are read automatically. For live limits, `codex` must be on your PATH, or `AIPULSE_CODEX` must point to the executable.

## Budgets and alerts

| Setting | Meaning |
|---|---|
| `DailyTokenBudget` | Tokens per day across all sources. 0 is off. Alert: *budget-tokens* |
| `DailyCostBudgetUsd` | Claude Code reported cost per day, in US dollars. 0 is off. Alert: *budget-cost* |
| `TemperatureWarning` | GPU and CPU temperature that raises an alert after 30 seconds. Default 90 °C |

Other alerts are built in:

- GPU clock throttling;
- memory pressure;
- storage;
- backup health;
- Ollama coverage gaps.

Alerts need a reading to persist before they fire. They never stop or unload a model.

## Labs from scripts: local metrics and lab API

Set **Settings → Local metrics and lab API port** (`MetricsPilotPort`), for example 18429, and restart AI Pulse. 0 turns it off.

The API listens on loopback only and refuses browser requests.

```bash
curl -X POST "http://127.0.0.1:18429/labs/start?name=rag-chunk-512"
python run_experiment.py
curl -X POST "http://127.0.0.1:18429/labs/stop"     # returns the lab with its report path
curl "http://127.0.0.1:18429/labs"                  # last 50 labs
curl "http://127.0.0.1:18429/metrics"               # Prometheus text format
```

PowerShell:

```powershell
Invoke-RestMethod -Method Post "http://127.0.0.1:18429/labs/start?name=rag-chunk-512"
Invoke-RestMethod -Method Post "http://127.0.0.1:18429/labs/stop"
```

The lab shows up on the **Agents** page like one you started by hand. You can compare it there. Lab names are up to 120 characters.

[METRICS-PILOT.md](METRICS-PILOT.md) shows how to scrape `/metrics` into VictoriaMetrics.

## Lab pricing

`lab-pricing.json` sets the list price per million tokens for each model. A lab's API cost uses these prices. Edit it to add models or change rates:

```json
[
  { "Model": "claude-sonnet-5-5", "InputPerMillion": 2.00, "OutputPerMillion": 10.00,
    "CacheReadPerMillion": null, "CacheWritePerMillion": null }
]
```

Leave the cache rates as `null` to price cache reads at 10% and cache writes at 125% of the input rate, the usual Anthropic ratio. Local Ollama models cost nothing here. Their energy use is shown separately.

## Bench and evals

- **Where the bench sends traffic.** The bench uses the gateway (`127.0.0.1:GatewayPort`) when it is on, otherwise `OllamaUrl`. Going through the gateway puts its tokens in the ledger and in any running lab.
- **Eval suites.** These are `*.jsonl` files in `evals/`, one case per line:

  ```json
  {"id": "capital", "prompt": "What is the capital of France? One word.", "contains": "Paris"}
  {"id": "json", "prompt": "Reply with only {\"ok\": true}", "regex": "^\\s*\\{\\s*\"ok\"\\s*:\\s*true\\s*\\}\\s*$"}
  {"id": "no-apology", "prompt": "Say hello.", "not": "sorry"}
  ```

  | Field | Meaning |
  |---|---|
  | `id` | Required. Unique name of the case |
  | `prompt` | Required. Sent with temperature 0 |
  | `contains` | The answer must contain this text (case-insensitive) |
  | `regex` | The answer must match this .NET regular expression |
  | `not` | The answer must not contain this text (case-insensitive) |

  A case passes only when every check it lists passes. A bad line stops the run and names its line number.
- **Results.** Results are saved to `evals/results/`. The next run of the same suite and model is compared with the last one.

## Google Drive

AI Pulse talks to Google with **your own** OAuth client. No shared secret ships with the app. You set this up once:

1. Open the Google Cloud console and create a project, or pick one.
2. Under **APIs & Services → Library**, enable the **Google Drive API**.
3. Under **OAuth consent screen**:
   - pick *External*;
   - fill in the app name and your email;
   - add yourself as a **test user**.
4. Under **Credentials**, choose **Create credentials → OAuth client ID → Desktop app**.
5. Copy the client ID and client secret into **Drive → Save client** in AI Pulse.
6. Press **Connect Google Drive…**. Your browser opens Google's sign-in, and AI Pulse receives the result on a loopback address.

The app asks for the full `drive` scope, because moving a file to trash needs write access. It only lists names, sizes and folders. It never downloads or opens your files.

While the consent screen is in *Testing*, Google expires the sign-in after 7 days. Press Connect again when that happens.

## Disk

There is nothing to configure.

- **Scan a folder…** picks any folder.
- The drive buttons scan a whole drive.
- Delete works on Windows only and goes to the Recycle Bin.

## Other settings

| Setting | Default | Meaning |
|---|---|---|
| `EnableGateway` | true | Run the Ollama gateway |
| `GatewayPort` | 11435 | Gateway port |
| `OllamaUrl` | `http://127.0.0.1:11434` | Your real Ollama server |
| `TelemetryPort` | 4319 | OTLP receiver for Claude Code, Cowork and vLLM |
| `VllmEndpoints` | `http://127.0.0.1:8000` | vLLM servers to read `/metrics` from |
| `RemoteEndpoints` | none | Other Ollama servers to watch |
| `ReadAgentUsage` | true | Read Codex and Claude Code session files for token counts |
| `RetentionDays` | 90 | History kept, 7 to 365 |
| `BackupDirectory` | data folder | Where snapshots go |
| `BackupDailyCopies` / `BackupWeeklyCopies` / `BackupBudgetMiB` | 3 / 4 / 512 | Snapshot policy |
| `StartAtLogin` | false | Start with Windows |
| `ReduceMotion` | false | Turn off page and button animation |
| `DashboardLightTheme` / `CompactLightTheme` | false | Light themes |
| `Assistant.Enabled` / `Assistant.Model` | false / `gemma3:1b` | Local AI for Ask Pulse and report analysis |

Ports and the gateway apply after a restart.

## Command line

| Flag | Use |
|---|---|
| `--data-dir <folder>` | Use another data folder |
| `--collect --seconds N` | Collect without a window for N seconds |
| `--self-test` | Run the built-in checks |
| `--diagnose` | Print a diagnostic report |
| `--backup-now`, `--restore <file>` | Back up or restore the database |
| `--claude-quota-statusline` | Used by Claude Code's status line; not for manual use |
| `--hub`, `--agent`, `--install-fleet` | Fleet setup; see [CENTRAL-MONITORING.md](CENTRAL-MONITORING.md) |
