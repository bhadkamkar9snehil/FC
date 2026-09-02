using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace FC;

public sealed class PairingService(StateStore store)
{
    private readonly ConcurrentDictionary<string, DateTime> _tokens = new(StringComparer.Ordinal);

    public async Task<string> CreateInviteCodeAsync()
    {
        var state = await store.GetSnapshotAsync();
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        _tokens[token] = DateTime.UtcNow.AddMinutes(10);
        var invite = new PairInvite(1, state.Identity.DeviceId, state.Identity.DeviceName, LanAddressService.GetBestLanAddress(), state.Identity.ListenPort, state.Identity.CertificateThumbprint, token);
        var json = JsonSerializer.Serialize(invite);
        return "FC1-" + Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    public bool ConsumePairingToken(string token)
    {
        if (!_tokens.TryRemove(token, out var expires)) return false;
        return expires >= DateTime.UtcNow;
    }

    public static PairInvite DecodeInvite(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.Trim().StartsWith("FC1-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This is not a valid FC invitation code.");
        var raw = code.Trim()[4..];
        var json = Encoding.UTF8.GetString(Base64UrlDecode(raw));
        var invite = JsonSerializer.Deserialize<PairInvite>(json) ?? throw new InvalidOperationException("Invitation could not be decoded.");
        if (invite.Version != 1) throw new InvalidOperationException("This invitation was created by an unsupported FC version.");
        return invite;
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s += new string('=', (4 - s.Length % 4) % 4);
        return Convert.FromBase64String(s);
    }
}
