# FC Architecture

## Design goal

FC solves one specific problem: keep ordinary folders synchronized between known Windows laptops that can directly reach each other on the same office network.

The architecture avoids SMB, central servers and Internet discovery.

```text
┌─────────────────────────────┐          HTTPS / LAN          ┌─────────────────────────────┐
│ Laptop A                    │◄──────────────────────────────►│ Laptop B                    │
│                             │                               │                             │
│ WPF UI                      │                               │ WPF UI                      │
│     │                       │                               │     │                       │
│ StateStore                  │                               │ StateStore                  │
│     │                       │                               │     │                       │
│ SyncEngine                  │                               │ SyncEngine                  │
│  ├─ FileSystemWatcher       │                               │  ├─ FileSystemWatcher       │
│  ├─ ManifestService        │                               │  ├─ ManifestService        │
│  ├─ VectorClock            │                               │  ├─ VectorClock            │
│  └─ PeerClient             │                               │  └─ PeerClient             │
│                             │                               │                             │
│ PeerApiHost (Kestrel/TLS)  │                               │ PeerApiHost (Kestrel/TLS)  │
└─────────────────────────────┘                               └─────────────────────────────┘
```

## Components

### WPF desktop shell

The UI handles folder selection, peer pairing, sharing, safety review, activity and startup configuration. Closing the window hides it while the process continues in the notification area.

### StateStore

`StateStore` persists JSON atomically to `%LOCALAPPDATA%\FC\state.json`.

Stored state includes:

- device identity;
- peer identities and pinned certificate fingerprints;
- shared-folder mappings;
- pending share invitations;
- per-file hashes and version vectors;
- deletion tombstones;
- bounded activity history.

V1 intentionally uses one small local state file instead of introducing a database dependency. A future SQLite migration can preserve the same logical model when folder cardinality requires indexed storage.

### Peer API

Every FC process hosts Kestrel on TCP 45832 using a per-device self-signed TLS certificate.

The certificate is not validated against a public CA. It is validated by exact fingerprint pinning learned during pairing.

### Detection

FC does not rely only on `FileSystemWatcher`.

A watcher causes a fast, debounced synchronization cycle after file changes. A periodic reconciliation scan also runs so that missed Windows filesystem events, app downtime, and network downtime do not create permanent divergence.

### Manifest and local change detection

Each tracked path records:

- SHA-256 content hash;
- file length;
- UTC last-write time;
- deleted/not-deleted state;
- version vector;
- last updating device ID.

Length and last-write time are used as a cheap guard. SHA-256 is recalculated when metadata indicates that content may have changed.

### Reconciliation

For the same relative path on two peers, version vectors are compared:

- equal → nothing to do;
- local dominates → remote peer will pull later;
- remote dominates → apply remote version locally;
- concurrent → preserve both versions using deterministic conflict resolution.

This is why FC is not simply two Robocopy passes.

### File transfer

Files are streamed to a temporary sibling path:

```text
example.bin.fc-partial-<guid>
```

After transfer FC calculates SHA-256 and compares it to the sender manifest. Only a verified file is moved into the final path.

### Deletes

A deletion is represented by a tombstone with a new vector version. Therefore a missing file can be distinguished from a file that never existed.

On receiving a deletion, FC moves the local physical file to `.fc-recycle` before storing the tombstone.

### Conflict resolution

Concurrent modifications are ordered deterministically using device/content signatures. The winning content keeps the original relative path. The other content receives a deterministic `sync-conflict` path.

Both peers derive the same merged vectors for the two resulting paths, avoiding an endless conflict loop.

## Why not Robocopy as the engine?

Robocopy remains useful for initial bulk seeding, but it is fundamentally source-to-destination. FC must know whether a missing path means "never existed" or "was deleted", and it must recognize simultaneous independent edits. Those decisions require persistent synchronization state.

A future bulk-seed path can invoke Robocopy for first transfer while FC remains authoritative for reconciliation.
