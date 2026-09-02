namespace FC;

public enum VectorRelation { Equal, LocalDominates, RemoteDominates, Concurrent }

public static class VectorClock
{
    public static VectorRelation Compare(IReadOnlyDictionary<string, long> local, IReadOnlyDictionary<string, long> remote)
    {
        var localGreater = false;
        var remoteGreater = false;
        foreach (var key in local.Keys.Concat(remote.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var l = local.GetValueOrDefault(key);
            var r = remote.GetValueOrDefault(key);
            if (l > r) localGreater = true;
            if (r > l) remoteGreater = true;
        }
        if (!localGreater && !remoteGreater) return VectorRelation.Equal;
        if (localGreater && !remoteGreater) return VectorRelation.LocalDominates;
        if (!localGreater && remoteGreater) return VectorRelation.RemoteDominates;
        return VectorRelation.Concurrent;
    }

    public static Dictionary<string, long> Increment(IReadOnlyDictionary<string, long>? source, string deviceId)
    {
        var result = source is null ? new(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, long>(source, StringComparer.OrdinalIgnoreCase);
        result[deviceId] = result.GetValueOrDefault(deviceId) + 1;
        return result;
    }

    public static Dictionary<string, long> Merge(IReadOnlyDictionary<string, long> a, IReadOnlyDictionary<string, long> b)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in a.Keys.Concat(b.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            result[key] = Math.Max(a.GetValueOrDefault(key), b.GetValueOrDefault(key));
        return result;
    }
}
