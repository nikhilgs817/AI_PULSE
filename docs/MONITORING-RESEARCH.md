# Monitoring research and AIPULSE design decisions

Reviewed official documentation on 2026-10-20. The following are design inputs, not claims of equivalent enterprise functionality or implemented integrations.

## Dynatrace: host health and useful drill-down

The [Hosts documentation](https://docs.dynatrace.com/docs/observe/infrastructure-observability/infrastructure-and-operations/hosts) combines host identity, health, alerts, resource usage and grouping, with navigation into entity details. AIPULSE will use a visible host context, capacity plus current consumption, alert-first status and navigation from Overview into Hardware, Sensors, Models and Agents. The [infrastructure entity documentation](https://docs.dynatrace.com/docs/observe/infrastructure-observability/infrastructure-and-operations) also informs future host/container/process relationships for k3s.

## Grafana: meaningful charts and a shared time context

[Dashboard guidance](https://grafana.com/docs/grafana/latest/visualizations/dashboards/build-dashboards/best-practices/) describes hardware utilization/saturation/errors and service request rate/errors/duration, hierarchical navigation and consistent comparison. The [time-series documentation](https://grafana.com/docs/grafana/latest/visualizations/panels-visualizations/visualizations/time-series/) covers axes and timezone presentation. Our interpretation: group host health above workload detail; use related charts with a common time range, visible date/time/timezone and units; keep unlike units separate; show legends and gaps; avoid adding overlapping memory figures or stacking independent usage sources.

## Prometheus: measurement contracts

[Instrumentation](https://prometheus.io/docs/practices/instrumentation/) and [naming guidance](https://prometheus.io/docs/practices/naming/) distinguish counters/rates from current values and make units and dimensionality explicit. AIPULSE will retain raw counter baselines and handle resets before computing network rates; bound metric dimensions; keep high-cardinality PIDs/session IDs in detail records rather than unbounded metric labels; monitor its own collection and ingestion health. The chosen first-party agent transport is outbound push with buffering because that matches the requested offline behaviour; using these measurement practices does not mean AIPULSE is a Prometheus scrape server.

## OpenTelemetry: buffers are finite

The [Collector resiliency documentation](https://opentelemetry.io/docs/collector/resiliency/) discusses queues, retry and persistent storage, including failure scenarios. This informs the durable local outbox, commit-before-ACK delivery, bounded queues, visible sync lag and deliberate disk-full handling. Persistence improves resilience but cannot promise unlimited offline retention or perfect delivery after exhausted retries/storage failures. This first increment does not require installing an OpenTelemetry Collector.

## Netdata: information density with context

The [node metrics documentation](https://learn.netdata.cloud/docs/dashboards-and-charts/tabs/metrics) and [chart documentation](https://learn.netdata.cloud/docs/dashboards-and-charts/charts) organise resource summaries followed by detailed, navigable charts. AIPULSE will use a dense Overview with short summaries, related trend panels and compact contributor tables, while retaining the small desktop widget as a separate mode.

## What to monitor

**Present collectors / first Overview increment:** CPU/GPU utilization and exposed temperatures; used/available/total RAM; GPU local/shared allocation and reported capacity; storage used/available; per-interface download/upload and reported link speed; model residency and observed generation; app CPU/working sets/process count; source-separated tokens; recorded request outcomes/duration/decode speed; collection freshness, backup health and alerts.

**Host-agent release:** uptime/boot identity, collector/provider health, service state, interface changes, host connectivity, buffering/sync lag, oldest unsent observation, queue/disk budgets, agent version and ingestion retry/error rates. Privileged/per-user data coverage must be visible.

**Additional provider work, not invented readings:** CPU scheduling pressure/load, swap/page faults; disk I/O throughput/latency/queue; network packet errors/drops; Wi-Fi signal/band/channel and separate transmit/receive link rates; GPU throttling/engine-specific activity; model queue/concurrency, request latency percentiles, time to first token and KV-cache occupancy. Collect only where a supported OS/runtime source exposes them. Internet speed tests require an explicit action because they create traffic and measure a particular external path.

**k3s stage:** node capacity/pressure, pod/container CPU/memory, limits/requests, restarts/readiness, serving-runtime demand/latency/error metrics and explicit GPU/runtime exporters. Retain cluster/namespace/node/pod/container/runtime identity without assuming pod resource use identifies token consumption.

## Overview composition

1. Host context and freshness; health/alert summary with drill-down.
2. CPU/GPU load, temperatures, RAM and GPU allocation with capacity context.
3. Matched time-series panels for load, memory, temperatures and Wi-Fi upload/download, with dated axes and a visible timezone.
4. Models loaded/generating, app CPU/RAM contributors and today's separate on-prem/tool token totals.
5. Storage/network pressure, latest observed requests, collection coverage and backup state.

This is the proposed mapping of the research to AIPULSE. Features in the roadmap stay labelled planned until their collectors, service lifecycle and UI tests pass. See [the fleet architecture](FLEET-ARCHITECTURE.md) for transport, security, migration and acceptance gates.
