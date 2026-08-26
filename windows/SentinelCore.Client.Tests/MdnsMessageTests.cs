using System.Buffers.Binary;
using System.Text;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

// The mDNS parser is the one piece of this client with no server to check it against and no
// exception when it goes wrong - a name pointer read one byte off walks the rest of the packet
// into nonsense and discovery just silently finds nothing. So the packets are built here by hand,
// compression pointers included, and parsed back.
public class MdnsMessageTests
{
    private const string ServiceType = "_sentinelcore._tcp.local";

    [Fact]
    public void BuildQuery_asks_for_the_service_type_by_PTR()
    {
        var query = MdnsMessage.BuildQuery(ServiceType);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0, 2)));   // transaction id
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2, 2)));   // standard query
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4, 2)));   // one question
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(6, 2)));   // no answers

        // The name, then QTYPE=PTR and QCLASS=IN.
        var expectedName = new List<byte>();
        foreach (var label in ServiceType.Split('.'))
        {
            expectedName.Add((byte)label.Length);
            expectedName.AddRange(Encoding.UTF8.GetBytes(label));
        }
        expectedName.Add(0);
        Assert.Equal(expectedName, query.Skip(12).Take(expectedName.Count).ToList());
        Assert.Equal(MdnsMessage.TypePtr, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(12 + expectedName.Count, 2)));
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(14 + expectedName.Count, 2)));
    }

    [Fact]
    public void A_full_announcement_resolves_to_a_host_port_and_scheme()
    {
        var packet = new MdnsPacketBuilder()
            .AddPtr(ServiceType, "Garage._sentinelcore._tcp.local")
            .AddSrv("Garage._sentinelcore._tcp.local", 3320, "garage-pc.local")
            .AddTxt("Garage._sentinelcore._tcp.local", "scheme=https", "path=/")
            .AddA("garage-pc.local", "192.168.1.50")
            .Build();

        var records = MdnsMessage.ParseRecords(packet);

        var ptr = Assert.Single(records, record => record.Type == MdnsMessage.TypePtr);
        Assert.Equal(ServiceType, ptr.DisplayName);
        var target = MdnsMessage.ReadPtrTarget(packet, ptr);
        Assert.NotNull(target);
        Assert.Equal("Garage._sentinelcore._tcp.local", string.Join('.', target!));
        Assert.Equal("Garage", target![0]);

        var srvRecord = Assert.Single(records, record => record.Type == MdnsMessage.TypeSrv);
        var srv = MdnsMessage.ReadSrv(packet, srvRecord);
        Assert.NotNull(srv);
        Assert.Equal(3320, srv!.Port);
        Assert.Equal("garage-pc.local", srv.TargetName);

        var txtRecord = Assert.Single(records, record => record.Type == MdnsMessage.TypeTxt);
        var txt = MdnsMessage.ReadTxt(txtRecord);
        Assert.Equal("https", txt["scheme"]);
        Assert.Equal("/", txt["path"]);

        var aRecord = Assert.Single(records, record => record.Type == MdnsMessage.TypeA);
        Assert.Equal("192.168.1.50", MdnsMessage.ReadA(aRecord)?.ToString());
    }

    [Fact]
    public void Records_after_a_compressed_name_still_parse()
    {
        // The bug this exists to catch: a name pointer is two bytes however long the name it points
        // at, and a reader that advances past the *expanded* name instead reads the next record's
        // header out of the middle of this one. Every real responder compresses, so getting this
        // wrong means discovery works against nothing at all.
        var packet = new MdnsPacketBuilder()
            .AddPtr(ServiceType, "Garage._sentinelcore._tcp.local")
            .AddSrvWithCompressedOwner(3320, "garage-pc.local")
            .AddA("garage-pc.local", "192.168.1.50")
            .Build();

        var records = MdnsMessage.ParseRecords(packet);

        Assert.Equal(3, records.Count);
        Assert.Equal(MdnsMessage.TypeSrv, records[1].Type);
        Assert.Equal("Garage._sentinelcore._tcp.local", records[1].DisplayName);
        Assert.Equal(3320, (int?)MdnsMessage.ReadSrv(packet, records[1])?.Port);

        // The record *after* the compressed one is where an off-by-name error shows up.
        Assert.Equal(MdnsMessage.TypeA, records[2].Type);
        Assert.Equal("192.168.1.50", MdnsMessage.ReadA(records[2])?.ToString());
    }

    [Fact]
    public void A_truncated_packet_yields_what_was_readable_rather_than_throwing()
    {
        var full = new MdnsPacketBuilder()
            .AddPtr(ServiceType, "Garage._sentinelcore._tcp.local")
            .AddSrv("Garage._sentinelcore._tcp.local", 3320, "garage-pc.local")
            .Build();
        var truncated = full.Take(full.Length - 12).ToArray();

        var records = MdnsMessage.ParseRecords(truncated);

        // The PTR survived; the half-present SRV was dropped. What matters is that nothing threw
        // out of a background socket loop.
        Assert.Single(records);
        Assert.Equal(MdnsMessage.TypePtr, records[0].Type);
    }

    [Fact]
    public void A_pointer_loop_terminates_instead_of_hanging()
    {
        // 0xC00C at offset 12 points at itself. A reader without a hop cap spins here forever,
        // which on a background thread is an app that never shuts down.
        var packet = new List<byte>();
        packet.AddRange(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 });
        packet.AddRange(new byte[] { 0xC0, 0x0C });                       // name: pointer to itself
        packet.AddRange(new byte[] { 0, 12, 0, 1, 0, 0, 0, 60, 0, 0 });    // PTR, IN, ttl, rdlength 0

        var records = MdnsMessage.ParseRecords(packet.ToArray());

        Assert.Single(records);
    }

    [Fact]
    public void Garbage_is_not_mistaken_for_a_message()
    {
        Assert.Empty(MdnsMessage.ParseRecords(new byte[] { 1, 2, 3 }));
        Assert.Empty(MdnsMessage.ParseRecords(Array.Empty<byte>()));
    }

    [Fact]
    public void A_TXT_record_without_a_scheme_reads_as_no_scheme()
    {
        var packet = new MdnsPacketBuilder()
            .AddTxt("Garage._sentinelcore._tcp.local", "path=/")
            .Build();
        var record = Assert.Single(MdnsMessage.ParseRecords(packet));

        var txt = MdnsMessage.ReadTxt(record);

        Assert.False(txt.ContainsKey("scheme"));
        Assert.Equal("/", txt["path"]);
    }

    /// <summary>Builds DNS response packets the way a real responder would, compression included.</summary>
    private sealed class MdnsPacketBuilder
    {
        private readonly List<byte> _records = new();
        private int _answers;
        private int _ptrNameOffset = -1;

        public MdnsPacketBuilder AddPtr(string owner, string target)
        {
            AppendName(owner);
            AppendHeader(MdnsMessage.TypePtr);
            var rdata = EncodeName(target);
            // Remember where the PTR's target name landed, so a later record can point at it.
            _ptrNameOffset = 12 + _records.Count + 2;
            AppendData(rdata);
            _answers++;
            return this;
        }

        public MdnsPacketBuilder AddSrv(string owner, ushort port, string target)
        {
            AppendName(owner);
            AppendHeader(MdnsMessage.TypeSrv);
            AppendData(SrvData(port, target));
            _answers++;
            return this;
        }

        /// <summary>An SRV whose owner name is a compression pointer at the earlier PTR target.</summary>
        public MdnsPacketBuilder AddSrvWithCompressedOwner(ushort port, string target)
        {
            if (_ptrNameOffset < 0) throw new InvalidOperationException("Add a PTR record first.");
            _records.Add((byte)(0xC0 | (_ptrNameOffset >> 8)));
            _records.Add((byte)(_ptrNameOffset & 0xFF));
            AppendHeader(MdnsMessage.TypeSrv);
            AppendData(SrvData(port, target));
            _answers++;
            return this;
        }

        public MdnsPacketBuilder AddTxt(string owner, params string[] entries)
        {
            AppendName(owner);
            AppendHeader(MdnsMessage.TypeTxt);
            var rdata = new List<byte>();
            foreach (var entry in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(entry);
                rdata.Add((byte)bytes.Length);
                rdata.AddRange(bytes);
            }
            AppendData(rdata);
            _answers++;
            return this;
        }

        public MdnsPacketBuilder AddA(string owner, string address)
        {
            AppendName(owner);
            AppendHeader(MdnsMessage.TypeA);
            AppendData(System.Net.IPAddress.Parse(address).GetAddressBytes());
            _answers++;
            return this;
        }

        public byte[] Build()
        {
            var packet = new List<byte>();
            packet.AddRange(new byte[] { 0, 0 });          // transaction id
            packet.AddRange(new byte[] { 0x84, 0x00 });    // authoritative response
            packet.AddRange(BigEndian(0));                 // questions
            packet.AddRange(BigEndian(_answers));          // answers
            packet.AddRange(BigEndian(0));                 // authority
            packet.AddRange(BigEndian(0));                 // additional
            packet.AddRange(_records);
            return packet.ToArray();
        }

        private static List<byte> SrvData(ushort port, string target)
        {
            var data = new List<byte>();
            data.AddRange(BigEndian(0));      // priority
            data.AddRange(BigEndian(0));      // weight
            data.AddRange(BigEndian(port));
            data.AddRange(EncodeName(target));
            return data;
        }

        private void AppendName(string name) => _records.AddRange(EncodeName(name));

        private void AppendHeader(ushort type)
        {
            _records.AddRange(BigEndian(type));
            _records.AddRange(BigEndian(1));                          // class IN
            _records.AddRange(new byte[] { 0, 0, 0x0E, 0x10 });        // ttl 3600
        }

        private void AppendData(IReadOnlyCollection<byte> data)
        {
            _records.AddRange(BigEndian(data.Count));
            _records.AddRange(data);
        }

        private static List<byte> EncodeName(string name)
        {
            var bytes = new List<byte>();
            foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                bytes.Add((byte)label.Length);
                bytes.AddRange(Encoding.UTF8.GetBytes(label));
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
}
