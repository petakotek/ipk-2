namespace Ipk2.Tests;

public class PacketTests
{
    public PacketTests()
    {
        // reset static _seqNum/_ackNum before every test
        Packet.SetUpPacket(PacketSetUpStage.SET_HEY, []);
    }

    [Fact]
    public void PreparePacket_SeqAckFlagLen_AreCorrect()
    {
        byte[] data = [1, 2, 3, 4, 5];
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 42, 99, data);

        Assert.Equal(42u, Packet.ReadSeqNum(pkt));
        Assert.Equal(99u, Packet.ReadAckNum(pkt));
        Assert.Equal(5, Packet.ReadDataLength(pkt));
        Assert.Equal((byte)PacketFlags.DATA, pkt[8]);
    }

    [Fact]
    public void PreparePacket_EmptyPayload_LengthIsHeaderSize()
    {
        var pkt = Packet.PreparePacket(PacketFlags.ACK, 0, 0, []);
        Assert.Equal(Packet.HeaderSize, pkt.Length);
    }

    [Fact]
    public void PreparePacket_Payload_WrittenAtPayloadOffset()
    {
        byte[] data = [0xDE, 0xAD, 0xBE, 0xEF];
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 0, 0, data);
        Assert.Equal(data, pkt[Packet.PacketHeaderLength..(Packet.PacketHeaderLength + data.Length)]);
    }

    // --- CRC ---

    [Fact]
    public void CheckCrc_FreshPacket_ReturnsTrue()
    {
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 1, 2, [10, 20, 30]);
        Assert.True(Packet.CheckCrc(pkt));
    }

    [Fact]
    public void CheckCrc_TamperedPayload_ReturnsFalse()
    {
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 1, 2, [10, 20, 30]);
        pkt[Packet.PacketHeaderLength] ^= 0xFF;
        Assert.False(Packet.CheckCrc(pkt));
    }

    [Fact]
    public void CheckCrc_TamperedHeader_ReturnsFalse()
    {
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 1, 2, [10, 20, 30]);
        pkt[0] ^= 0x01; // flip a bit in seq field
        Assert.False(Packet.CheckCrc(pkt));
    }

    [Fact]
    public void CheckCrc_TooShortPacket_ReturnsFalse()
    {
        Assert.False(Packet.CheckCrc(new byte[5]));
    }
    
    // --- SetUpPacket state machine ---

    [Fact]
    public void SetUpPacket_Hey_SeqAndAckAreZero()
    {
        var pkt = Packet.SetUpPacket(PacketSetUpStage.SET_HEY, []);
        Assert.Equal(0u, Packet.ReadSeqNum(pkt));
        Assert.Equal(0u, Packet.ReadAckNum(pkt));
        Assert.Equal((byte)PacketFlags.HEY, pkt[8]);
    }

    [Fact]
    public void SetUpPacket_Data_SeqAdvancesByPayloadLength()
    {
        var pkt1 = Packet.SetUpPacket(PacketSetUpStage.SET_DATA, new byte[100]);
        var pkt2 = Packet.SetUpPacket(PacketSetUpStage.SET_DATA, new byte[200]);
        var pkt3 = Packet.SetUpPacket(PacketSetUpStage.SET_DATA, new byte[50]);

        Assert.Equal(0u,   Packet.ReadSeqNum(pkt1));
        Assert.Equal(100u, Packet.ReadSeqNum(pkt2));
        Assert.Equal(300u, Packet.ReadSeqNum(pkt3));
    }

    // --- UpdateAckNum ---

    [Fact]
    public void UpdateAckNum_NextAckCarriesSeqPlusLen()
    {
        var incoming = Packet.PreparePacket(PacketFlags.DATA, 500, 0, new byte[100]);
        Packet.UpdateAckNum(incoming);

        var ack = Packet.SetUpPacket(PacketSetUpStage.SET_ACK, []);
        Assert.Equal(600u, Packet.ReadAckNum(ack));
    }

    [Fact]
    public void UpdateAckNum_CalledTwice_UsesLastValue()
    {
        var pkt1 = Packet.PreparePacket(PacketFlags.DATA, 0,   0, new byte[200]);
        var pkt2 = Packet.PreparePacket(PacketFlags.DATA, 200, 0, new byte[300]);
        Packet.UpdateAckNum(pkt1);
        Packet.UpdateAckNum(pkt2);

        var ack = Packet.SetUpPacket(PacketSetUpStage.SET_ACK, []);
        Assert.Equal(500u, Packet.ReadAckNum(ack));
    }

    // --- Constants ---

    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal(16,   Packet.HeaderSize);
        Assert.Equal(16,   Packet.PacketHeaderLength);
        Assert.Equal(1184, Packet.MaxPayloadSize);
    }

    [Fact]
    public void SetConnectionId_ReadBack_ReturnsSetId()
    {
        Packet.SetConnectionId(42);
        var pkt = Packet.PreparePacket(PacketFlags.HEY, 0, 0, []);
        Assert.Equal((byte)42, Packet.ReadConnectionId(pkt));
    }

    [Fact]
    public void ValidateConnectionId_MatchingId_ReturnsTrue()
    {
        Packet.SetConnectionId(77);
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 0, 0, [1, 2]);
        Assert.True(Packet.ValidateConnectionId(pkt));
    }

    [Fact]
    public void ValidateConnectionId_MismatchedId_ReturnsFalse()
    {
        Packet.SetConnectionId(10);
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 0, 0, [1, 2]);
        Packet.SetConnectionId(11);
        Assert.False(Packet.ValidateConnectionId(pkt));
    }

    [Fact]
    public void SetConnectionId_ZeroAndMax_RoundTrip()
    {
        Packet.SetConnectionId(0);
        var pkt0 = Packet.PreparePacket(PacketFlags.HEY, 0, 0, []);
        Assert.Equal((byte)0, Packet.ReadConnectionId(pkt0));

        Packet.SetConnectionId(255);
        var pkt255 = Packet.PreparePacket(PacketFlags.HEY, 0, 0, []);
        Assert.Equal((byte)255, Packet.ReadConnectionId(pkt255));
    }
    
    [Fact]
    public void SetUpPacket_HeyYes_HasCorrectFlag()
    {
        var pkt = Packet.SetUpPacket(PacketSetUpStage.SET_HEY_YES, []);
        Assert.Equal((byte)PacketFlags.HEY_YES, pkt[8]);
    }

    [Fact]
    public void SetUpPacket_Ack_HasCorrectFlag()
    {
        var pkt = Packet.SetUpPacket(PacketSetUpStage.SET_ACK, []);
        Assert.Equal((byte)PacketFlags.ACK, pkt[8]);
    }

    [Fact]
    public void SetUpPacket_SeeYou_HasCorrectFlag()
    {
        var pkt = Packet.SetUpPacket(PacketSetUpStage.SET_SEE_YOU, []);
        Assert.Equal((byte)PacketFlags.SEE_YOU, pkt[8]);
    }

    [Fact]
    public void SetUpPacket_Data_HasCorrectFlag()
    {
        var pkt = Packet.SetUpPacket(PacketSetUpStage.SET_DATA, [0xAB]);
        Assert.Equal((byte)PacketFlags.DATA, pkt[8]);
    }

    [Fact]
    public void UpdateAckNum_ZeroLengthData_AckEqualsSeq()
    {
        var incoming = Packet.PreparePacket(PacketFlags.ACK, 100, 0, []);
        Packet.UpdateAckNum(incoming);
        var ack = Packet.SetUpPacket(PacketSetUpStage.SET_ACK, []);
        Assert.Equal(100u, Packet.ReadAckNum(ack));
    }

    [Fact]
    public void UpdateAckNum_LargeSeq_CorrectResult()
    {
        var incoming = Packet.PreparePacket(PacketFlags.DATA, 0xFFF0u, 0, new byte[16]);
        Packet.UpdateAckNum(incoming);
        var ack = Packet.SetUpPacket(PacketSetUpStage.SET_ACK, []);
        Assert.Equal(0xFFF0u + 16u, Packet.ReadAckNum(ack));
    }

    [Fact]
    public void CheckCrc_ZeroesCrcField_SecondCallReturnsFalse()
    {
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 1, 2, [10, 20, 30]);
        Assert.True(Packet.CheckCrc(pkt));
        // CRC field is zeroed after the first check, a second call will fail
        Assert.False(Packet.CheckCrc(pkt));
    }

    [Fact]
    public void ReadDataLength_MatchesActualPayloadLength()
    {
        byte[] data = new byte[500];
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 0, 0, data);
        Assert.Equal((ushort)500, Packet.ReadDataLength(pkt));
    }

    [Fact]
    public void ReadDataLength_EmptyPayload_ReturnsZero()
    {
        var pkt = Packet.PreparePacket(PacketFlags.ACK, 0, 0, []);
        Assert.Equal((ushort)0, Packet.ReadDataLength(pkt));
    }
    

    [Fact]
    public void PreparePacket_MaxPayload_TotalLengthIsHeaderPlusPayload()
    {
        byte[] data = new byte[Packet.MaxPayloadSize];
        var pkt = Packet.PreparePacket(PacketFlags.DATA, 0, 0, data);
        Assert.Equal(Packet.HeaderSize + Packet.MaxPayloadSize, pkt.Length);
    }
}