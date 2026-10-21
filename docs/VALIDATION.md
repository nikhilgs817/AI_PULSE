# Validation report — 0.3.0 unsigned fleet preview

## What is ready

The central hub, boot-time Host Agents, offline queue and host-scoped dashboard are implemented. The fleet implementation at source `5490db387a5dfe4f4e7630f69e0c262e97c81748` passed [Windows and Ubuntu CI run 39485624908](https://github.com/nikhilgs817/AI_PULSE/actions/runs/39485624908). The 0.3.0 release is unsigned by project choice. Its exact binary source and verified packaging run are recorded in the [release](https://github.com/nikhilgs817/AI_PULSE/releases/tag/v0.3.0); documentation updates can follow the implementation checks.

Windows passed **36 data checks, 33 fleet checks and 63 UI checks**. Ubuntu passed **36 data checks, 32 fleet checks and 63 UI checks**. The extra Windows fleet check exercises metadata access when the configured signed-in user is missing or mismatched.

Reports: [Windows data](validation/windows-data-v030.txt), [Windows fleet](validation/windows-fleet-v030.txt), [Windows UI](validation/windows-ui-v030.txt), [Ubuntu data](validation/linux-data-v030.txt), [Ubuntu fleet](validation/linux-fleet-v030.txt), [Ubuntu UI](validation/linux-ui-v030.txt).

## Services and delivery

Both CI platforms installed a real hub and Host Agent through the bundled application's service installer. The checks confirmed automatic startup registration, running services, service restart, live hardware arrival over HTTPS and service removal. Windows uses the Service Control Manager; Ubuntu uses systemd. Registration and restart were exercised; a full physical-machine reboot was not.

Fleet integration coverage includes:

- Authenticated pairing, single-use enrollment, recovery after a lost enrollment response, certificate mismatch rejection and credential revocation.
- Isolation when two hosts share device, model, session and request names; ingestion credentials cannot query or administer the fleet.
- Transactional offline observations, durable restart, lost acknowledgements, replay deduplication and exact token totals after reconnect.
- Continued real hardware collection while the hub is stopped, then reconnection and backlog delivery after hub restart.
- Fresh current-state delivery during history upload, with stale/offline readings distinguished from cached history.
- Queue exhaustion rolling back the observation with an explicit storage-pressure state.
- Consistent history snapshots while new observations arrive, stable pagination, expired journal recovery and restart with an offline dashboard cache.
- Verified HTTPS for every network profile; Internet mode rejects private-only certificate trust. No public internet endpoint was deployed in these tests.

Windows and Ubuntu packaging checks cover the self-contained runtime, installer/package installation and removal, retained data, GUI launch and Linux AppImage extraction/launch. Linux CI also persisted real CPU/RAM observations. The default retention remains 90 days, with configurable summaries/usage up to 365 days.

## Local Windows installation boundary

The standard 0.3.0 installer completed, but Windows Application Control rejected `AiPulse.dll` before application startup with error `0x800711C7`. The Code Integrity event confirmed a policy rejection. The working 0.2.2 installation was restored, and no security policy was changed. No local 0.3.0 hub or Host Agent service was installed.

The portable ZIP is also unsigned and does not avoid that policy. A matching download checksum verifies file integrity, not publisher trust or administrator approval. An administrator-approved build is required on a device that rejects this preview. This project is proceeding without a signing certificate or signing subscription; local 0.3.0 installation remains blocked rather than reported as complete.

Earlier 0.2.2 hardware, Wi-Fi, temperature-helper and real Ollama checks remain in the [preserved report](VALIDATION-0.2.2.md). Those checks must not be read as a successful installed 0.3.0 fleet test. A fresh real Ollama test through the installed 0.3.0 central setup remains pending.

## Existing-history migration

A separate local hub/agent test used a consistent copy of the existing standalone database. It delivered **1,982 usage records and 318,206 minute metric buckets**, with no missing usage or completed minute buckets and no changed historical token counts. The current incomplete minute was excluded from the aggregate comparison because collection continued. Seeding took approximately six seconds in that run; live delivery remained independent during backfill. This was a migration/integration check, not installation of boot services on the development PC.

A later pre-upgrade snapshot passed SQLite integrity checks and was retained before the attempted installation. Original standalone data was preserved through rollback. New agent installations can seed a verified retained-history snapshot once; this is separate from automatic hub-restore reconciliation, which is not implemented.

## Interface and screenshots

The shared UI checks cover host switching, an All hosts view, scoped token/model/process data, compact host selection, search, window controls, compact orientation, daily tokens/active agents, date/time chart axes, Wi-Fi link versus traffic and the one-year retention range.

![All hosts with two synthetic test machines](images/fleet-all-hosts-fixture.png)

![Selected host with isolated data](images/fleet-selected-host-fixture.png)

![Compact host selection](images/fleet-compact-fixture.png)

These are explicitly synthetic Windows/Ubuntu hosts used by the interface checks. No second physical computer has yet been paired. A full local central-installation screenshot is not available because Application Control blocked startup.

## Remaining limits

Physical Ubuntu GPU, temperature and Wi-Fi coverage remains untested. Sensor availability depends on the machine, driver and permissions; protected processes can make per-app totals incomplete. The hub cannot remotely approve sensor elevation. The Windows metadata reader waits for the matching console user and does not read private logs with SYSTEM file access. A multi-session user companion, automatic reconciliation after a hub database rollback, remote upgrades and Kubernetes pod collectors remain future work.

This preview targets small personal installations; it has no claimed large-fleet capacity. Offline queue payload is capped at 2 GiB per host, with additional disk space needed for databases and backups. See [central setup and recovery limits](CENTRAL-MONITORING.md) before enabling services.
