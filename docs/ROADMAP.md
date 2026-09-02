# FC Roadmap

## V1 — LAN MVP

Implemented in the initial repository build:

- Windows WPF desktop application
- direct LAN HTTPS
- certificate-pinned pairing
- folder offers and local path mapping
- bidirectional whole-file synchronization
- watcher + periodic scan
- content verification
- tombstones
- conflict preservation
- recycle area
- large-delete protection
- system tray/startup support

## V1.1 — hardening

- automated unit/integration tests for vector and reconciliation edge cases
- test harness that launches two FC nodes on one workstation
- explicit locked-file/backoff queue instead of relying on next reconciliation
- rename detection using hash/file identity rather than create+delete semantics
- activity log filtering/export
- peer edit/remove UI
- folder remove UI
- adjustable ignore patterns (`bin`, `obj`, `.git`, etc.)
- configurable safety thresholds and recycle retention
- code signing and installer

## V1.2 — scale

- move file state from JSON to SQLite
- paged/streamed manifests rather than one JSON list
- persisted transfer queue
- parallel transfer limits
- bandwidth throttling
- hash worker pool
- incremental directory indexing
- folder statistics without loading all states into memory

## V2 — efficiency

- fixed-size or content-defined block hashing
- delta/block transfer for large modified files
- resumable transfers
- optional Robocopy-assisted initial seeding for very large local trees

## Explicitly out of scope until needed

- global discovery
- relay infrastructure
- cloud accounts
- NAT traversal
- mobile platforms
- Linux/macOS clients
- placeholder/selective sync
