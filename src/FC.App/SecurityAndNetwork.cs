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
    public static string GetBestLanAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."))
            .ToList();

        var preferred = candidates.FirstOrDefault(IsPrivateIpv4) ?? candidates.FirstOrDefault();
        return preferred?.ToString() ?? "127.0.0.1";
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] is >= 16 and <= 31);
    }
}
