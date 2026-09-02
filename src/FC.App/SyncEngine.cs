using System.Collections.Concurrent;

namespace FC;

public sealed class SyncEngine(StateStore store, ManifestService manifest, PeerClient client) : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<Guid, FileSystemWatcher> _watchers = new();
    private Task? _loop;

    public void Start(CancellationToken ct) => _loop = Task.Run(() => RunAsync(ct), ct);

    public void Signal()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); } catch (ObjectDisposedException) { }
    }

    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        await RunCycleAsync(ct);
    }

    public async Task AllowLargeDeleteOnceAsync(Guid folderId)
    {
        await store.MutateAsync(s =>
        {
            var folder = s.Folders.FirstOrDefault(f => f.FolderId == folderId);
            if (folder is null) return;
            folder.SafetyPaused = false;
            folder.SafetyReason = string.Empty;
            folder.DeleteSafetyOverrideUntilUtc = DateTime.UtcNow.AddMinutes(2);
        });
        Signal();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(800, ct);
        while (!ct.IsCancellationRequested)
        {
            try { await RunCycleAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { await store.AddActivityAsync("Error", $"Sync cycle failed: {ex.Message}"); }

            try
            {
                var wait = _wake.WaitAsync(ct);
                var periodic = Task.Delay(TimeSpan.FromSeconds(30), ct);
                await Task.WhenAny(wait, periodic);
                if (wait.IsCompletedSuccessfully) await Task.Delay(650, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var snapshot = await store.GetSnapshotAsync();
        RefreshWatchers(snapshot);
        foreach (var folder in snapshot.Folders.Where(f => f.Enabled && Directory.Exists(f.LocalPath)))
        {
            ct.ThrowIfCancellationRequested();
            if (folder.SafetyPaused) continue;
            var localFiles = await manifest.ScanFolderAsync(folder);
            var latest = await store.GetSnapshotAsync();
            foreach (var peerId in folder.PeerDeviceIds.Distinct())
            {
                var peer = latest.Peers.FirstOrDefault(p => p.DeviceId == peerId);
                if (peer is null) continue;
                try
                {
                    var remote = await client.GetManifestAsync(peer, folder.FolderId, ct);
                    await SetPeerOnlineAsync(peer.DeviceId, true);
                    if (!remote.Found)
                    {
                        await client.OfferFolderAsync(peer, folder, ct);
                        continue;
                    }
                    await ReconcileAsync(folder, peer, localFiles, remote.Files, ct);
                    localFiles = await manifest.ScanFolderAsync(folder);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    await SetPeerOnlineAsync(peer.DeviceId, false);
                    await store.AddActivityAsync("Network", $"{peer.DeviceName} unavailable: {ShortError(ex)}");
                }
            }
        }
    }

    private async Task ReconcileAsync(SyncFolder folder, PeerDevice peer, List<FileVersionState> localFiles, List<FileVersionState> remoteFiles, CancellationToken ct)
    {
        var local = localFiles.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var remote = remoteFiles.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var remoteDeletes = remote.Values.Count(r => r.Deleted && local.TryGetValue(r.RelativePath, out var l) && !l.Deleted && VectorClock.Compare(l.Vector, r.Vector) == VectorRelation.RemoteDominates);
        var activeLocal = Math.Max(1, local.Values.Count(f => !f.Deleted));
        var state = await store.GetSnapshotAsync();
        var currentFolder = state.Folders.First(f => f.FolderId == folder.FolderId);
        var overrideDeletes = currentFolder.DeleteSafetyOverrideUntilUtc > DateTime.UtcNow;
        if (!overrideDeletes && remoteDeletes > 100 && remoteDeletes > activeLocal * 0.20)
        {
            await store.MutateAsync(s =>
            {
                var f = s.Folders.First(x => x.FolderId == folder.FolderId);
                f.SafetyPaused = true;
                f.SafetyReason = $"{remoteDeletes} incoming deletions were blocked for review.";
                s.Activity.Insert(0, new ActivityItem { Kind = "Safety", Message = $"Paused {folder.Name}: {remoteDeletes} incoming deletions from {peer.DeviceName}." });
            });
            return;
        }

        foreach (var path in local.Keys.Concat(remote.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            ct.ThrowIfCancellationRequested();
            local.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
            if (r is null) continue;
            if (l is null)
            {
                if (r.Deleted) await StoreFileStateAsync(r);
                else await ApplyRemoteAsync(folder, peer, r, ct);
                continue;
            }

            switch (VectorClock.Compare(l.Vector, r.Vector))
            {
                case VectorRelation.RemoteDominates:
                    await ApplyRemoteAsync(folder, peer, r, ct);
                    break;
                case VectorRelation.Concurrent:
                    if (l.Deleted && r.Deleted)
                    {
                        var merged = VectorClock.Increment(VectorClock.Merge(l.Vector, r.Vector), MinClockKey(l.UpdatedByDeviceId, r.UpdatedByDeviceId));
                        l.Vector = merged;
                        l.StateUtc = DateTime.UtcNow;
                        await StoreFileStateAsync(l);
                    }
                    else
                    {
                        await ResolveConflictAsync(folder, peer, l, r, ct);
                    }
                    break;
            }
        }
    }

    private async Task ApplyRemoteAsync(SyncFolder folder, PeerDevice peer, FileVersionState remote, CancellationToken ct)
    {
        var full = ManifestService.ResolveSafePath(folder, remote.RelativePath);
        if (full is null) return;
        if (remote.Deleted)
        {
            try
            {
                if (File.Exists(full))
                {
                    var recycle = Path.Combine(folder.LocalPath, ".fc-recycle", DateTime.UtcNow.ToString("yyyyMMdd"), remote.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(recycle)!);
                    File.Move(full, MakeUnique(recycle), false);
                }
                await StoreFileStateAsync(CloneForFolder(remote, folder.FolderId));
                await store.AddActivityAsync("Delete", $"{folder.Name}: removed {remote.RelativePath} from {peer.DeviceName} (recycled locally).");
            }
            catch (Exception ex) { await store.AddActivityAsync("Error", $"Could not apply deletion for {remote.RelativePath}: {ShortError(ex)}"); }
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + $".fc-partial-{Guid.NewGuid():N}";
        try
        {
            await client.DownloadFileToAsync(peer, folder.FolderId, remote.RelativePath, temp, ct);
            var hash = ManifestService.ComputeHash(temp);
            if (!string.Equals(hash, remote.Hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Hash verification failed for {remote.RelativePath}.");
            File.Move(temp, full, true);
            File.SetLastWriteTimeUtc(full, remote.LastWriteUtc);
            await StoreFileStateAsync(CloneForFolder(remote, folder.FolderId));
            await store.AddActivityAsync("Sync", $"{folder.Name}: received {remote.RelativePath} from {peer.DeviceName}.");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private async Task ResolveConflictAsync(SyncFolder folder, PeerDevice peer, FileVersionState local, FileVersionState remote, CancellationToken ct)
    {
        var localSig = $"{local.UpdatedByDeviceId}|{local.Hash}|{(local.Deleted ? 1 : 0)}";
        var remoteSig = $"{remote.UpdatedByDeviceId}|{remote.Hash}|{(remote.Deleted ? 1 : 0)}";
        var localWins = string.CompareOrdinal(localSig, remoteSig) <= 0;
        var winner = localWins ? local : remote;
        var loser = localWins ? remote : local;
        var originalFull = ManifestService.ResolveSafePath(folder, local.RelativePath)!;
        var conflictRel = BuildConflictPath(local.RelativePath, loser);
        var conflictFull = ManifestService.ResolveSafePath(folder, conflictRel)!;
        Directory.CreateDirectory(Path.GetDirectoryName(conflictFull)!);

        if (!loser.Deleted)
        {
            if (ReferenceEquals(loser, local))
            {
                if (File.Exists(originalFull)) File.Copy(originalFull, conflictFull, true);
            }
            else
            {
                var tempConflict = conflictFull + $".fc-partial-{Guid.NewGuid():N}";
                await client.DownloadFileToAsync(peer, folder.FolderId, remote.RelativePath, tempConflict, ct);
                if (!string.Equals(ManifestService.ComputeHash(tempConflict), remote.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tempConflict);
                    throw new IOException($"Hash verification failed while preserving conflict {remote.RelativePath}.");
                }
                File.Move(tempConflict, conflictFull, true);
                File.SetLastWriteTimeUtc(conflictFull, loser.LastWriteUtc);
            }
        }

        if (ReferenceEquals(winner, remote)) await ApplyRemoteAsync(folder, peer, remote, ct);
        else if (winner.Deleted && File.Exists(originalFull)) File.Delete(originalFull);

        var merged = VectorClock.Merge(local.Vector, remote.Vector);
        var winnerKey = string.IsNullOrWhiteSpace(winner.UpdatedByDeviceId) ? "winner" : winner.UpdatedByDeviceId;
        var loserKey = string.IsNullOrWhiteSpace(loser.UpdatedByDeviceId) ? "loser" : loser.UpdatedByDeviceId;
        var winnerState = CloneForFolder(winner, folder.FolderId);
        winnerState.Vector = VectorClock.Increment(merged, winnerKey);
        winnerState.UpdatedByDeviceId = winnerKey;
        winnerState.StateUtc = DateTime.UtcNow;
        await StoreFileStateAsync(winnerState);

        if (!loser.Deleted)
        {
            var conflictState = CloneForFolder(loser, folder.FolderId);
            conflictState.RelativePath = conflictRel;
            conflictState.Vector = VectorClock.Increment(merged, loserKey);
            conflictState.UpdatedByDeviceId = loserKey;
            conflictState.StateUtc = DateTime.UtcNow;
            await StoreFileStateAsync(conflictState);
        }
        await store.AddActivityAsync("Conflict", $"{folder.Name}: preserved both versions of {local.RelativePath}; conflict copy is {conflictRel}.");
    }

    private Task StoreFileStateAsync(FileVersionState file) => store.MutateAsync(s =>
    {
        s.Files.RemoveAll(f => f.FolderId == file.FolderId && string.Equals(f.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
        s.Files.Add(file);
    }, notify: false);

    private async Task SetPeerOnlineAsync(Guid peerId, bool online)
    {
        var snapshot = await store.GetSnapshotAsync();
        var peer = snapshot.Peers.FirstOrDefault(p => p.DeviceId == peerId);
        if (peer is null) return;
        var refreshSeen = online && (peer.LastSeenUtc is null || DateTime.UtcNow - peer.LastSeenUtc > TimeSpan.FromSeconds(30));
        if (peer.IsOnline == online && !refreshSeen) return;
        await store.MutateAsync(s =>
        {
            var p = s.Peers.First(x => x.DeviceId == peerId);
            p.IsOnline = online;
            if (online) p.LastSeenUtc = DateTime.UtcNow;
        });
    }

    private void RefreshWatchers(AppState state)
    {
        var valid = state.Folders.Where(f => f.Enabled && Directory.Exists(f.LocalPath)).ToDictionary(f => f.FolderId);
        foreach (var removed in _watchers.Keys.Where(id => !valid.ContainsKey(id)).ToList())
            if (_watchers.TryRemove(removed, out var watcher)) watcher.Dispose();
        foreach (var folder in valid.Values)
        {
            if (_watchers.ContainsKey(folder.FolderId)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder.LocalPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler signal = (_, _) => Signal();
                RenamedEventHandler renamed = (_, _) => Signal();
                ErrorEventHandler error = (_, _) => Signal();
                watcher.Changed += signal; watcher.Created += signal; watcher.Deleted += signal; watcher.Renamed += renamed; watcher.Error += error;
                _watchers[folder.FolderId] = watcher;
            }
            catch { }
        }
    }

    private static FileVersionState CloneForFolder(FileVersionState source, Guid folderId) => new()
    {
        FolderId = folderId,
        RelativePath = source.RelativePath,
        Length = source.Length,
        LastWriteUtc = source.LastWriteUtc,
        Hash = source.Hash,
        Deleted = source.Deleted,
        Vector = new Dictionary<string, long>(source.Vector, StringComparer.OrdinalIgnoreCase),
        UpdatedByDeviceId = source.UpdatedByDeviceId,
        StateUtc = source.StateUtc
    };

    private static string BuildConflictPath(string original, FileVersionState loser)
    {
        var directory = Path.GetDirectoryName(original.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? string.Empty;
        var file = Path.GetFileNameWithoutExtension(original);
        var ext = Path.GetExtension(original);
        var device = string.IsNullOrWhiteSpace(loser.UpdatedByDeviceId) ? "unknown" : loser.UpdatedByDeviceId[..Math.Min(8, loser.UpdatedByDeviceId.Length)];
        var hash = string.IsNullOrWhiteSpace(loser.Hash) ? "deleted" : loser.Hash[..Math.Min(8, loser.Hash.Length)].ToLowerInvariant();
        var name = $"{file}.sync-conflict-{device}-{hash}{ext}";
        return string.IsNullOrEmpty(directory) ? name : $"{directory}/{name}";
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}.{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string MinClockKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a : b;
    private static string ShortError(Exception ex) => ex.Message.Length > 140 ? ex.Message[..140] + "…" : ex.Message;
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
        _wake.Dispose();
    }
}
