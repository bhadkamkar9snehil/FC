# FC LAN Protocol V1

## Transport

- HTTPS over TCP `45832` for synchronization.
- UDP `45833` for LAN rediscovery of already-paired devices.
- Kestrel listens on all local interfaces.
- Recommended Windows Firewall rules are limited to `LocalSubnet` and Private/Domain profiles.

## Device identity

Every installation creates:

- a GUID device ID;
- device display name (initially Windows machine name);
- random 256-bit access key;
- self-signed RSA TLS certificate;
- certificate SHA fingerprint.

## Pairing

The inviting computer creates a 10-minute one-time pairing token and serializes:

```text
version
device id
device name
IPv4 address
port
TLS certificate fingerprint
one-time pairing token
```

into a URL-safe code prefixed `FC1-`.

The joining computer:

1. decodes the invitation;
2. connects to the advertised address with HTTPS;
3. accepts the server certificate only if its fingerprint exactly matches the invitation;
4. sends its own identity, access key and certificate fingerprint to `/api/pair` together with the one-time token;
5. receives the inviter's persistent access key in response;
6. stores the peer identity locally.

The inviter consumes the pairing token, so it cannot be reused.

## Authentication after pairing

Authenticated requests contain:

```text
X-FC-Key: <caller's persistent random access key>
```

The receiver maps that key to a paired device before allowing folder manifest/file access.

TLS certificate pinning is performed by the caller on every peer connection.

## LAN rediscovery

Every running FC instance broadcasts a small UDP announcement on port `45833` approximately every 10 seconds:

```text
protocol version
device ID
device name
HTTPS sync port
TLS certificate fingerprint
```

The persistent access key and folder information are **not** broadcast.

An announcement can update a stored peer IP address only when:

1. the device ID already belongs to a paired peer; and
2. the announced TLS fingerprint matches the certificate fingerprint stored during pairing.

This allows FC to recover automatically after DHCP changes a laptop's IPv4 address without turning discovery into an authentication mechanism.

## Main HTTPS endpoints

### `GET /api/health`

Unauthenticated liveness metadata.

### `POST /api/pair`

One-time pairing endpoint. Authorized by the expiring pairing token.

### `POST /api/shares/offer`

Authenticated. Creates an idempotent pending folder offer.

### `GET /api/folders/{folderId}/manifest`

Authenticated. The calling device must be registered on the folder's peer access list.

Returns the local per-file state records for the folder.

### `GET /api/folders/{folderId}/file?path=<relative>`

Authenticated. The calling device must have folder access. Path traversal is rejected by canonical root checking.

Streams the current file bytes.

## Synchronization state

A manifest entry carries:

```text
folder ID
relative path
file length
UTC last-write time
SHA-256 content hash
deleted flag
version vector
last updating device ID
```

A missing physical file with a persisted `deleted=true` entry is a tombstone. It is therefore distinct from a path that has never existed.

Vector comparison determines whether local or remote state dominates or whether both sides changed concurrently.

## File transfer

Files are streamed to a sibling temporary path and SHA-256 verified before replacement of the destination path. Temporary FC partial files are excluded from manifests.

## Trust model

FC V1 assumes:

- both computers are controlled by teammates;
- the office LAN itself is not treated as sufficient authentication;
- pairing codes are exchanged through a trusted side channel.

The access key is protected in transit by TLS, and TLS is authenticated using the pinned fingerprint from pairing rather than public PKI.

## Not implemented in V1

- public CA certificates;
- revocation lists;
- automatic key rotation;
- encrypted invitation envelopes;
- Internet relay/NAT traversal;
- central account service;
- remote administration.
