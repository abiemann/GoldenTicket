using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GoldenTicket.ConnectivitySpike.Networking;

/// <summary>
/// A minimal multicast-DNS responder: it answers A-record queries for one <c>.local</c> name with
/// one address, and announces that name when it starts.
///
/// DESIGN 18.5 prefers a unique installation hostname advertised over local mDNS, and requires the
/// implementation to be validated on every supported phone rather than assumed. That is exactly what
/// this exists to find out, which is why it is hand-rolled and deliberately small: no dependency to
/// license or audit, and nothing between the wire format and the observed device behaviour.
///
/// It answers only for its own name. It is not a general responder and does not serve PTR, SRV or
/// service discovery.
/// </summary>
public sealed class MulticastDnsResponder : IAsyncDisposable
{
    private const int MulticastPort = 5353;
    private const ushort TypeA = 1;
    private const ushort TypeAny = 255;
    private const ushort ClassIn = 1;

    /// <summary>Marks the record as authoritative for this name, flushing stale caches.</summary>
    private const ushort ClassInFlush = 0x8001;

    /// <summary>Two minutes: short enough that a stale address does not linger after a DHCP change.</summary>
    private const uint RecordTtlSeconds = 120;

    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");

    private readonly string _hostname;
    private readonly IPAddress _address;
    private readonly int _prefixLength;
    private readonly Func<bool> _networkIsPrivate;
    private readonly CancellationTokenSource _stopping = new();

    private Socket? _socket;
    private Task? _loop;
    private int _interfaceIndex;

    public MulticastDnsResponder(string hostname, IPAddress address, int prefixLength = 32,
        Func<bool>? networkIsPrivate = null)
    {
        _hostname = hostname.TrimEnd('.');
        _address = address;
        _prefixLength = prefixLength;
        _networkIsPrivate = networkIsPrivate ?? (() => true);
    }

    /// <summary>How many queries this responder has answered. Reported as spike evidence.</summary>
    public int AnsweredQueries { get; private set; }

    /// <summary>Null while the responder is working; otherwise why it could not start or run.</summary>
    public string? Problem { get; private set; }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>
    /// Starts listening. A failure here is a finding, not a crash: the host still works through its
    /// IP address, and DESIGN 18.5 requires that fallback to be offered and explained.
    /// </summary>
    public bool TryStart()
    {
        if (_socket is not null || _stopping.IsCancellationRequested) return false;
        try
        {
            var adapter = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Single(candidate => candidate.GetIPProperties().UnicastAddresses.Any(item => item.Address.Equals(_address)));
            _interfaceIndex = adapter.GetIPProperties().GetIPv4Properties().Index;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Windows already runs an mDNS responder for its own name; sharing the port lets this
            // one answer for the GoldenTicket name alongside it.
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            _socket.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));

            _socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(MulticastGroup, _address));

            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            // Joining an interface does not select it for outgoing multicast. Without this,
            // Windows may advertise the board's address on a different adapter/default route.
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, _address.GetAddressBytes());

            _loop = Task.Run(() => ListenAsync(_stopping.Token));
            _ = AnnounceAsync();
            return true;
        }
        catch (SocketException exception)
        {
            Problem = $"could not join the multicast group ({exception.SocketErrorCode})";
            _socket?.Dispose();
            _socket = null;
            return false;
        }
        catch (Exception exception)
        {
            Problem = $"could not start ({exception.GetType().Name})";
            _socket?.Dispose();
            _socket = null;
            return false;
        }
    }

    /// <summary>Sends an unsolicited answer so caches learn the name without being asked.</summary>
    public async Task AnnounceAsync()
    {
        if (_socket is null || !_networkIsPrivate()) return;

        try
        {
            var response = BuildResponse(queryId: 0);
            await _socket.SendToAsync(response, SocketFlags.None, new IPEndPoint(MulticastGroup, MulticastPort));
        }
        catch (Exception exception)
        {
            Problem ??= $"could not announce ({exception.GetType().Name})";
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested && _socket is not null)
        {
            try
            {
                var result = await _socket.ReceiveMessageFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);

                if (result.PacketInformation.Interface != _interfaceIndex ||
                    result.RemoteEndPoint is not IPEndPoint peer ||
                    !LanInterfaces.IsInSubnet(peer.Address, _address, _prefixLength)) continue;

                if (!AsksForThisName(buffer.AsSpan(0, result.ReceivedBytes), out var queryId)) continue;
                if (!_networkIsPrivate()) continue;

                var response = BuildResponse(queryId);
                await _socket.SendToAsync(
                    response, SocketFlags.None, new IPEndPoint(MulticastGroup, MulticastPort), cancellationToken);

                AnsweredQueries++;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException exception)
            {
                Problem ??= $"stopped listening ({exception.SocketErrorCode})";
                return;
            }
            catch (Exception exception)
            {
                // A malformed packet from the network must not take the responder down.
                Problem ??= $"ignored a bad packet ({exception.GetType().Name})";
            }
        }
    }

    // ---- Wire format ---------------------------------------------------------------------------

    /// <summary>True when the message is a query whose question matches this responder's name.</summary>
    internal bool AsksForThisName(ReadOnlySpan<byte> message, out ushort queryId)
    {
        queryId = 0;
        if (message.Length < 12) return false;

        queryId = BinaryPrimitives.ReadUInt16BigEndian(message);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);

        // Responses have QR set; only questions are answered.
        if ((flags & 0xF800) != 0) return false;

        var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var offset = 12;

        for (var index = 0; index < questions; index++)
        {
            if (!TryReadName(message, ref offset, out var name)) return false;
            if (offset + 4 > message.Length) return false;

            var type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            var queryClass = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 2)..]);
            offset += 4;

            // The top class bit is the unicast-response request, not part of the class.
            if ((queryClass & 0x7FFF) != ClassIn) continue;
            if (type != TypeA && type != TypeAny) continue;
            if (name.Equals(_hostname, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Reads a DNS name, following at most one compression pointer chain.</summary>
    private static bool TryReadName(ReadOnlySpan<byte> message, ref int offset, out string name)
    {
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var safety = 0;
        var wireLength = 1;

        while (cursor < message.Length)
        {
            if (++safety > 128) break;

            var length = message[cursor];

            if (length == 0)
            {
                cursor++;
                if (!jumped) offset = cursor;
                name = string.Join('.', labels);
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= message.Length) break;

                var pointer = ((length & 0x3F) << 8) | message[cursor + 1];
                if (!jumped) offset = cursor + 2;
                jumped = true;
                cursor = pointer;
                continue;
            }

            if (length > 63 || (wireLength += length + 1) > 255) break;
            if (cursor + 1 + length > message.Length) break;

            labels.Add(Encoding.UTF8.GetString(message.Slice(cursor + 1, length)));
            cursor += 1 + length;
        }

        name = string.Empty;
        return false;
    }

    /// <summary>Builds an authoritative answer carrying one A record for this name.</summary>
    internal byte[] BuildResponse(ushort queryId)
    {
        var nameBytes = EncodeName(_hostname);
        var addressBytes = _address.GetAddressBytes();

        var message = new byte[12 + nameBytes.Length + 10 + addressBytes.Length];
        var span = message.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span, queryId);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0x8400);   // response, authoritative
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 0);        // questions
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], 1);        // answers
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], 0);

        var offset = 12;
        nameBytes.CopyTo(span[offset..]);
        offset += nameBytes.Length;

        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 2)..], ClassInFlush);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 4)..], RecordTtlSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 8)..], (ushort)addressBytes.Length);
        offset += 10;

        addressBytes.CopyTo(span[offset..]);
        return message;
    }

    internal static byte[] EncodeName(string name)
    {
        var labels = name.TrimEnd('.').Split('.');
        var size = labels.Sum(label => 1 + Encoding.UTF8.GetByteCount(label)) + 1;
        var bytes = new byte[size];

        var offset = 0;
        foreach (var label in labels)
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            if (encoded.Length > 63) throw new ArgumentException($"Label '{label}' is too long.", nameof(name));

            bytes[offset++] = (byte)encoded.Length;
            encoded.CopyTo(bytes, offset);
            offset += encoded.Length;
        }

        bytes[offset] = 0;
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        _socket?.Close();
        _socket?.Dispose();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _stopping.Dispose();
    }
}
