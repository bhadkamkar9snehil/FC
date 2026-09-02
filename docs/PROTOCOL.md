# FC LAN Protocol V1

## Transport

- HTTPS over TCP.
- Default port: `45832`.
- Kestrel listens on all local interfaces.
- Recommended Windows Firewall rule is limited to `LocalSubnet` and Private/Domain profiles.

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

## Main endpoints

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
