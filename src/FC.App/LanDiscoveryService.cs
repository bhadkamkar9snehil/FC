using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace FC;

public sealed class LanDiscoveryService(StateStore store) : IDisposable
{
    public const int DiscoveryPort = 45833;
    private UdpClient? _udp;
    private Task? _runner;
    public event EventHandler? PeerAddressChanged;

    public void Start(CancellationToken ct)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _udp.EnableBroadcast = true;
        _runner = Task.WhenAll(SendLoopAsync(ct), ReceiveLoopAsync(ct));
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp is not null)
        {
            try
            {
                var state = await store.GetSnapshotAsync();
                var packet = new DiscoveryPacket(1, state.Identity.DeviceId, state.Identity.DeviceName, state.Identity.ListenPort, state.Identity.CertificateThumbprint);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
                await _udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp is not null)
        {
            try
            {
                var received = await _udp.ReceiveAsync(ct);
                var packet = JsonSerializer.Deserialize<DiscoveryPacket>(received.Buffer);
                if (packet is null || packet.Version != 1) continue;
                var state = await store.GetSnapshotAsync();
                if (packet.DeviceId == state.Identity.DeviceId) continue;
                var peer = state.Peers.FirstOrDefault(p => p.DeviceId == packet.DeviceId);
                if (peer is null) continue;
                if (!string.Equals(CertificateService.NormalizeThumbprint(peer.CertificateThumbprint), CertificateService.NormalizeThumbprint(packet.CertificateThumbprint), StringComparison.OrdinalIgnoreCase))
                    continue;
                var discoveredHost = received.RemoteEndPoint.Address.ToString();
                if (string.Equals(peer.Host, discoveredHost, StringComparison.OrdinalIgnoreCase) && peer.Port == packet.ListenPort) continue;

                await store.MutateAsync(s =>
                {
                    var p = s.Peers.First(x => x.DeviceId == packet.DeviceId);
                    var previous = p.Endpoint;
                    p.Host = discoveredHost;
                    p.Port = packet.ListenPort;
                    s.Activity.Insert(0, new ActivityItem { Kind = "Discovery", Message = $"Updated {p.DeviceName} address: {previous} → {p.Endpoint}." });
                });
                PeerAddressChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch { }
        }
    }

    public void Dispose()
    {
        try { _udp?.Dispose(); } catch { }
        _udp = null;
    }

    private sealed record DiscoveryPacket(int Version, Guid DeviceId, string DeviceName, int ListenPort, string CertificateThumbprint);
}
