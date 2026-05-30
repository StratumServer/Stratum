using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Vintagestory.Server.Nimbus;

// PROXY protocol v2 reader. Consumes a v2 header from an accepted TCP socket BEFORE any
// game bytes are read. Returns the parsed real client endpoint, or null if the peer sent no
// header (treat as a direct connection).
//
// Only called for peers whose address matches a configured trusted-proxy CIDR. We don't
// silently fall back when an untrusted peer omits the header, but we DO fall back when a
// trusted peer omits it (so the same backend works for both proxied and direct admin probes
// from the proxy box).
//
// Spec: https://www.haproxy.org/download/2.8/doc/proxy-protocol.txt
internal static class ProxyProtocolV2Reader
{
    private static readonly byte[] Signature =
    {
        0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A
    };

    public sealed class Result
    {
        public IPAddress SourceAddress { get; init; } = IPAddress.None;
        public int SourcePort { get; init; }
        public IPAddress DestinationAddress { get; init; } = IPAddress.None;
        public int DestinationPort { get; init; }
    }

    // Returns parsed header on success, null when no header was present.
    // Throws on malformed/short reads so the caller can drop the connection.
    public static async Task<Result?> ReadAsync(Socket socket, int timeoutMs, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        var token = timeoutCts.Token;

        // Peek the first 16 bytes (signature + version/cmd + family + length). If the
        // signature doesn't match, leave the bytes in the socket buffer for the normal
        // game packet reader and return null.
        var peek = new byte[16];
        int peeked = await PeekExactAsync(socket, peek, token).ConfigureAwait(false);
        if (peeked < 16) return null;

        for (int i = 0; i < 12; i++)
            if (peek[i] != Signature[i]) return null;

        byte verCmd = peek[12];
        if ((verCmd & 0xF0) != 0x20) throw new InvalidDataException("PROXY v2: bad version");
        byte cmd = (byte)(verCmd & 0x0F);
        byte famXport = peek[13];
        int addrLen = BinaryPrimitives.ReadUInt16BigEndian(peek.AsSpan(14, 2));
        int totalLen = 16 + addrLen;

        var full = new byte[totalLen];
        await ReadExactAsync(socket, full, token).ConfigureAwait(false);

        // cmd 0 = LOCAL (health check), cmd 1 = PROXY. Anything else is invalid.
        if (cmd == 0) return null;
        if (cmd != 1) throw new InvalidDataException($"PROXY v2: unsupported command 0x{cmd:X}");

        // family 0x11 = TCP/IPv4 (12 bytes), 0x21 = TCP/IPv6 (36 bytes). UNIX and UDP variants
        // are not used by Nimbus.
        if (famXport == 0x11)
        {
            if (addrLen < 12) throw new InvalidDataException("PROXY v2: short v4 address block");
            var src = new IPAddress(full.AsSpan(16, 4).ToArray());
            var dst = new IPAddress(full.AsSpan(20, 4).ToArray());
            int sport = BinaryPrimitives.ReadUInt16BigEndian(full.AsSpan(24, 2));
            int dport = BinaryPrimitives.ReadUInt16BigEndian(full.AsSpan(26, 2));
            return new Result { SourceAddress = src, SourcePort = sport, DestinationAddress = dst, DestinationPort = dport };
        }
        if (famXport == 0x21)
        {
            if (addrLen < 36) throw new InvalidDataException("PROXY v2: short v6 address block");
            var src = new IPAddress(full.AsSpan(16, 16).ToArray());
            var dst = new IPAddress(full.AsSpan(32, 16).ToArray());
            int sport = BinaryPrimitives.ReadUInt16BigEndian(full.AsSpan(48, 2));
            int dport = BinaryPrimitives.ReadUInt16BigEndian(full.AsSpan(50, 2));
            return new Result { SourceAddress = src, SourcePort = sport, DestinationAddress = dst, DestinationPort = dport };
        }
        throw new InvalidDataException($"PROXY v2: unsupported family 0x{famXport:X}");
    }

    private static async Task<int> PeekExactAsync(Socket socket, byte[] buf, CancellationToken ct)
    {
        // MSG_PEEK doesn't consume bytes, so each iteration just re-peeks the buffered prefix
        // from offset 0 until either the kernel has enough buffered or the deadline elapses.
        int n = 0;
        for (int tries = 0; tries < 200; tries++)
        {
            try
            {
                n = socket.Receive(buf, 0, buf.Length, SocketFlags.Peek);
            }
            catch (SocketException) { return n; }
            if (n >= buf.Length) return n;
            if (n < 0) return 0;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
        return n;
    }

    private static async Task ReadExactAsync(Socket socket, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await socket.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("PROXY v2: short read consuming header");
            got += n;
        }
    }
}
