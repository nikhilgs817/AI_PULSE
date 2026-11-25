# What AI Pulse does

AI Pulse is a desktop monitor for a machine that runs local and hosted AI models. It is built for lab work. You can record a run, change something (a prompt, a model, a context size, a cache setting), record again and see what the change did to tokens, cost, latency and hardware.

Everything runs on your machine. Prompts and replies are never saved. Only usage numbers, timings and hardware readings are kept.

This page lists every feature by page. For setup, see [CONFIGURATION.md](CONFIGURATION.md). For known limits, see [LIMITATIONS.md](LIMITATIONS.md).

## Pages at a glance

| Page | What it is for |
|---|---|
| Overview | One screen with load, memory, temperatures, active models, today's tokens and the health of every collector |
| Hardware | CPU, GPU, RAM, VRAM, storage and network against capacity, plus per-process GPU memory |
| Sensors | Temperatures, power, clocks and fans, with access status where a sensor is blocked |
| Models | Ollama models installed, loaded, their VRAM share, load and eviction history, decode speed |
| Agents | Codex, Claude and Ollama app resources, recent sessions, **labs**, **model bench** and **evals** |
| Token | Token and cost accounting by source, model and agent; budgets; coverage health; CSV export |
| History | Seven days of detailed readings and 7 to 365 days of summaries |
| Alerts | Active alerts, recent checks and coverage health |
| Disk | Folder-size tree for your local drives, with delete to the Recycle Bin on Windows |
| Drive | Folder-size tree for your Google Drive, with move to Drive trash |
| Assistant | Ask Pulse, an optional local model that answers questions about your own readings |
| Settings | Everything you can change, from ports and budgets to theme and motion |

A compact widget (horizontal or vertical) keeps the critical readings on screen while you work.

## Token and cost tracking

- **Sources.** The app counts tokens from:
  - Ollama requests through the local gateway;
  - Ollama's own request log;
  - Codex and Claude Code session files;
  - Claude Code and Cowork OpenTelemetry;
  - vLLM `/metrics`.

  Each source shows exact input, output, cache read, cache write and reasoning splits.
- **Claude limits.** The Claude status line writes your 5-hour and weekly usage after every Claude Code reply. AI Pulse reads that, so Claude limits no longer depend on an undocumented, rate-limited endpoint. The Token page shows this under Coverage health with a one-click **Connect**.
- **ChatGPT / Codex limits.** These are read live from the `codex` command when it is on your PATH. Otherwise they come from Codex session logs after each turn.
- **Cost.**
  - Claude Code cost comes from the `cost_usd` that Claude Code reports on its own telemetry events, once you turn its telemetry on.
  - Lab reports also price API usage at list rates from `lab-pricing.json`, which you can edit.
- **Budgets.** Set a daily token budget, a daily Claude Code cost budget, or both. An alert fires at the limit.
- **Coverage health.** Every collector is listed as Receiving, Waiting for traffic, Stale, Broken or Not available, with the time it last reported. Missing data shows as `—`, never as zero.

## Labs: before and after

Labs are the core workflow for the MLOps lab use case.

1. On **Agents**, type a name and press **Start recording**.
2. Run your experiment.
3. Press **Stop and build report**. The report is a Markdown file in `exports/` and covers:
   - tokens by model, agent and source;
   - cache hit rate;
   - request count and error rate;
   - median and p95 time to first token and total latency;
   - decode tokens per second;
   - model loads and VRAM evictions;
   - GPU peak temperature, power and energy per 1,000 tokens;
   - API cost at list prices.
4. A **What to look at** section lists rule-based findings with a concrete thing to try. Examples:
   - one model or agent using most of the tokens;
   - very high input/output ratio;
   - low cache hits on large inputs;
   - error rate of 5% or more;
   - long-tail first-token latency;
   - models swapping in and out of VRAM;
   - GPU at 85 °C or more.
5. Press **Compare with previous** on any past lab to get a side-by-side table with what got better and what got worse.
6. If the local assistant is on, both the lab report and the comparison end with a short **Analysis (local AI)** section. It explains the numbers in plain words and suggests a next step.

Scripts can start and stop labs too, through the local API (see [CONFIGURATION.md](CONFIGURATION.md#local-metrics-and-lab-api)). That fits labs run from a notebook or a CI job on the same machine.

## Model bench and evals

These are on the **Agents** page, under the lab card. They work with any Ollama model you have installed.

- **Load sweep.** This sends the same prompt at 1, 2, 4 and 8 concurrent requests (8 per level, 128 tokens each). For each level it shows p50 and p95 latency, tokens per second, requests per second and errors.
  - It names the **knee**: the last concurrency level before throughput stops growing, errors appear or p95 doubles.
  - The run goes through the gateway when it is on, so the tokens also show up in the usage ledger and in any lab you are recording.
- **Evals.** A small regression suite you write in JSONL, run against a model, with pass rate, tokens and mean latency.
  - The next run of the same suite and model shows a before/after table and lists the cases that flipped.
  - A sample suite is created on first use. **Edit evals** opens the folder.
- **Analysis.** With the assistant on, both results end with a short plain-language analysis.

## Disk space

**Disk** scans a drive or folder in the background and shows a tree of folders by size, largest first.

- Folders that hold AI files carry a label, so the big ones are easy to spot: Hugging Face models and cache, Ollama models, pip/conda/uv caches, Node packages, and similar.
- **Show** opens a folder in Explorer.
- **Delete…** sends a folder to the Recycle Bin after a confirmation that names the folder, its size and how many files it holds. Cancel is the default button.
- Some folders are never offered for delete: drive roots, Windows, Program Files, your profile, Downloads, the app's own folders, `$Recycle.Bin` and `System Volume Information`.

## Google Drive space

**Drive** does the same for your Google Drive.

- **Connect Google Drive…** signs in through your browser with your own Google OAuth client. AI Pulse never sees your password.
- **Scan Drive** lists the files you own and builds a folder tree with sizes. Shared files and files outside My Drive are grouped under **Not in My Drive**.
- The top cards show:
  - account storage used out of your quota;
  - space used by Drive files;
  - space used by Drive trash.

  The page also lists your 15 largest files. **Show** opens a file or folder in Google Drive in your browser.
- **Delete…** moves a file or a whole folder to Drive trash after a confirmation. You can restore it from Drive for 30 days.
- **Disconnect** revokes the sign-in and deletes the stored token.

## Ask Pulse and AI analysis

The optional local assistant runs a small Ollama model. See [ASSISTANT.md](ASSISTANT.md) for model choices.

- **Ask Pulse** answers questions about your own readings, for example "why is my GPU hot?" or "which model used most tokens today?". It answers from a snapshot that includes:
  - live hardware readings;
  - today's top models;
  - Claude Code cost against your budget;
  - the token budget;
  - active alerts;
  - the lab being recorded.
- **Analysis.** Lab reports, lab comparisons, eval runs and load sweeps get a short written analysis appended when the assistant is on.

## Fleet and central monitoring

One machine can act as a hub and collect readings from other machines running AI Pulse as host agents. See [CENTRAL-MONITORING.md](CENTRAL-MONITORING.md) and [FLEET-ARCHITECTURE.md](FLEET-ARCHITECTURE.md).

## Data and backups

- Readings live in a local SQLite database. Verified, compressed snapshots are kept: 3 daily and 4 weekly, within a 512 MiB budget by default.
- Retention is 90 days by default (7 to 365).
- See [DATA-AND-BACKUPS.md](DATA-AND-BACKUPS.md).

## Look and feel

- Dark and light themes.
- Pages slide in gently, the footer's live dot breathes while data is fresh, and bars ease to new values. Buttons respond to a press.
- **Settings → Reduce motion** turns all of this off.
