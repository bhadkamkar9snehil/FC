using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FC;

public sealed class PeerApiHost(StateStore store, PairingService pairing, ManifestService manifest)
{
    private WebApplication? _app;

    public async Task StartAsync(X509Certificate2 certificate, CancellationToken ct)
    {
        var snapshot = await store.GetSnapshotAsync();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.ListenAnyIP(snapshot.Identity.ListenPort, listen => listen.UseHttps(certificate)));
        _app = builder.Build();

        _app.MapGet("/api/health", async () =>
        {
            var state = await store.GetSnapshotAsync();
            return Results.Ok(new { state.Identity.DeviceId, state.Identity.DeviceName, state.Identity.ListenPort });
        });

        _app.MapPost("/api/pair", async (PairRequest request) =>
        {
            if (!pairing.ConsumePairingToken(request.PairingToken)) return Results.Unauthorized();
            if (request.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(request.AccessKey) || string.IsNullOrWhiteSpace(request.CertificateThumbprint))
                return Results.BadRequest("Incomplete device identity.");

            var peer = new PeerDevice
            {
                DeviceId = request.DeviceId,
                DeviceName = request.DeviceName,
                Host = request.Host,
                Port = request.Port,
                AccessKey = request.AccessKey,
                CertificateThumbprint = CertificateService.NormalizeThumbprint(request.CertificateThumbprint),
                LastSeenUtc = DateTime.UtcNow,
                IsOnline = true
            };
            await store.MutateAsync(s =>
            {
                s.Peers.RemoveAll(p => p.DeviceId == peer.DeviceId);
                s.Peers.Add(peer);
                s.Activity.Insert(0, new ActivityItem { Kind = "Device", Message = $"Paired with {peer.DeviceName} ({peer.Endpoint})." });
            });

            var state = await store.GetSnapshotAsync();
            return Results.Ok(new PairResponse(state.Identity.DeviceId, state.Identity.DeviceName, LanAddressService.GetBestLanAddress(), state.Identity.ListenPort, state.Identity.AccessKey, state.Identity.CertificateThumbprint));
        });

        _app.MapPost("/api/shares/offer", async (HttpContext context, ShareOffer offer) =>
        {
            var peer = await AuthenticateAsync(context);
            if (peer is null) return Results.Unauthorized();
            await store.MutateAsync(s =>
            {
                var alreadyAccepted = s.Folders.Any(f => f.FolderId == offer.FolderId && f.PeerDeviceIds.Contains(peer.DeviceId));
                var alreadyPending = s.PendingShares.Any(p => p.FolderId == offer.FolderId && p.FromPeerId == peer.DeviceId);
                if (!alreadyAccepted && !alreadyPending)
                {
                    s.PendingShares.Add(new PendingShare { FolderId = offer.FolderId, FolderName = offer.FolderName, FromPeerId = peer.DeviceId, FromPeerName = peer.DeviceName });
                    s.Activity.Insert(0, new ActivityItem { Kind = "Share", Message = $"{peer.DeviceName} offered folder {offer.FolderName}." });
                }
            });
            return Results.Ok();
        });

        _app.MapGet("/api/folders/{folderId:guid}/manifest", async (HttpContext context, Guid folderId) =>
        {
            var peer = await AuthenticateAsync(context);
            if (peer is null) return Results.Unauthorized();
            var state = await store.GetSnapshotAsync();
            var folder = state.Folders.FirstOrDefault(f => f.FolderId == folderId && f.Enabled && f.PeerDeviceIds.Contains(peer.DeviceId));
            if (folder is null || !Directory.Exists(folder.LocalPath)) return Results.NotFound();
            var files = await manifest.ScanFolderAsync(folder);
            return Results.Ok(files);
        });

        _app.MapGet("/api/folders/{folderId:guid}/file", async (HttpContext context, Guid folderId, string path) =>
        {
            var peer = await AuthenticateAsync(context);
            if (peer is null) return Results.Unauthorized();
            var state = await store.GetSnapshotAsync();
            var folder = state.Folders.FirstOrDefault(f => f.FolderId == folderId && f.Enabled && f.PeerDeviceIds.Contains(peer.DeviceId));
            if (folder is null) return Results.NotFound();
            var full = ManifestService.ResolveSafePath(folder, path);
            if (full is null || !File.Exists(full)) return Results.NotFound();
            return Results.File(full, "application/octet-stream", enableRangeProcessing: true);
        });

        await _app.StartAsync(ct);
        await store.AddActivityAsync("Network", $"Listening on {LanAddressService.GetBestLanAddress()}:{snapshot.Identity.ListenPort}.");
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync(TimeSpan.FromSeconds(3));
        await _app.DisposeAsync();
        _app = null;
    }

    private async Task<PeerDevice?> AuthenticateAsync(HttpContext context)
    {
        var key = context.Request.Headers["X-FC-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key)) return null;
        var state = await store.GetSnapshotAsync();
        return state.Peers.FirstOrDefault(p => string.Equals(p.AccessKey, key, StringComparison.Ordinal));
    }
}
