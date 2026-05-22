using static Ipk2.Program;

namespace Ipk2.Tests;

public class DataTests
{
    public DataTests()
    {
        Packet.SetUpPacket(PacketSetUpStage.SET_HEY, []);
    }

    [Fact]
    public void PreparePacketStruct_Constructor_SetsFields()
    {
        byte[] data = [1, 2, 3];
        var s = new PreparePacketStruct(100, data);
        Assert.Equal(100, s._packetSeqNumber);
        Assert.Same(data, s._packetData);
    }

    // --- ComputeNumberOfReqPackets ---

    [Fact]
    public void ComputeNumberOfReqPackets_ZeroBytes_ReturnsZero()
    {
        Assert.Equal(0, ComputeNumberOfReqPackets(0, 1184));
    }

    [Fact]
    public void ComputeNumberOfReqPackets_OneByte_ReturnsOne()
    {
        Assert.Equal(1, ComputeNumberOfReqPackets(1, 1184));
    }

    [Fact]
    public void ComputeNumberOfReqPackets_ExactFit_ReturnsOne()
    {
        Assert.Equal(1, ComputeNumberOfReqPackets(1184, 1184));
    }

    [Fact]
    public void ComputeNumberOfReqPackets_OneExtraByte_ReturnsTwo()
    {
        Assert.Equal(2, ComputeNumberOfReqPackets(1185, 1184));
    }

    [Fact]
    public void ComputeNumberOfReqPackets_TwoFullPackets_ReturnsTwo()
    {
        Assert.Equal(2, ComputeNumberOfReqPackets(2368, 1184));
    }

    [Fact]
    public void ComputeNumberOfReqPackets_TwoFullPlusOne_ReturnsThree()
    {
        Assert.Equal(3, ComputeNumberOfReqPackets(2369, 1184));
    }

    // --- PrepareDataForEachPacket ---

    [Fact]
    public async Task PrepareDataForEachPacket_SmallData_ReturnsSingleEntry()
    {
        byte[] data = [1, 2, 3];
        var result = await PrepareDataForEachPacket(3, 1, data);
        Assert.Single(result);
        Assert.Equal(0, result[0]._packetSeqNumber);
        Assert.Equal(data, result[0]._packetData);
    }

    [Fact]
    public async Task PrepareDataForEachPacket_TwoPackets_CorrectSeqNumbers()
    {
        int max = Packet.MaxPayloadSize;
        byte[] data = new byte[max + 10];
        var result = await PrepareDataForEachPacket(data.Length, 2, data);
        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0]._packetSeqNumber);
        Assert.Equal(max, result[1]._packetSeqNumber);
    }

    [Fact]
    public async Task PrepareDataForEachPacket_LastPacketCarriesRemainder()
    {
        int max = Packet.MaxPayloadSize;
        byte[] data = new byte[max + 7];
        var result = await PrepareDataForEachPacket(data.Length, 2, data);
        Assert.Equal(max, result[0]._packetData.Length);
        Assert.Equal(7, result[1]._packetData.Length);
    }

    [Fact]
    public async Task PrepareDataForEachPacket_DataContentMatches()
    {
        byte[] data = Enumerable.Range(0, 10).Select(b => (byte)b).ToArray();
        var result = await PrepareDataForEachPacket(10, 1, data);
        Assert.Equal(data, result[0]._packetData);
    }

    [Fact]
    public async Task PrepareDataForEachPacket_SecondSliceContentMatches()
    {
        int max = Packet.MaxPayloadSize;
        byte[] data = new byte[max + 3];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
        var result = await PrepareDataForEachPacket(data.Length, 2, data);
        Assert.Equal(data[max..], result[1]._packetData);
    }

    // --- AcceptSegment ---

    [Fact]
    public void AcceptSegment_InOrderPacket_ReturnsTrueAndAdvancesExpected()
    {
        byte[] payload = [1, 2, 3];
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 0, 0, payload);
        uint expectedSeq = 0;
        var received = new List<byte>();
        var ooo = new Dictionary<uint, byte[]>();

        bool ok = AcceptSegment(pkt, ref expectedSeq, received, ooo);

        Assert.True(ok);
        Assert.Equal(3u, expectedSeq);
        Assert.Equal(payload, received.ToArray());
    }

    [Fact]
    public void AcceptSegment_OutOfOrderPacket_ReturnsFalseAndBuffers()
    {
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 10, 0, [4, 5, 6]);
        uint expectedSeq = 0;
        var received = new List<byte>();
        var ooo = new Dictionary<uint, byte[]>();

        bool ok = AcceptSegment(pkt, ref expectedSeq, received, ooo);

        Assert.False(ok);
        Assert.Equal(0u, expectedSeq);
        Assert.Empty(received);
        Assert.True(ooo.ContainsKey(10u));
    }

    [Fact]
    public void AcceptSegment_OldDuplicate_ReturnsFalseAndIgnores()
    {
        uint expectedSeq = 0;
        var received = new List<byte>();
        var ooo = new Dictionary<uint, byte[]>();
        var packet = Packet.PreparePacket(PacketFlags.DATA, 0, 0, [1, 2, 3]);
        AcceptSegment(packet, ref expectedSeq, received, ooo);

        bool ok = AcceptSegment(packet, ref expectedSeq, received, ooo);

        Assert.False(ok);
        Assert.Equal(3u, expectedSeq);
        Assert.Equal(3, received.Count); // payload not added twice
    }

    [Fact]
    public void AcceptSegment_InOrderThenFlushesOutOfOrderBuffer()
    {
        var pkt1 = Packet.PreparePacket(PacketFlags.DATA, 0, 0, [1, 2, 3]);
        var pkt2 = Packet.PreparePacket(PacketFlags.DATA, 3, 0, [4, 5, 6]);
        uint expectedSeq = 0;
        var received = new List<byte>();
        var ooo = new Dictionary<uint, byte[]>();

        AcceptSegment(pkt2, ref expectedSeq, received, ooo); // buffered
        bool ok = AcceptSegment(pkt1, ref expectedSeq, received, ooo); // accepted + flush

        Assert.True(ok);
        Assert.Equal(6u, expectedSeq);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, received.ToArray());
        Assert.Empty(ooo);
    }

    [Fact]
    public void AcceptSegment_DuplicateOutOfOrder_NotBufferedTwice()
    {
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 100, 0, [7, 8, 9]);
        uint expectedSeq = 0;
        var received = new List<byte>();
        var ooo = new Dictionary<uint, byte[]>();

        AcceptSegment(pkt, ref expectedSeq, received, ooo);
        AcceptSegment(pkt, ref expectedSeq, received, ooo); // same packet again

        Assert.Single(ooo);
    }

    [Fact]
    public void AcceptSegment_ChainOfThreeOutOfOrder_FlushedInOrder()
    {
        var pkt1 = Packet.PreparePacket(PacketFlags.DATA, 0, 0, [1]);
        var pkt2 = Packet.PreparePacket(PacketFlags.DATA, 1, 0, [2]);
        var pkt3 = Packet.PreparePacket(PacketFlags.DATA, 2, 0, [3]);
        uint expectedSeq = 0;
        var received = new List<byte>();
        var ooo = new Dictionary<uint, byte[]>();

        AcceptSegment(pkt3, ref expectedSeq, received, ooo);
        AcceptSegment(pkt2, ref expectedSeq, received, ooo);
        AcceptSegment(pkt1, ref expectedSeq, received, ooo); // should flush all three

        Assert.Equal(3u, expectedSeq);
        Assert.Equal(new byte[] { 1, 2, 3 }, received.ToArray());
        Assert.Empty(ooo);
    }
}
