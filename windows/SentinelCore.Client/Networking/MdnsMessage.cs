using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace SentinelCore.Client.Networking;

/// <summary>
/// Just enough DNS wire format to browse for one service type over mDNS. Apple's NWBrowser and
/// Android's NsdManager both do this for their apps; Windows has no equivalent in .NET, so this
/// reads and writes the packets directly.
/// </summary>
/// <remarks>
/// Pure functions over byte arrays, kept apart from the socket work in <see cref="ServerDiscovery"/>
/// so that the parsing - which is where the bugs live, and which no amount of manual testing on one
/// network will cover - can be exercised against captured packets in unit tests.
/// </remarks>
internal static class MdnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeAaaa = 28;
    public const ushort TypeSrv = 33;

    public sealed record ResourceRecord(IReadOnlyList<string> Name, ushort Type, ReadOnlyMemory<byte> Data)
    {
        public string DisplayName => string.Join('.', Name);
    }

    /// <summary>A one-question query for the given service type, as a browsing client sends it.</summary>
    public static byte[] BuildQuery(string serviceType, ushort transactionId = 0)
    {
        var body = new List<byte>();
        // Header: id, flags (standard query), one question, no answers/authority/additional.
        body.AddRange(BigEndian(transactionId));
        body.AddRange(BigEndian(0x0000));
        body.AddRange(BigEndian(1));
        body.AddRange(BigEndian(0));
        body.AddRange(BigEndian(0));
        body.AddRange(BigEndian(0));

        body.AddRange(EncodeName(serviceType));
        body.AddRange(BigEndian(TypePtr));
        // QU bit clear: ask for a multicast answer, which is what lets one query populate every
        // client on the network rather than just this one.
        body.AddRange(BigEndian(0x0001));
        return body.ToArray();
    }

    /// <summary>
    /// Every answer, authority and additional record in a response. Questions are skipped: an mDNS
    /// responder echoes the question back and nothing here needs it.
    /// </summary>
    /// <returns>Whatever was readable. A malformed packet from something else on the network
    /// yields the records parsed before it went wrong, never an exception out of a socket loop.
    /// </returns>
    public static List<ResourceRecord> ParseRecords(byte[] packet)
    {
        var records = new List<ResourceRecord>();
        if (packet.Length < 12) return records;

        var questions = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
        var authority = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2));
        var additional = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10, 2));

        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(packet, ref offset);
            if (offset + 4 > packet.Length) return records;
            offset += 4; // qtype + qclass
        }

        var total = answers + authority + additional;
        for (var i = 0; i < total; i++)
        {
            if (offset >= packet.Length) break;
            var name = ReadName(packet, ref offset);
            if (offset + 10 > packet.Length) break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            offset += 8; // type + class + ttl
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            offset += 2;
            if (offset + length > packet.Length) break;
            records.Add(new ResourceRecord(name, type, packet.AsMemory(offset, length)));
            offset += length;
        }

        return records;
    }

    /// <summary>
    /// The instance name a PTR record points at, as its labels - ["Kitchen", "_sentinelcore",
    /// "_tcp", "local"]. Kept as labels rather than a joined string because a server named
    /// "10.0.0.4" is a legal instance name, and joining would make its dots indistinguishable from
    /// the separators.
    /// </summary>
    public static IReadOnlyList<string>? ReadPtrTarget(byte[] packet, ResourceRecord record)
    {
        if (record.Type != TypePtr) return null;
        var offset = OffsetOf(packet, record);
        return offset < 0 ? null : ReadName(packet, ref offset);
    }

    public sealed record ServiceRecord(ushort Port, IReadOnlyList<string> Target)
    {
        public string TargetName => string.Join('.', Target);
    }

    /// <summary>Port and target host from an SRV record; priority and weight are not used here.</summary>
    public static ServiceRecord? ReadSrv(byte[] packet, ResourceRecord record)
    {
        if (record.Type != TypeSrv || record.Data.Length < 7) return null;
        var offset = OffsetOf(packet, record);
        if (offset < 0 || offset + 6 > packet.Length) return null;
        var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 4, 2));
        var target = offset + 6;
        return new ServiceRecord(port, ReadName(packet, ref target));
    }

    /// <summary>
    /// The key=value pairs in a TXT record. The server publishes "scheme" here so a client knows
    /// whether to address it as http or https before it has ever connected.
    /// </summary>
    public static Dictionary<string, string> ReadTxt(ResourceRecord record)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (record.Type != TypeTxt) return entries;

        var data = record.Data.Span;
        var offset = 0;
        while (offset < data.Length)
        {
            var length = data[offset];
            offset++;
            if (length == 0 || offset + length > data.Length) break;
            var text = Encoding.UTF8.GetString(data.Slice(offset, length));
            offset += length;
            var separator = text.IndexOf('=');
            if (separator > 0)
            {
                entries[text[..separator]] = text[(separator + 1)..];
            }
        }

        return entries;
    }

    /// <summary>The IPv4 address in an A record, or null if this isn't one.</summary>
    public static IPAddress? ReadA(ResourceRecord record) =>
        record.Type == TypeA && record.Data.Length == 4 ? new IPAddress(record.Data.ToArray()) : null;

    private static int OffsetOf(byte[] packet, ResourceRecord record)
    {
        // ReadOnlyMemory over the original array keeps its offset, which is what a name pointer
        // inside RDATA has to be resolved against.
        return System.Runtime.InteropServices.MemoryMarshal.TryGetArray(record.Data, out var segment) &&
               ReferenceEquals(segment.Array, packet)
            ? segment.Offset
            : -1;
    }

    /// <summary>
    /// Reads a DNS name, following compression pointers. Advances <paramref name="offset"/> past
    /// the name *as encoded at that position* - two bytes for a pointer, however long the name it
    /// points at. Getting that wrong walks the rest of the packet off by the length of one name,
    /// which is why it is spelled out here and covered by tests.
    /// </summary>
    private static List<string> ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        // Where the caller should continue from. Set the moment the first pointer is followed,
        // since everything after that is being read from somewhere else in the packet.
        var resume = -1;
        // A pointer loop is the classic malformed-packet denial of service; cap the hops rather
        // than trusting the sender.
        var guard = 0;

        while (position < packet.Length && guard++ <= 128)
        {
            var length = packet[position];

            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length) break;
                var pointer = ((length & 0x3F) << 8) | packet[position + 1];
                if (resume < 0) resume = position + 2;
                if (pointer >= packet.Length) break;
                position = pointer;
                continue;
            }

            position++;
            if (position + length > packet.Length) break;
            labels.Add(Encoding.UTF8.GetString(packet, position, length));
            position += length;
        }

        offset = resume >= 0 ? resume : position;
        return labels;
    }

    private static IEnumerable<byte> EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }
        bytes.Add(0);
        return bytes;
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        return bytes;
    }
}
