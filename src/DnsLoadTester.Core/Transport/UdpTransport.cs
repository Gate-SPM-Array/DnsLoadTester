using System.Buffers.Binary;
using System.Net.Sockets;

namespace DnsLoadTester.Core.Transport;

/// <summary>DNS su UDP: un socket "connesso" per worker. Le risposte con ID diverso (tardive) vengono scartate.</summary>
public sealed class UdpTransport(TransportContext ctx) : DnsTransport
{
    private Socket? _socket;

    private Socket GetSocket()
    {
        if (_socket != null) return _socket;
        var s = new Socket(ctx.EndPoint!.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try { s.ReceiveBufferSize = 256 * 1024; } catch { /* non critico */ }
        s.Connect(ctx.EndPoint);
        _socket = s;
        return s;
    }

    public override async ValueTask<int> ExchangeAsync(ReadOnlyMemory<byte> request, ushort id, Memory<byte> response, CancellationToken ct)
    {
        var s = GetSocket();
        try
        {
            await s.SendAsync(request, SocketFlags.None, ct).ConfigureAwait(false);
            while (true)
            {
                int n = await s.ReceiveAsync(response, SocketFlags.None, ct).ConfigureAwait(false);
                if (n >= 2 && BinaryPrimitives.ReadUInt16BigEndian(response.Span) == id) return n;
                Interlocked.Increment(ref StrayResponses);
            }
        }
        catch (SocketException ex)
        {
            Reset();
            string msg = ex.SocketErrorCode == SocketError.ConnectionReset
                ? "ICMP port unreachable (ConnectionReset)"
                : ex.SocketErrorCode.ToString();
            throw new DnsTransportException(TransportErrorKind.Network, msg, ex);
        }
    }

    private void Reset()
    {
        try { _socket?.Dispose(); } catch { }
        _socket = null;
    }

    public override ValueTask DisposeAsync() { Reset(); return ValueTask.CompletedTask; }
}
