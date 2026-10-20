# ADR-0001: Central hub with autonomous host agents

**Status:** Accepted architecture direction; implementation staged.
**Date:** 2026-10-20
**Decider:** AIPULSE owner

## Context

The current desktop app directly owns its collectors and database. The owner wants one dashboard for several Windows/Ubuntu systems, agents collecting from boot while offline, automatic backfill on reconnection, host selection, and a choice of LAN, VPN or internet transport.

## Decision

Use a separately supervised hub service and lightweight outbound-only Host Agents. Each agent owns local collection/history and a durable outbox. The desktop UI queries host-scoped data from the hub and offers a common host selector, including All hosts. Keep bundled .NET/SQLite; introduce no mandatory external database or messaging server for the initial personal-fleet release.

Pair each installation with its own revocable credential over verified HTTPS. Acknowledge records only after commit; deduplicate/revision-check retries. Distinguish transport connectivity from data freshness and history sync. Separate the machine service, user metadata companion and privileged sensor broker.

## Options considered

**Desktop polls every host directly.** Small initial change, but leaves gaps when the desktop is off and requires inbound agent endpoints. Does not meet offline collection/backfill requirements by itself.

**Hub service with buffered agents (chosen).** Supports autonomous boot-time collection, NAT-friendly outbound traffic and central retention with a bounded operating footprint. Requires service installation, identity, authenticated ingestion and migration work.

**Require Prometheus/Grafana/OpenTelemetry infrastructure.** Broad integrations and future interoperability, but adds deployment and administration to a product intended to install with bundled dependencies. Borrow sound metric/UI/resilience practices and leave adapter interoperability as a later capability.

## Consequences

Closing the dashboard window no longer stops central ingestion; turning off its hub computer still makes agents buffer. Per-user token coverage and unattended hardware access need explicit service-aware implementation. All database/query/export identities must include the host; timestamps, retries, counter resets, queue limits and restore reconciliation become first-class concerns.

## Action items

1. Implement the Overview, Wi-Fi and dated-chart increment with the current collectors.
2. Extract shared contracts/collectors/storage; add OS service lifecycle and controlled local migration.
3. Add pairing, authenticated hub ingestion, durable outbox, reconciliation and fault-injection tests.
4. Add the host dropdown, All hosts view, scoped navigation and selectable network profiles.
5. Validate role-specific Windows/Ubuntu installers and actual multi-system acceptance.

The detailed [fleet design](../FLEET-ARCHITECTURE.md) defines the contracts, deployment behaviour and release gates. [Monitoring research](../MONITORING-RESEARCH.md) records the official product documentation used as input.
