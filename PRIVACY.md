# AIPULSE privacy policy

AIPULSE has no advertising, usage analytics or account sign-in. It stores hardware readings, process names/IDs and resource measurements, model identifiers, token counts, session identifiers, observation times and diagnostic status in local SQLite databases and backups. These values can still identify activity, so review screenshots and exports before sharing them.

## AI tools and local gateway

The supported Codex/Claude Code readers parse allowlisted usage metadata from readable local files. They do not persist prompts, replies or tool-body content. Windows service metadata access is scoped to the configured signed-in user's permissions; it waits when that user is unavailable. AIPULSE does not collect AI provider account passwords or subscription billing data.

The optional loopback Ollama gateway forwards the request/response bytes needed for inference to the configured Ollama server. Those bytes can contain prompts and generated content, but only allowlisted usage metadata is persisted by AIPULSE. Its default destination is local. An operator choosing a remote model endpoint is responsible for that server's policy. AIPULSE is independent of Ollama, OpenAI and Anthropic; their software and services have their own privacy policies.

## Network destinations

The app probes the configured local Ollama endpoint to discover models. Named remote endpoints and the central hub are contacted only when configured by the user or installer/operator. In central mode the Host Agent transfers allowlisted hardware, process, model and optional usage metadata to the chosen hub using verified HTTPS. The hub administrator can query enrolled hosts' metadata. Prompts, arbitrary files and remote execution commands are not part of that telemetry protocol.

This program will not transfer information to other networked systems unless specifically requested by the user or the person installing or operating it. Configuring an endpoint or enabling an explicitly described integration is such a request. No cloud backup destination is enabled by default.

## Storage, exports and removal

Hardware details are retained for seven days; summaries and usage default to 90 days and can be configured up to a year. Backups can contain records older than the live query interval. Data folders, backups, settings and credentials are never intended for release packages. CSV export and backup copying are user initiated.

Uninstalling the desktop app preserves personal data and independent fleet services. Service removal preserves history and credentials until the administrator explicitly deletes them. See [data locations, retention and backups](docs/DATA-AND-BACKUPS.md) and [service removal](docs/CENTRAL-MONITORING.md#services-upgrades-and-removal).

## Signing and distribution

If SignPath accepts this project, CI signing will send only allowlisted application/installer binaries and build-origin metadata to SignPath. It will not send the development machine's monitoring database, local endpoint settings or model conversations. Existing screenshots in the source documentation can show the maintainer's hardware configuration and example monitoring values; synthetic datasets are labelled.

## Changes and contact

This policy describes the implemented 0.3.x telemetry and proposed signing integration. New chatbot, Kubernetes and platform integrations must document their additional destinations and permissions before release. Contact [the maintainer](https://github.com/nikhilgs817) for privacy questions without posting private logs or credentials publicly.
