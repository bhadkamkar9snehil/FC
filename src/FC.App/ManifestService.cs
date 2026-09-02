using System.Security.Cryptography;

namespace FC;

public sealed class ManifestService(StateStore store)
{
    public async Task<List<FileVersionState>> ScanFolderAsync(SyncFolder folder)
    {
        if (!Directory.Exists(folder.LocalPath)) return [];
        var snapshot = await store.GetSnapshotAsync();
        var deviceKey = snapshot.Identity.DeviceId.ToString("N");
        var known = snapshot.Files.Where(f => f.FolderId == folder.FolderId)
            .ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var observed = new Dictionary<string, FileVersionState>(StringComparer.OrdinalIgnoreCase);

        foreach (var fullPath in Directory.EnumerateFiles(folder.LocalPath, "*", SearchOption.AllDirectories))
        {
            var relative = Normalize(Path.GetRelativePath(folder.LocalPath, fullPath));
            if (ShouldIgnore(relative)) continue;
            try
            {
                var info = new FileInfo(fullPath);
                known.TryGetValue(relative, out var old);
                var metadataMatches = old is not null && !old.Deleted && old.Length == info.Length && Math.Abs((old.LastWriteUtc - info.LastWriteTimeUtc).TotalMilliseconds) < 2;
                var hash = metadataMatches ? old!.Hash : ComputeHash(fullPath);
                if (old is not null && !old.Deleted && string.Equals(old.Hash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    old.Length = info.Length;
                    old.LastWriteUtc = info.LastWriteTimeUtc;
                    observed[relative] = old;
                }
                else
                {
                    observed[relative] = new FileVersionState
                    {
                        FolderId = folder.FolderId,
                        RelativePath = relative,
                        Length = info.Length,
                        LastWriteUtc = info.LastWriteTimeUtc,
                        Hash = hash,
                        Deleted = false,
                        Vector = VectorClock.Increment(old?.Vector, deviceKey),
                        UpdatedByDeviceId = deviceKey,
                        StateUtc = DateTime.UtcNow
                    };
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var old in known.Values.Where(v => !observed.ContainsKey(v.RelativePath)))
        {
            if (old.Deleted) { observed[old.RelativePath] = old; continue; }
            observed[old.RelativePath] = new FileVersionState
            {
                FolderId = folder.FolderId,
                RelativePath = old.RelativePath,
                Deleted = true,
                Hash = string.Empty,
                Length = 0,
                LastWriteUtc = DateTime.UtcNow,
                Vector = VectorClock.Increment(old.Vector, deviceKey),
                UpdatedByDeviceId = deviceKey,
                StateUtc = DateTime.UtcNow
            };
        }

        await store.MutateAsync(s =>
        {
            s.Files.RemoveAll(f => f.FolderId == folder.FolderId);
            s.Files.AddRange(observed.Values);
        }, notify: false);
        return observed.Values.ToList();
    }

    public static string Normalize(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/');

    public static string? ResolveSafePath(SyncFolder folder, string relativePath)
    {
        relativePath = Normalize(relativePath);
        if (relativePath.Length == 0) return null;
        var root = Path.GetFullPath(folder.LocalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public static string ComputeHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool ShouldIgnore(string relative) =>
        relative.StartsWith(".fc-recycle/", StringComparison.OrdinalIgnoreCase) ||
        relative.Contains(".fc-partial-", StringComparison.OrdinalIgnoreCase);
}
