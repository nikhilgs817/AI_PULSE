# 0.3.2 local preview

This source update combines the compact workload layout, monitoring reliability fixes and clearer token coverage. It is a local preview, not a claim of signed distribution or completed k3s/macOS/CentOS support.

## Token windows

An empty recent window previously showed only zero. Retained older records were still present, but the page did not show collection health or last activity. The update adds Today, exact dated ranges, all-retained history, per-source last-recorded timestamps and a direct older-history action. No observations show a dash with an explanation. Future-dated records are excluded from headline totals and the matching CSV range. Live supported Codex/Claude Code metadata continues to import without changing history.

## Compact workload information

The widget shows GPU utilization, local GPU allocation/capacity, available RAM, the hottest exposed CPU/GPU core reading (or an explicitly named GPU VR SoC fallback), and last completed Ollama decode speed. It also shows recent active sessions and independent local/tool token totals. The model dropdown reports observed gateway requests, loaded models and freshness. Decode speed is historical, not live throughput; shared GPU memory overlaps RAM.

## Reliability

- Live fleet snapshots remain in memory instead of rewriting pairing configuration every two seconds.
- SQLite backup uses a separate source connection; fleet maintenance releases the host registry lock before backups.
- New agents report send time so live freshness can use transport timing rather than assume synchronized host clocks. Historical timestamps are not normalized by this change.
- New agents negotiate individual bad-record rejection. Rejected records are quarantined locally, with durable hub rejection receipts so replay does not silently accept them later. Queue management UI remains pending.
- Overview offers a deterministic, read-only measured explanation without a model.

The optional assistant remains experimental, off by default, with no bundled weights and a curated Google/Microsoft catalog. Model-answer quality and full runtime setup remain pending. The [loopback metrics pilot](METRICS-PILOT.md) is also off by default and does not replace SQLite or the fleet transport.

## Validation scope

Local Windows validation passed 44 data/logic checks, 56 desktop UI checks and 36 fleet checks. These cover token aggregation, exports, history/backup integrity, compact and expanded UI, empty recent windows with older history, Today boundaries and fleet protocol regressions. CI runs Windows and Ubuntu checks on the pushed source. Real multi-machine soak testing, long-outage/reboot verification and a production metrics backend remain separate work.
