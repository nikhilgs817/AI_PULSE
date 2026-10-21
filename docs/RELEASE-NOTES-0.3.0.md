# AIPULSE 0.3.0 fleet preview

**Unsigned preview:** the Windows application and installer have no publisher signature. Windows Application Control may block execution even after installation succeeds. This happened on the development PC; standalone 0.2.2 was restored, and the local 0.3.0 fleet installation remains incomplete. The portable ZIP follows the same policy. Use only where permitted by the device administrator; do not disable security protections. Ubuntu builds do not require Windows code signing.

- Independent central Hub and Host Agent runtime roles with automatic Windows Service/systemd startup and restart policies.
- Host selection in expanded and compact dashboards, All hosts status and per-host hardware, model, agent, token, history and export scope.
- Durable SQLite upload queue, original observation times, committed acknowledgements, retry deduplication and separate current-state delivery during history upload.
- Authenticated short-lived pairing, unique per-installation credentials, revocation and verified HTTPS for selectable Private LAN / VPN / Internet profiles.
- Bounded four-hour delivery journals with consistent paged history snapshots, recovery for expired dashboard cursors, and offline cache restoration.
- Guided setup/import, local service status and service removal preserving data. The same bundled application supports all roles.

The fleet setup guide documents network requirements, storage limits, user-metadata permissions and remaining recovery/companion/k3s work. Existing standalone history is preserved, and optional verified migration seeds retained minute history and usage into the central store. Public internet use requires a reachable hostname and managed publicly trusted certificate.

[Central monitoring guide](https://github.com/nikhilgs817/AI_PULSE/blob/main/docs/CENTRAL-MONITORING.md)

[Validation results and screenshots](https://github.com/nikhilgs817/AI_PULSE/blob/main/docs/VALIDATION.md). Windows and Ubuntu CI cover boot service registration/restart, live hardware delivery, offline buffering, replay, host isolation and the dashboard; a second physical host and physical Ubuntu GPU/Wi-Fi hardware have not been validated. Earlier real Ollama checks apply to standalone 0.2.2, not an installed local 0.3.0 fleet.
