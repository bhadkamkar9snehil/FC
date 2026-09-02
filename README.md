# FC — LAN Folder Sync

FC is a Windows-first, peer-to-peer folder synchronizer for trusted office networks. It is intentionally narrower than Syncthing: no cloud account, no relay server, no Internet discovery, and no SMB share requirement.

Two teammates install the same application, pair their computers once, select local folders, and FC keeps those folders synchronized in both directions over the LAN.

## What is implemented

- Windows desktop UI (WPF, .NET 10)
- Direct LAN peer-to-peer HTTPS transport
- One-time pairing invitations
- TLS certificate fingerprint pinning
- Per-device 256-bit access keys
- UDP LAN rediscovery for already-paired devices after DHCP/IP changes
- Folder sharing and acceptance workflow
- Different local paths on each computer
- Two-way synchronization
- FileSystemWatcher for fast change detection
- Periodic reconciliation scan to recover missed events/offline changes
- SHA-256 verification before replacing a destination file
- Vector-clock style per-file state for determining which side changed
- Deterministic conflict copies when both sides edit the same file
- Delete tombstones so deletions propagate instead of files reappearing
- Deleted files moved into `.fc-recycle` locally before removal
- Large-delete safety pause (>100 files and >20% of tracked active files)
- System tray operation
- Optional start-at-Windows-login
- Single-file self-contained Windows publish

## Current scope

FC V1 is deliberately designed for:

- Windows 10/11 x64
- trusted machines
- same office/LAN
- direct IPv4 connectivity
- whole-file transfers
- a small number of teammates and shared folders

It does **not** currently provide Internet/NAT traversal, relay servers, cloud storage, block-level delta transfer, filesystem ACL replication, VSS snapshots, or Linux/macOS clients.

## Build

Install the .NET 10 SDK, then:

```powershell
dotnet build .\FC.sln -c Release
```

To create a self-contained single EXE:

```powershell
.\scripts\Publish.ps1
```

Output:

```text
artifacts\publish\FC.exe
```

## First-time setup on both laptops

### 1. Allow FC on the office LAN

Run PowerShell as Administrator:

```powershell
.\scripts\Allow-FC-Firewall.ps1
```

The script creates two LocalSubnet-only rules on Private/Domain firewall profiles:

```text
TCP 45832   peer synchronization
UDP 45833   paired-device LAN rediscovery
```

The UDP discovery packet contains only the device ID/name, sync port and certificate fingerprint. It does not contain the persistent access key. Unknown devices are ignored.

### 2. Start FC

Run `FC.exe` on both computers.

### 3. Pair the computers

On laptop A:

1. Click **Invite teammate**.
2. Send the generated one-time code to laptop B.

On laptop B:

1. Click **Pair device**.
2. Paste the invitation code.
3. Pairing automatically registers both sides.

The invitation expires after 10 minutes and pins the TLS certificate of the invited computer.

After pairing, FC broadcasts a small LAN announcement every 10 seconds. If DHCP later changes a teammate's IP address, the stored endpoint is updated only when both the paired device ID and pinned certificate fingerprint match.

### 4. Share a folder

On laptop A:

1. Click **Add folder**.
2. Select a folder such as `C:\Projects\XStudio`.
3. Click **Share** and choose laptop B.

Laptop B receives a pending share. Click **Accept** and choose any local destination, for example:

```text
D:\Team\XStudio
```

The two local paths do not need to match.

### 5. Leave FC running

Closing the main window hides FC to the notification area; synchronization continues. Use the tray icon's **Exit** command to stop it completely. Enable **Start with Windows** if FC should start automatically after login.

## Conflict behavior

FC never silently chooses a timestamp winner when two machines independently modify the same file.

For a conflict involving `Proposal.docx`, the deterministic loser is preserved as a second file similar to:

```text
Proposal.sync-conflict-a1b2c3d4-91f825aa.docx
```

The same resolution is reproduced on both peers so the folder can converge.

## Delete safety

Normal deletions propagate to peers, but the receiving machine first moves the physical file into:

```text
<shared-folder>\.fc-recycle\YYYYMMDD\...
```

The `.fc-recycle` directory itself is excluded from synchronization.

If a peer sends more than 100 deletions and they represent more than 20% of the receiver's active tracked files, the folder is safety-paused. The UI exposes **Allow once** to approve that batch.

## Reliability model

FC deliberately uses more than one signal:

```text
FileSystemWatcher event
        +
forced hash for watcher-dirty paths
        +
30-second full tree reconciliation
        +
SHA-256 transfer verification
        +
10-second paired-device LAN rediscovery
```

Watcher events therefore make synchronization fast, but they are not the sole source of truth.

## Data location

FC keeps its local identity, peer registry, folder configuration, file vectors, tombstones, and activity history under:

```text
%LOCALAPPDATA%\FC\
```

The device TLS private key is stored in `device.pfx` in the same directory and protected by a randomly generated local password stored in FC state.

## Architecture

See:

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- [`docs/PROTOCOL.md`](docs/PROTOCOL.md)
- [`docs/ROADMAP.md`](docs/ROADMAP.md)

## Important V1 limitations

FC is usable as a LAN MVP, but it should still be treated as pre-production software until it has been exercised against your real folders and file types. In particular:

- open/locked files may be retried on a later cycle rather than copied immediately;
- changes are transferred as complete files, not changed blocks;
- a full metadata reconciliation occurs periodically, so extremely large trees will need later optimization;
- Windows ACLs, alternate data streams and EFS metadata are not replicated;
- pairing assumes the invitation code is exchanged through a trusted channel.
