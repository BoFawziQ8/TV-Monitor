PreviousSetup.json
==================

Personal per-TV recovery history (android_id + build fingerprint hashes). It is
device-specific and intentionally not stored in the repository.

The app reads this file at runtime if present and falls back to per-TV snapshots
in %LocalAppData%\HomeTVMonitor when it is missing, so a clone builds and runs
without it.

The Store package replaces this file with {} (see work/Build-StoreMsix.ps1).