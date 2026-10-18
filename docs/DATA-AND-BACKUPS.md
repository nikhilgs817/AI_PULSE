# Data, retention and backups

SQLite is embedded in AI Pulse. There is no database server or separate installer. The app creates its schema and uses a single synchronized writer with WAL transactions.

## Default locations

- Windows: `%LOCALAPPDATA%\AI Pulse`.
- Ubuntu: `$XDG_DATA_HOME/ai-pulse`, or `~/.local/share/ai-pulse` when XDG_DATA_HOME is unset.
- Custom location: launch with `--data-dir "absolute folder"`. The owner installation can keep all app-owned data under its chosen project directory.

Under that root, `data/pulse.db` contains history; `backups/` contains verified snapshots; `exports/` contains CSV exports; `logs/` contains bounded diagnostic logs; `settings.json` contains non-secret preferences/endpoints. The live `-wal` and `-shm` files are SQLite sidecars. Do not copy just `pulse.db` while the writer is active and assume it is a complete backup.

## Retention

- Raw hardware samples: 7 days.
- Minute aggregates: configured retention, 7–365 days; default 90.
- Requests, agent sessions and resolved alerts: same configured retention.
- Backups: newest 7 distinct daily snapshots plus `ceil(retention days / 7)` weekly snapshots (13 by default, 53 for one year), deduplicated where a file serves both roles.

The default is a rolling 90-day window. Set **Settings → History retention** to up to 365 days for one-year coverage. Increasing retention cannot reconstruct deleted hardware samples; the agent importer replays available local records with deduplication. Reducing retention removes older live records at the next cleanup. Backup rotation follows the new period at the next snapshot. Cleanup runs about hourly while the app is running. SQLite can reuse freed pages; deleting old records does not necessarily shrink the physical file immediately. Disk usage depends on sensor and request volume. The app warns about low free space but does not silently delete required history to meet a fixed storage cap.

## Backup

Use **Settings → Back up now**, or leave the app running for its daily UTC snapshot/catch-up. AI Pulse uses SQLite's online backup API, validates database integrity, renames the completed snapshot and writes a SHA-256 sidecar. Partial snapshots are not successful backups. Keep the `.db` and matching `.db.sha256` together.

Same-drive backups protect against application/data mistakes; they do not protect against physical drive failure. Copy verified snapshot pairs to a second device/NAS if desired. No cloud upload is enabled by default. Retained snapshots may contain records older than the configured live query window.

## Restore

1. Open **Settings → Restore a verified backup…**.
2. Select the snapshot `.db`; its checksum sidecar must be alongside it.
3. AI Pulse checks checksum, database integrity and schema version.
4. Confirm **Restore and restart**. The collector exits, the restore helper waits for it, preserves the current database as a rollback copy, then starts the restored app.

For recovery if the UI does not open, quit all AI Pulse instances using that data folder and run:

```powershell
AiPulse.exe --restore "C:\path\pulse-snapshot.db" --data-dir "C:\path\AI Pulse"
```

```bash
ai-pulse --restore /path/pulse-snapshot.db --data-dir /path/ai-pulse
```

Ordinary uninstall preserves history. Explicitly remove the data folder only when you want to erase it. Releases contain no personal database, usage records, endpoint settings or source-app session logs.

## Privacy

The app stores metric values, model names, session identifiers, timestamps and token categories. The usage readers discard prompts, responses and tool-body content. The gateway temporarily passes request/response bytes but persists only allowlisted usage metadata. Logs store diagnostic component/error types rather than exception bodies that could contain request content. Session/model metadata can still be personal; review exports and screenshots before sharing them.
