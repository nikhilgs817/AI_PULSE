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
- Backup restore points: **3 daily + 4 weekly** by default, deduplicated where one archive serves both roles. The default archive budget is **512 MiB**. Settings lets you choose 1–14 daily points, 0–12 weekly points and 64–8192 MiB. Older points may be removed sooner to meet the budget. The newest verified backup is always retained, even when it alone is larger than the budget.

The default is a rolling 90-day window. Set **Settings → History retention** to up to 365 days for one-year coverage. Increasing retention cannot reconstruct deleted hardware samples; the agent importer replays available local records with deduplication. Reducing retention removes older live records at the next cleanup. History retention and backup restore-point retention are separate: reducing backup copies does not shorten the data stored in the live database or each new snapshot. Cleanup runs about hourly while the app is running. SQLite can reuse freed pages; deleting old records does not necessarily shrink the physical live file immediately. The backup budget applies to managed snapshot archives, not live databases, exports or development artifacts.

## Backup

Use **Settings → Back up now**, or leave the app running for its daily UTC snapshot/catch-up. AI Pulse takes a consistent SQLite online snapshot in the OS temporary area, compacts its unused pages, validates database integrity and compresses it with gzip. Only the completed compressed archive and checksum are published to the backup folder; large raw intermediate copies stay outside that folder. Backup work runs separately from live hardware sampling. Keep the **`.db.gz` and matching `.db.gz.sha256`** together. Partial snapshots are not successful backups. Legacy `.db` + `.db.sha256` snapshots remain supported.

**Settings → Optimize existing backups** converts managed legacy snapshots without losing their contents. Each source checksum/database is checked, the compressed copy is decoded and compared byte-for-byte with the original, and only then is the raw copy replaced. Original restore dates are preserved. Rotation then applies your daily/weekly counts and budget. Unrelated files and incomplete archives are left alone. A backup with a nonempty WAL is rejected instead of silently omitting pending changes.

Same-drive backups protect against application/data mistakes; they do not protect against physical drive failure. Copy verified archive/checksum pairs to a second device/NAS if desired. AIPULSE does not upload to a cloud service itself. If your chosen backup folder is inside a sync folder or a backed-up Desktop, that external client can upload the files. Avoid syncing the changing live SQLite database and its WAL/SHM sidecars; keep working data local and sync completed compressed snapshots instead. Retained snapshots may contain records older than the configured live query window. Fleet hubs use the same bounded default policy per host.

## Restore

1. Open **Settings → Restore a verified backup…**.
2. Select the snapshot `.db.gz` (or legacy `.db`); its checksum sidecar must be alongside it.
3. AI Pulse checks checksum, database integrity and schema version.
4. Confirm **Restore and restart**. The collector exits, the restore helper waits for it, preserves the current database as a rollback copy, then starts the restored app.

For recovery if the UI does not open, quit all AI Pulse instances using that data folder and run:

```powershell
AiPulse.exe --restore "C:\path\pulse-snapshot.db.gz" --data-dir "C:\path\AI Pulse"
```

```bash
ai-pulse --restore /path/pulse-snapshot.db.gz --data-dir /path/ai-pulse
```

Ordinary uninstall preserves history. Explicitly remove the data folder only when you want to erase it. Releases contain no personal database, usage records, endpoint settings or source-app session logs.

For unattended backup creation or legacy optimization, close the app using that data folder and use `--backup-now --data-dir <folder>` or `--optimize-backups --data-dir <folder>`. These commands refuse to compete with a running app instance. Neither command downloads a model or changes monitoring retention.

## Privacy

The app stores metric values, model names, session identifiers, timestamps and token categories. The usage readers discard prompts, responses and tool-body content. The gateway temporarily passes request/response bytes but persists only allowlisted usage metadata. Logs store diagnostic component/error types rather than exception bodies that could contain request content. Session/model metadata can still be personal; review exports and screenshots before sharing them.


## Central service data

Fleet installations keep each host in a separate central SQLite database. Windows uses ProgramData\AIPULSE\hub and ProgramData\AIPULSE\agent; Ubuntu uses /var/lib/aipulse-hub and /var/lib/aipulse-agent. The desktop cache is derived data under its existing user data folder. See [central monitoring](CENTRAL-MONITORING.md) for service permissions, optional standalone migration, bounded upload journals, snapshot synchronization and coordinated recovery limitations. The ordinary standalone restore button does not restore the running hub.
