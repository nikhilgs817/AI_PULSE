# 0.3.3 backup efficiency preview

Previously, daily/weekly snapshots copied the entire physical SQLite file, including free pages. The number of copies also grew with monitoring-history retention. A desktop folder backed up by an external sync client could therefore upload several large, similar files.

This update creates a consistent snapshot outside the backup folder, compacts unused pages, checks database integrity and publishes a gzip archive with its SHA-256 checksum. Hardware sampling continues during backup work. The default archive policy is 3 daily + 4 weekly restore points within 512 MiB, with the latest verified snapshot always retained. These are separate from the default 90-day live history and its optional 365-day setting.

Legacy `.db` restore points are still supported. Optimization validates the source and proves the decoded compressed archive is byte-for-byte identical before replacing it, preserves its restore date, and then rotates according to the new policy. Corrupted checksums, damaged gzip input and standalone files with pending WAL changes are rejected. Restore publishes a staged database only after verification.

The same compression, restore and per-host backup limits apply on Windows and Ubuntu. Existing cloud copies, cloud trash/version history and unrelated development artifacts are not included in the app's archive budget. AIPULSE itself does not configure or perform cloud synchronization. Keep working databases local and sync completed backups when selecting storage locations.

Local checks passed 55 data/logic checks, 57 desktop UI checks and 36 fleet checks. Backup tests cover compressed and legacy restore, checksum/gzip failures, pending WAL rejection, lossless conversion, compaction, daily/weekly selection, byte-budget enforcement and preservation of the latest oversized snapshot. Windows and Ubuntu CI validate the pushed source separately.
