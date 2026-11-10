using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalCast.Core.Protocol;

namespace LocalCast.Core.Discovery;

public class MdnsDiscoveryService : IDiscoveryService
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private const int MulticastPort = 5353;
    private const int BroadcastPort = 8766;
    private const int DefaultTvPort = 8765;
    private const string TargetService = "_localcast._tcp.local";

    private readonly ConcurrentDictionary<string, DiscoveredTv> _tvs = new();
    private readonly List<UdpClient> _clients = new();
    private UdpClient? _broadcastListener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _broadcastTask;
    private Task? _pruneTask;

    public event Action<DiscoveredTv>? TvDiscovered;
    public event Action<string>? TvLost;

    public IReadOnlyCollection<DiscoveredTv> DiscoveredTvs => _tvs.Values.ToList();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        InitializeSockets();

        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
        _broadcastTask = Task.Run(() => ListenBroadcastLoopAsync(_cts.Token), _cts.Token);
        _pruneTask = Task.Run(() => PruneLoopAsync(_cts.Token), _cts.Token);

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        // 1. Send mDNS Queries across all interfaces with QU unicast response bit
        var query = CreateMdnsQuery(TargetService);
        var endpoint = new IPEndPoint(MulticastAddress, MulticastPort);

        foreach (var client in _clients)
        {
            try
            {
                await client.SendAsync(query, query.Length, endpoint);
            }
            catch {}
        }

        // 2. Fast probe local subnets on port 8765
        _ = Task.Run(() => ProbeSubnetAsync(DefaultTvPort, _cts?.Token ?? CancellationToken.None));
    }

    public void AddManualTv(string ipAddress, int port = DefaultTvPort, string name = "TV Manual")
    {
        var id = $"{ipAddress}:{port}";
        var tv = new DiscoveredTv(id, name, ipAddress, port, TvState.Available, DateTime.UtcNow);
        _tvs[id] = tv;
        TvDiscovered?.Invoke(tv);
    }

    private void InitializeSockets()
    {
        _clients.Clear();

        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        foreach (var ni in interfaces)
        {
            var ipProps = ni.GetIPProperties();
            foreach (var unicast in ipProps.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                try
                {
                    var client = new UdpClient(AddressFamily.InterNetwork);
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    client.Client.Bind(new IPEndPoint(unicast.Address, 0));
                    client.JoinMulticastGroup(MulticastAddress, unicast.Address);
                    client.MulticastLoopback = true;
                    _clients.Add(client);
                }
                catch {}
            }
        }

        // Dedicated UDP Broadcast listener on port 8766
        try
        {
            _broadcastListener = new UdpClient();
            _broadcastListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _broadcastListener.Client.Bind(new IPEndPoint(IPAddress.Any, BroadcastPort));
        }
        catch {}
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        var tasks = _clients.Select(c => ListenOnClientAsync(c, token)).ToList();
        await Task.WhenAll(tasks);
    }

    private async Task ListenOnClientAsync(UdpClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(token);
                ParseMdnsResponse(result.Buffer, result.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(100, token);
            }
        }
    }

    private async Task ListenBroadcastLoopAsync(CancellationToken token)
    {
        if (_broadcastListener == null) return;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _broadcastListener.ReceiveAsync(token);
                var json = Encoding.UTF8.GetString(result.Buffer);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "LocalCast TV" : "LocalCast TV";
                int port = root.TryGetProperty("port", out var p) ? p.GetInt32() : DefaultTvPort;
                string stateStr = root.TryGetProperty("state", out var s) ? s.GetString() ?? "available" : "available";

                var state = stateStr.Equals("busy", StringComparison.OrdinalIgnoreCase) ? TvState.Busy : TvState.Available;
                string ip = result.RemoteEndPoint.Address.ToString();
                string id = $"{ip}:{port}";

                var tv = new DiscoveredTv(id, name, ip, port, state, DateTime.UtcNow);
                _tvs[id] = tv;
                TvDiscovered?.Invoke(tv);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(200, token);
            }
        }
    }

    private async Task ProbeSubnetAsync(int port, CancellationToken token)
    {
        var activeSubnets = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
            .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(ua => ua.Address)
            .ToList();

        foreach (var localIp in activeSubnets)
        {
            var bytes = localIp.GetAddressBytes();
            if (bytes[0] == 127) continue;

            string subnetPrefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.";

            var ips = Enumerable.Range(1, 254).Select(i => $"{subnetPrefix}{i}").ToList();

            await Parallel.ForEachAsync(ips, new ParallelOptions { MaxDegreeOfParallelism = 48, CancellationToken = token }, async (ip, ct) =>
            {
                if (ip == localIp.ToString()) return;

                try
                {
                    using var tcp = new TcpClient();
                    var connectTask = tcp.ConnectAsync(ip, port, ct).AsTask();
                    if (await Task.WhenAny(connectTask, Task.Delay(350, ct)) == connectTask && tcp.Connected)
                    {
                        var id = $"{ip}:{port}";
                        var tv = new DiscoveredTv(id, $"LocalCast TV ({ip})", ip, port, TvState.Available, DateTime.UtcNow);
                        _tvs[id] = tv;
                        TvDiscovered?.Invoke(tv);
                    }
                }
                catch {}
            });
        }
    }

    private void ParseMdnsResponse(byte[] data, IPAddress remoteIp)
    {
        if (data.Length < 12) return;

        var payload = Encoding.ASCII.GetString(data);
        if (!payload.Contains("_localcast")) return;

        var txtAttributes = ExtractTxtAttributes(data);

        string name = txtAttributes.GetValueOrDefault("name", $"LocalCast TV ({remoteIp})");
        string stateStr = txtAttributes.GetValueOrDefault("state", "available");
        string portStr = txtAttributes.GetValueOrDefault("port", DefaultTvPort.ToString());

        if (!int.TryParse(portStr, out int port)) port = DefaultTvPort;

        var state = stateStr.Equals("busy", StringComparison.OrdinalIgnoreCase)
            ? TvState.Busy
            : TvState.Available;

        string ip = remoteIp.ToString();
        string id = $"{ip}:{port}";

        var tv = new DiscoveredTv(id, name, ip, port, state, DateTime.UtcNow);
        _tvs[id] = tv;
        TvDiscovered?.Invoke(tv);
    }

    private static Dictionary<string, string> ExtractTxtAttributes(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 12;

        while (i < data.Length - 4)
        {
            if (data[i] == (byte)'=')
            {
                int keyStart = i - 1;
                while (keyStart >= 0 && data[keyStart] >= 0x20 && data[keyStart] <= 0x7E && (i - keyStart) < 32)
                {
                    keyStart--;
                }
                keyStart++;

                int valEnd = i + 1;
                while (valEnd < data.Length && data[valEnd] >= 0x20 && data[valEnd] <= 0x7E && (valEnd - i) < 64)
                {
                    valEnd++;
                }

                if (i > keyStart && valEnd > (i + 1))
                {
                    string key = Encoding.UTF8.GetString(data, keyStart, i - keyStart);
                    string val = Encoding.UTF8.GetString(data, i + 1, valEnd - (i + 1));
                    if (!string.IsNullOrWhiteSpace(key) && !result.ContainsKey(key))
                    {
                        result[key] = val;
                    }
                }
                i = valEnd;
            }
            else
            {
                i++;
            }
        }

        return result;
    }

    private static byte[] CreateMdnsQuery(string serviceName)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Header: ID=0, Flags=0, QDCOUNT=1, ANCOUNT=0, NSCOUNT=0, ARCOUNT=0
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((byte)0); writer.Write((byte)1); // QDCOUNT = 1
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);

        // QNAME
        foreach (var part in serviceName.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(part);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }
        writer.Write((byte)0);

        // QTYPE = PTR (12), QCLASS = IN (0x0001) with QU unicast response bit (0x8000) -> 0x8001
        writer.Write((byte)0); writer.Write((byte)12);
        writer.Write((byte)0x80); writer.Write((byte)1);

        return ms.ToArray();
    }

    private async Task PruneLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, token);
                var now = DateTime.UtcNow;

                foreach (var (id, tv) in _tvs)
                {
                    if (now - tv.LastSeen > TimeSpan.FromSeconds(45))
                    {
                        if (_tvs.TryRemove(id, out _))
                        {
                            TvLost?.Invoke(id);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch {}
        }
    }

    public Task StopAsync()
    {
        _cts?.Cancel();

        foreach (var client in _clients)
        {
            try
            {
                client.Close();
                client.Dispose();
            }
            catch {}
        }
        _clients.Clear();

        try
        {
            _broadcastListener?.Close();
            _broadcastListener?.Dispose();
            _broadcastListener = null;
        }
        catch {}

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
    }
}
