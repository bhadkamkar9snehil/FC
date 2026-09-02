using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FC;

public sealed class CertificateService(StateStore store)
{
    public string CertificatePath => Path.Combine(store.DataDirectory, "device.pfx");

    public async Task<X509Certificate2> EnsureAsync()
    {
        var state = await store.GetSnapshotAsync();
        var identity = state.Identity;
        if (!File.Exists(CertificatePath))
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN=FC-{identity.DeviceId:N}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            var bytes = generated.Export(X509ContentType.Pfx, identity.CertificatePassword);
            await File.WriteAllBytesAsync(CertificatePath, bytes);
        }

        var cert = X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, identity.CertificatePassword, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        var thumbprint = NormalizeThumbprint(cert.Thumbprint);
        if (!string.Equals(identity.CertificateThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            await store.MutateAsync(s => s.Identity.CertificateThumbprint = thumbprint);
        return cert;
    }

    public static string NormalizeThumbprint(string? value) => (value ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
}

public static class LanAddressService
{
    public static string GetBestLanAddress() => GetCandidateAddresses().FirstOrDefault() ?? "127.0.0.1";

    public static IReadOnlyList<string> GetCandidateAddresses()
    {
        var rows = new List<(string Address, int Score)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            IPInterfaceProperties properties;
            try { properties = nic.GetIPProperties(); } catch { continue; }
            var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            var physical = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || address.ToString().StartsWith("169.254."))
                    continue;
                var score = 0;
                if (physical) score += 100;
                if (hasGateway) score += 50;
                if (IsPrivateIpv4(address)) score += 25;
                rows.Add((address.ToString(), score));
            }
        }
        return rows.OrderByDescending(r => r.Score).Select(r => r.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31);
    }
}
