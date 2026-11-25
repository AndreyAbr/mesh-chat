using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MeshChat.Crypto;
using MeshChat.Protocol;

namespace MeshChat.Network;

public sealed class MeshNodeOptions
{
    public required string NodeId { get; init; }
    public int TcpPort { get; init; } = 0;
    public int DiscoveryPort { get; init; } = 50500;
    public string SharedSecret { get; init; } = "agatu-mesh-secret-2026";
    public TimeSpan BeaconInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PeerTtl { get; init; } = TimeSpan.FromSeconds(10);
    public int DefaultMessageTtl { get; init; } = 5;
    public IEnumerable<string>? AllowedPeers { get; init; }
    public bool EnableUdpBroadcast { get; init; } = true;
}

public sealed class MeshNode : IAsyncDisposable
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.42.99");

    private readonly MeshNodeOptions _options;
    private readonly MeshCrypto _crypto;
    private readonly PeerTable _peerTable;
    private readonly ConcurrentDictionary<Guid, DateTime> _seenMessages = new();
    private readonly ConcurrentDictionary<Guid, bool> _ackedMessages = new();
    private readonly ConcurrentQueue<DeliveredMessage> _inbox = new();
    private readonly ConcurrentQueue<string> _logs = new();

    private TcpListener? _tcpListener;
    private UdpClient? _udpReceiver;
    private UdpClient? _udpSender;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _backgroundTasks = [];
    private int _disposed;

    public event Action<string>? OnLog;
    public event Action<DeliveredMessage>? OnMessageDelivered;

    public string NodeId => _options.NodeId;
    public int ActualTcpPort { get; private set; }
    public PeerTable Peers => _peerTable;

    public MeshNode(MeshNodeOptions options)
    {
        _options = options;
        _crypto = new MeshCrypto(options.SharedSecret);
        _peerTable = new PeerTable(options.AllowedPeers);
    }

    public Task StartAsync(CancellationToken externalCt = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

        _tcpListener = new TcpListener(IPAddress.Any, _options.TcpPort);
        _tcpListener.Start();
        ActualTcpPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;

        _backgroundTasks.Add(Task.Run(() => RunTcpAcceptLoopAsync(_cts.Token)));

        if (_options.DiscoveryPort > 0)
        {
            StartUdpDiscovery(_cts.Token);
        }

        _backgroundTasks.Add(Task.Run(() => RunHousekeepingLoopAsync(_cts.Token)));

        Log($"[START] Узел '{NodeId}' запущен на TCP :{ActualTcpPort}, UDP discovery :{(_options.DiscoveryPort > 0 ? _options.DiscoveryPort : 0)}");
        return Task.CompletedTask;
    }

    private void StartUdpDiscovery(CancellationToken ct)
    {
        _udpReceiver = new UdpClient();
        _udpReceiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpReceiver.ExclusiveAddressUse = false;
        _udpReceiver.Client.Bind(new IPEndPoint(IPAddress.Any, _options.DiscoveryPort));
        try
        {
            _udpReceiver.JoinMulticastGroup(MulticastGroup);
        }
        catch
        {
            // Fallback to broadcast-only if multicast join is unavailable
        }

        _udpSender = new UdpClient
        {
            EnableBroadcast = true,
            MulticastLoopback = true
        };

        _backgroundTasks.Add(Task.Run(() => RunUdpBeaconLoopAsync(ct)));
        _backgroundTasks.Add(Task.Run(() => RunUdpListenLoopAsync(ct)));
    }

    private async Task RunUdpBeaconLoopAsync(CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new BeaconPacket(NodeId, ActualTcpPort, 1));
        var multicastEp = new IPEndPoint(MulticastGroup, _options.DiscoveryPort);
        var broadcastEp = new IPEndPoint(IPAddress.Broadcast, _options.DiscoveryPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_udpSender is not null)
                {
                    await _udpSender.SendAsync(payload, payload.Length, multicastEp);
                    if (_options.EnableUdpBroadcast)
                    {
                        try { await _udpSender.SendAsync(payload, payload.Length, broadcastEp); }
                        catch { /* Ignore restricted broadcast on isolated interfaces */ }
                    }
                }
                await Task.Delay(_options.BeaconInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"[UDP-ERR] Ошибка отправки маячка: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task RunUdpListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpReceiver is not null)
        {
            try
            {
                var res = await _udpReceiver.ReceiveAsync(ct);
                var beacon = JsonSerializer.Deserialize<BeaconPacket>(res.Buffer);
                if (beacon is null || string.IsNullOrWhiteSpace(beacon.NodeId) || beacon.NodeId == NodeId)
                    continue;

                var senderIp = res.RemoteEndPoint.Address.MapToIPv4().ToString();
                if (senderIp == "0.0.0.0") senderIp = "127.0.0.1";

                bool isNew = _peerTable.UpsertPeer(beacon.NodeId, senderIp, beacon.TcpPort, DateTime.UtcNow);
                if (isNew)
                    Log($"[PEER+] Обнаружен сосед '{beacon.NodeId}' ({senderIp}:{beacon.TcpPort})");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore malformed UDP datagrams
            }
        }
    }

    private async Task RunHousekeepingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var expired = _peerTable.PurgeExpired(_options.PeerTtl, now);
                foreach (var p in expired)
                    Log($"[PEER-] Узел '{p.NodeId}' удалён по таймауту TTL ({_options.PeerTtl.TotalSeconds:0}с)");

                var seenCutoff = now.AddSeconds(-60);
                foreach (var kv in _seenMessages)
                {
                    if (kv.Value < seenCutoff)
                        _seenMessages.TryRemove(kv.Key, out _);
                }

                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunTcpAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _tcpListener is not null)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleTcpClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var lenBuf = new byte[4];
                await stream.ReadExactlyAsync(lenBuf, ct);
                int payloadLen = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                if (payloadLen <= 0 || payloadLen > 1024 * 1024)
                    return;

                var encrypted = new byte[payloadLen];
                await stream.ReadExactlyAsync(encrypted, ct);

                var plainBytes = _crypto.Decrypt(encrypted);
                var envelope = JsonSerializer.Deserialize<MeshEnvelope>(plainBytes);
                if (envelope is null)
                    return;

                await ProcessIncomingEnvelopeAsync(envelope);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log($"[TCP-ERR] Ошибка обработки пакета (возможно неверный ключ или обрыв): {ex.Message}");
            }
        }
    }

    private async Task ProcessIncomingEnvelopeAsync(MeshEnvelope envelope)
    {
        if (!_seenMessages.TryAdd(envelope.MsgId, DateTime.UtcNow))
            return; // Duplicate message

        string prevHop = envelope.Path.Count > 0 ? envelope.Path[^1] : envelope.From;

        if (envelope.Type == "ACK")
        {
            if (envelope.To == NodeId)
            {
                _ackedMessages[envelope.MsgId] = true;
                Log($"[ACK] Подтверждена доставка сообщения от '{envelope.From}' (маршрут: {string.Join(" -> ", envelope.Path)} -> {NodeId})");
                return;
            }

            if (envelope.Ttl > 1)
                await ForwardEnvelopeAsync(envelope, prevHop);
            return;
        }

        bool isForMe = envelope.To == NodeId || envelope.To == "*";
        if (isForMe)
        {
            var fullPath = new List<string>(envelope.Path) { NodeId };
            var delivered = new DeliveredMessage(
                envelope.MsgId,
                envelope.Type,
                envelope.From,
                envelope.To,
                envelope.Text,
                envelope.TimestampMs,
                fullPath,
                DateTime.UtcNow);

            _inbox.Enqueue(delivered);
            while (_inbox.Count > 200) _inbox.TryDequeue(out _);

            OnMessageDelivered?.Invoke(delivered);
            Log($"[MSG<-] Получено от '{envelope.From}' (путь: {string.Join(" -> ", fullPath)}): {envelope.Text}");

            if (envelope.To == NodeId)
            {
                await SendAckAsync(envelope.From, envelope.MsgId);
            }
        }

        if ((envelope.To == "*" || envelope.To != NodeId) && envelope.Ttl > 1)
        {
            await ForwardEnvelopeAsync(envelope, prevHop);
        }
    }

    public async Task<Guid> SendDirectAsync(string targetNodeId, string text)
    {
        var envelope = new MeshEnvelope
        {
            MsgId = Guid.NewGuid(),
            Type = "CHAT",
            From = NodeId,
            To = targetNodeId,
            Text = text,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Ttl = _options.DefaultMessageTtl,
            Path = [NodeId]
        };

        _seenMessages.TryAdd(envelope.MsgId, DateTime.UtcNow);
        await DispatchEnvelopeAsync(envelope, excludePeerId: null);
        return envelope.MsgId;
    }

    public async Task<Guid> BroadcastAsync(string text)
    {
        var envelope = new MeshEnvelope
        {
            MsgId = Guid.NewGuid(),
            Type = "CHAT",
            From = NodeId,
            To = "*",
            Text = text,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Ttl = _options.DefaultMessageTtl,
            Path = [NodeId]
        };

        _seenMessages.TryAdd(envelope.MsgId, DateTime.UtcNow);
        await DispatchEnvelopeAsync(envelope, excludePeerId: null);
        return envelope.MsgId;
    }

    private async Task SendAckAsync(string originalSender, Guid originalMsgId)
    {
        var ack = new MeshEnvelope
        {
            MsgId = Guid.NewGuid(),
            Type = "ACK",
            From = NodeId,
            To = originalSender,
            Text = originalMsgId.ToString(),
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Ttl = _options.DefaultMessageTtl,
            Path = [NodeId]
        };
        _seenMessages.TryAdd(ack.MsgId, DateTime.UtcNow);
        await DispatchEnvelopeAsync(ack, excludePeerId: null);
    }

    private async Task ForwardEnvelopeAsync(MeshEnvelope incoming, string prevHop)
    {
        var forwarded = new MeshEnvelope
        {
            MsgId = incoming.MsgId,
            Type = incoming.Type,
            From = incoming.From,
            To = incoming.To,
            Text = incoming.Text,
            TimestampMs = incoming.TimestampMs,
            Ttl = incoming.Ttl - 1,
            Path = [.. incoming.Path, NodeId]
        };

        // Do NOT log plaintext when forwarding transit traffic!
        Log($"[FWD] Транзитная пересылка msgId={forwarded.MsgId:N} ({forwarded.From} -> {forwarded.To}, ttl={forwarded.Ttl})");
        await DispatchEnvelopeAsync(forwarded, excludePeerId: prevHop);
    }

    private async Task DispatchEnvelopeAsync(MeshEnvelope envelope, string? excludePeerId)
    {
        byte[] framed = EncodeFrame(envelope);

        if (envelope.To != "*" && _peerTable.TryGetPeer(envelope.To, out var directPeer) && directPeer is not null)
        {
            await SendFrameToPeerAsync(directPeer, framed);
            return;
        }

        var targets = _peerTable.GetActivePeers()
            .Where(p => p.NodeId != excludePeerId && p.NodeId != envelope.From && !envelope.Path.Contains(p.NodeId))
            .ToList();

        if (targets.Count == 0 && envelope.Path.Count <= 1)
        {
            Log($"[WARN] Нет известных соседей для доставки msgId={envelope.MsgId:N}");
            return;
        }

        await Task.WhenAll(targets.Select(p => SendFrameToPeerAsync(p, framed)));
    }

    public byte[] EncodeFrame(MeshEnvelope envelope)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var encrypted = _crypto.Encrypt(jsonBytes);
        var framed = new byte[4 + encrypted.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(0, 4), encrypted.Length);
        encrypted.CopyTo(framed.AsSpan(4));
        return framed;
    }

    private async Task SendFrameToPeerAsync(PeerInfo peer, byte[] framed)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(peer.Address, peer.TcpPort, cts.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(framed, cts.Token);
            await stream.FlushAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log($"[SEND-ERR] Не удалось отправить узлу '{peer.NodeId}' ({peer.Address}:{peer.TcpPort}): {ex.Message}");
        }
    }

    public List<DeliveredMessage> GetInbox() => _inbox.ToArray().ToList();
    public List<string> GetLogs() => _logs.ToArray().ToList();
    public int AckCount => _ackedMessages.Count;

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{NodeId}] {message}";
        _logs.Enqueue(line);
        while (_logs.Count > 300) _logs.TryDequeue(out _);
        OnLog?.Invoke(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
        try { _tcpListener?.Stop(); } catch { }
        try { _udpReceiver?.Close(); _udpReceiver?.Dispose(); } catch { }
        try { _udpSender?.Close(); _udpSender?.Dispose(); } catch { }

        try
        {
            await Task.WhenAll(_backgroundTasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown timeouts
        }
    }
}
