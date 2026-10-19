# AIPULSE 0.2.1 Preview

- Tighter vertical compact widget: seven 28-pixel rows, 260 × 256 logical pixels overall.
- Active agents and Tokens today are now visible in both compact orientations. Horizontal mode is 560 × 142.
- Active agents counts Working/Recent activity sessions within five minutes, excluding completed, interrupted, stale and future-dated sessions. Disabled imports show unavailable.
- Tokens today uses local midnight rather than a rolling 24-hour period. Input/output totals include cached/reasoning subsets once; tool and gateway observations can overlap. Hover for exact counts and the source split.
- Hardware now pairs live CPU/GPU utilization and RAM/GPU memory consumption with capacity; storage shows used/available totals and network adapters show link capacity plus live traffic. Device specifications remain expandable.
- Agents now shows local Codex, Claude and Ollama app CPU, summed working-set memory and process counts, with expandable child-process details. Totals are shared across sessions; cloud resources and per-session CPU/RAM are not inferred.
- Process discovery and resource views work on Windows and Ubuntu, including CPU warm-up, PID reuse protection, stale-data handling and unrestricted process-count aggregation.
- The loaded-model dropdown and all existing preferences/history remain intact.

Windows installer and portable ZIP, Ubuntu DEB and Linux AppImage bundle the runtime and SQLite. This remains an unsigned preview. CPU temperature on Windows uses the existing session-scoped sensor-helper approval.

[Illustrated guide](https://github.com/nikhilgs817/AI_PULSE/blob/main/docs/USER-GUIDE.md) · [Validation](https://github.com/nikhilgs817/AI_PULSE/blob/main/docs/VALIDATION.md)
