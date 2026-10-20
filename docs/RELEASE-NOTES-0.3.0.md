# AIPULSE 0.3.0 fleet preview

- Independent central Hub and Host Agent runtime roles with automatic Windows Service/systemd startup and restart policies.
- Host selection in expanded and compact dashboards, All hosts status and per-host hardware, model, agent, token, history and export scope.
- Durable SQLite upload queue, original observation times, committed acknowledgements, retry deduplication and separate current-state delivery during history upload.
- Authenticated short-lived pairing, unique per-installation credentials, revocation and verified HTTPS for selectable Private LAN / VPN / Internet profiles.
- Bounded four-hour delivery journals with consistent paged history snapshots, recovery for expired dashboard cursors, and offline cache restoration.
- Guided setup/import, local service status and service removal preserving data. The same bundled application supports all roles.

The fleet setup guide documents network requirements, storage limits, user-metadata permissions and remaining recovery/companion/k3s work. Existing standalone history is preserved, and optional verified migration seeds retained minute history and usage into the central store. Public internet use requires a reachable hostname and managed publicly trusted certificate.

[Central monitoring guide](https://github.com/nikhilgs817/AI_PULSE/blob/main/docs/CENTRAL-MONITORING.md)
