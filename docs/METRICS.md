# Metrics and measurement boundaries

## Hardware

CPU utilization uses operating-system counters. Windows load is based on elapsed system times; Linux uses procfs deltas. Physical memory used is total minus available. Commit is a different Windows memory measure and can exceed physical RAM.

Windows sensor details come from LibreHardwareMonitor, with a separate elevated helper for protected readings. GPU load prefers the device's core-utilization sensor. A fallback uses the busiest observed graphics/compute engine across available adapters, explicitly identified in the sensor inventory. Engine percentages are not added across different engines. GPU counters are not a direct measure of model FLOP efficiency.

GPU local allocation, GPU shared allocation and runtime-reported model memory answer different questions. On integrated/unified-memory machines, a legacy 2 GiB dedicated-memory value is not the total amount available to AI models. Shared allocations overlap host RAM. Runtime model memory does not necessarily include every driver or process allocation.

The collector exposes whatever sensors are available. GPU VR SoC is not core/hotspot temperature. CPU package power is not whole-system wall power. Motherboard fan/voltage and NPU readings are hardware-dependent; the current version has no dedicated NPU collector.

Network throughput is derived from active non-loopback interfaces and can include virtual interfaces. Avoid interpreting it as internet-only traffic or summing overlapping host/virtual adapter activity for billing. Process working sets contain shared pages.

## Model requests

Native Ollama responses supply prompt/output counts and evaluation durations. The gateway forwards streams byte-for-byte and extracts final usage metadata when present.

```text
decode tokens/second = eval_count / (eval_duration / 1,000,000,000)
```

This is model decode time, excluding loading/prompt processing. The request-duration column is gateway-observed elapsed time and includes those phases and transport overhead. Live per-token throughput, exact scheduler queue time, time-to-first-token and KV-cache occupancy are not implemented in this preview.

Embedding calls can report prompt token usage without generated output tokens. OpenAI-compatible endpoints are forwarded and their reported usage is collected when supplied; streaming clients that omit final usage leave counts unknown. Requests are never automatically retried by the gateway.

## Agent token normalization

Codex cumulative token counters are converted into incremental records; repeated counters are ignored. Cache hits are included in input and reasoning is included in output. A counter reset starts a new accounting segment.

Claude Code usage records report ordinary input, cache-read input and cache-creation input separately. AI Pulse adds those categories into total input, while preserving cache-read/write subsets. Repeated records for the same request/message update the stored maximum rather than multiplying usage. Complex future provider schema changes can require adapter updates.

Local usage-file import is bounded and incremental, with byte checkpoints. Oversized non-usage lines are skipped. Data before the configured retention window is not imported into usage totals. The app does not currently ingest OpenTelemetry or cloud account billing APIs; the verified local-record adapters provide the initial Codex/Claude integration.

## Storage and history

Hardware is sampled approximately every two seconds. Charts display sample means from minute buckets; min/max/count are retained. Gaps mean no observation. Process lists update approximately every six seconds and model discovery every four seconds. Endpoint timeouts can delay a refresh, so it is not a real-time safety system.

Request and sensor records are deduplicated before aggregation. Tokens sum across actual observed calls, including separate retries performed by the client. They do not estimate unobserved calls or subscription billing.

## Capacity and agent resources

Hardware shows OS-visible total RAM, available RAM, GPU local capacity/shared limit where exposed, fixed-volume total/used/available space and per-adapter link speed. GPU capacities are driver limits, not an extra pool to add to host RAM. Disk used is total minus total free; available respects the current user quota. Network speed is Mbps and sampled traffic is MiB/s. Only connected adapters appear initially; search includes disconnected adapters.

Agent app CPU is the summed process CPU-time delta divided by elapsed wall time and available logical processors. The collector detects named Codex, Claude and Ollama executables and attributes readable child processes to the nearest named app ancestor. Each PID is counted once. Process start times prevent PID reuse from contaminating CPU deltas or parent assignment. First samples and reset counters remain unknown. Protected/inaccessible processes can make totals incomplete.

Memory is a sum of working sets, not a unique-page physical-memory total. Session metadata does not supply a reliable process mapping, so app totals are shared across sessions. Cloud model CPU/RAM and per-process GPU allocations are unavailable. App CPU, summed working set and process count use stable `agent.<app>.*` metric IDs and follow ordinary retention; per-PID rows are live only.
