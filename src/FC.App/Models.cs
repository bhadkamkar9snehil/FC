namespace FC;

public sealed class AppState
{
    public DeviceIdentity Identity { get; set; } = DeviceIdentity.Create();
    public List<PeerDevice> Peers { get; set; } = [];
    public List<SyncFolder> Folders { get; set; } = [];
    public List<PendingShare> PendingShares { get; set; } = [];
    public List<FileVersionState> Files { get; set; } = [];
    public List<ActivityItem> Activity { get; set; } = [];
    public bool RunAtStartup { get; set; }
}

public sealed class DeviceIdentity
{
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    public string DeviceName { get; set; } = Environment.MachineName;
    public int ListenPort { get; set; } = 45832;
    public string AccessKey { get; set; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    public string CertificatePassword { get; set; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    public string CertificateThumbprint { get; set; } = string.Empty;
    public static DeviceIdentity Create() => new();
}

public sealed class PeerDevice
{
    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string AccessKey { get; set; } = string.Empty;
    public string CertificateThumbprint { get; set; } = string.Empty;
    public DateTime? LastSeenUtc { get; set; }
    public bool IsOnline { get; set; }
    public string Endpoint => $"{Host}:{Port}";
}

public sealed class SyncFolder
{
    public Guid FolderId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<Guid> PeerDeviceIds { get; set; } = [];
    public bool SafetyPaused { get; set; }
    public string SafetyReason { get; set; } = string.Empty;
    public DateTime? DeleteSafetyOverrideUntilUtc { get; set; }
}

public sealed class PendingShare
{
    public Guid PendingShareId { get; set; } = Guid.NewGuid();
    public Guid FolderId { get; set; }
    public string FolderName { get; set; } = string.Empty;
    public Guid FromPeerId { get; set; }
    public string FromPeerName { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class FileVersionState
{
    public Guid FolderId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string Hash { get; set; } = string.Empty;
    public bool Deleted { get; set; }
    public Dictionary<string, long> Vector { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string UpdatedByDeviceId { get; set; } = string.Empty;
    public DateTime StateUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ActivityItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
}

public sealed record PairInvite(int Version, Guid DeviceId, string DeviceName, string Host, int Port, string CertificateThumbprint, string PairingToken);
public sealed record PairRequest(Guid DeviceId, string DeviceName, string Host, int Port, string AccessKey, string CertificateThumbprint, string PairingToken);
public sealed record PairResponse(Guid DeviceId, string DeviceName, string Host, int Port, string AccessKey, string CertificateThumbprint);
public sealed record ShareOffer(Guid FolderId, string FolderName);
public sealed record ManifestFetchResult(bool Found, List<FileVersionState> Files);
