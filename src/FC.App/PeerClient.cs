using System.Net;
using System.Net.Http.Json;

namespace FC;

public sealed class PeerClient(StateStore store)
{
    public async Task<PeerDevice> PairAsync(string inviteCode, CancellationToken cancellationToken = default)
    {
        var invite = PairingService.DecodeInvite(inviteCode);
        var local = await store.GetSnapshotAsync();
        using var client = CreateClient(invite.Host, invite.Port, invite.CertificateThumbprint, accessKey: null);
        var request = new PairRequest(local.Identity.DeviceId, local.Identity.DeviceName, LanAddressService.GetBestLanAddress(), local.Identity.ListenPort, local.Identity.AccessKey, local.Identity.CertificateThumbprint, invite.PairingToken);
        using var response = await client.PostAsJsonAsync("api/pair", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var paired = await response.Content.ReadFromJsonAsync<PairResponse>(cancellationToken: cancellationToken) ?? throw new InvalidOperationException("Pairing response was empty.");
        var peer = new PeerDevice
        {
            DeviceId = paired.DeviceId,
            DeviceName = paired.DeviceName,
            Host = paired.Host,
            Port = paired.Port,
            AccessKey = paired.AccessKey,
            CertificateThumbprint = paired.CertificateThumbprint,
            IsOnline = true,
            LastSeenUtc = DateTime.UtcNow
        };
        await UpsertPeerAsync(peer);
        await store.AddActivityAsync("Device", $"Paired with {peer.DeviceName} ({peer.Endpoint}).");
        return peer;
    }

    public async Task<ManifestFetchResult> GetManifestAsync(PeerDevice peer, Guid folderId, CancellationToken ct)
    {
        var local = await store.GetSnapshotAsync();
        using var client = CreateClient(peer.Host, peer.Port, peer.CertificateThumbprint, local.Identity.AccessKey);
        using var response = await client.GetAsync($"api/folders/{folderId:D}/manifest", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(false, []);
        response.EnsureSuccessStatusCode();
        var files = await response.Content.ReadFromJsonAsync<List<FileVersionState>>(cancellationToken: ct) ?? [];
        return new(true, files);
    }

    public async Task<byte[]> DownloadFileAsync(PeerDevice peer, Guid folderId, string relativePath, CancellationToken ct)
    {
        var local = await store.GetSnapshotAsync();
        using var client = CreateClient(peer.Host, peer.Port, peer.CertificateThumbprint, local.Identity.AccessKey);
        var url = $"api/folders/{folderId:D}/file?path={Uri.EscapeDataString(relativePath)}";
        return await client.GetByteArrayAsync(url, ct);
    }

    public async Task OfferFolderAsync(PeerDevice peer, SyncFolder folder, CancellationToken ct)
    {
        var local = await store.GetSnapshotAsync();
        using var client = CreateClient(peer.Host, peer.Port, peer.CertificateThumbprint, local.Identity.AccessKey);
        using var response = await client.PostAsJsonAsync("api/shares/offer", new ShareOffer(folder.FolderId, folder.Name), ct);
        response.EnsureSuccessStatusCode();
    }

    private static HttpClient CreateClient(string host, int port, string expectedThumbprint, string? accessKey)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null &&
                string.Equals(CertificateService.NormalizeThumbprint(cert.GetCertHashString()), CertificateService.NormalizeThumbprint(expectedThumbprint), StringComparison.OrdinalIgnoreCase)
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"https://{host}:{port}/"), Timeout = TimeSpan.FromSeconds(20) };
        if (!string.IsNullOrWhiteSpace(accessKey)) client.DefaultRequestHeaders.Add("X-FC-Key", accessKey);
        return client;
    }

    private Task UpsertPeerAsync(PeerDevice peer) => store.MutateAsync(s =>
    {
        s.Peers.RemoveAll(p => p.DeviceId == peer.DeviceId);
        s.Peers.Add(peer);
    });
}
