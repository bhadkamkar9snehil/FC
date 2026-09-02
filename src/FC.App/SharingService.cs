namespace FC;

public sealed class SharingService(StateStore store, PeerClient client)
{
    public async Task<SyncFolder> AddFolderAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var snapshot = await store.GetSnapshotAsync();
        var existing = snapshot.Folders.FirstOrDefault(f => string.Equals(Path.GetFullPath(f.LocalPath), path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var folder = new SyncFolder { Name = new DirectoryInfo(path).Name, LocalPath = path };
        await store.MutateAsync(s => s.Folders.Add(folder));
        await store.AddActivityAsync("Folder", $"Added {folder.Name}: {folder.LocalPath}");
        return folder;
    }

    public async Task ShareWithPeerAsync(Guid folderId, Guid peerId, CancellationToken ct = default)
    {
        await store.MutateAsync(s =>
        {
            var folder = s.Folders.Single(f => f.FolderId == folderId);
            if (!folder.PeerDeviceIds.Contains(peerId)) folder.PeerDeviceIds.Add(peerId);
        });
        var snapshot = await store.GetSnapshotAsync();
        var folder = snapshot.Folders.Single(f => f.FolderId == folderId);
        var peer = snapshot.Peers.Single(p => p.DeviceId == peerId);
        try
        {
            await client.OfferFolderAsync(peer, folder, ct);
            await store.AddActivityAsync("Share", $"Offered {folder.Name} to {peer.DeviceName}.");
        }
        catch (Exception ex)
        {
            await store.AddActivityAsync("Share", $"{folder.Name} is queued for {peer.DeviceName}; peer is currently unreachable ({ex.Message}).");
        }
    }

    public async Task AcceptPendingShareAsync(Guid pendingShareId, string localPath)
    {
        localPath = Path.GetFullPath(localPath);
        Directory.CreateDirectory(localPath);
        var snapshot = await store.GetSnapshotAsync();
        var pending = snapshot.PendingShares.Single(p => p.PendingShareId == pendingShareId);
        await store.MutateAsync(s =>
        {
            s.Folders.RemoveAll(f => f.FolderId == pending.FolderId);
            s.Folders.Add(new SyncFolder { FolderId = pending.FolderId, Name = pending.FolderName, LocalPath = localPath, PeerDeviceIds = [pending.FromPeerId] });
            s.PendingShares.RemoveAll(p => p.PendingShareId == pendingShareId);
        });
        await store.AddActivityAsync("Share", $"Accepted {pending.FolderName} from {pending.FromPeerName} into {localPath}.");
    }

    public Task DeclinePendingShareAsync(Guid pendingShareId) => store.MutateAsync(s => s.PendingShares.RemoveAll(p => p.PendingShareId == pendingShareId));
}
