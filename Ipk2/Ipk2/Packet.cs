using System.Buffers.Binary;
using Crc32 = System.IO.Hashing.Crc32;

namespace Ipk2;

public enum PacketFlags :  byte
{
    HEY = 0x01, // start connection with server
    HEY_YES = 0x03, // server response to client which wants to connect
    SEE_YOU = 0x04, // client/server wants to end up communication
    DATA = 0x08, // data transfer
    ACK = 0x09,
    RST = 0x10
}

public enum PacketSetUpStage
{
    SET_HEY, // Client starting communication
    SET_HEY_YES, // Server answer onto starting communication
    SET_ACK, // server: delivery confirmation with ackNum, client: confirmation of communication with the server
    SET_DATA, // data send from client
    SET_SEE_YOU
}
public class Packet
{
    private static uint _seqNum;
    private static uint _ackNum;
    private static byte _connId;

    public const int HeaderSize = 16;
    public const int MaxPayloadSize = 1200 - HeaderSize;

    private const int SeqOffSet = 0;
    private const int AckOffSet = 4;
    private const int FlagOffSet = 8;
    private const int LenOffset = 9;
    private const int ConnIdOffset = 11;
    private const int CrcOffset = 12;
    public const int PacketHeaderLength = 16;
    
    
    /// <summary>
    /// By set stage sets up packet type with appropriate Packet flag, sequence and acknowledgment numbers.
    /// For example, when stage is SET_DATA, it prepares packet with DATA flag and increases sequence
    /// number by data length for next packet.
    /// </summary>
    /// <param name="stage"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    public static byte[] SetUpPacket(PacketSetUpStage stage, byte[] data)
    {
        switch (stage)
        {
            case PacketSetUpStage.SET_HEY:
                _seqNum = 0;
                _ackNum = 0;
                return PreparePacket(PacketFlags.HEY, _seqNum, _ackNum, data);

            case PacketSetUpStage.SET_HEY_YES:
                return PreparePacket(PacketFlags.HEY_YES, _seqNum, _ackNum, data);

            case PacketSetUpStage.SET_ACK:
                return PreparePacket(PacketFlags.ACK, _seqNum, _ackNum, data);

            case PacketSetUpStage.SET_DATA:
                var seq = _seqNum;
                _seqNum += (uint)data.Length; // increase _seqNum for next packet
                return PreparePacket(PacketFlags.DATA, seq, _ackNum, data);

            case PacketSetUpStage.SET_SEE_YOU:
                return PreparePacket(PacketFlags.SEE_YOU, _seqNum, _ackNum, data);
        }

        return [];
    }

    public static void UpdateAckNum(byte[] receivedPacket)
    {
        uint seq = ReadSeqNum(receivedPacket);
        uint dataLen = BinaryPrimitives.ReadUInt16BigEndian(receivedPacket.AsSpan(LenOffset));
        _ackNum = seq + dataLen;
    }
    
    public static void SetConnectionId(byte id)
    {
        _connId = id;
    }
    public static byte ReadConnectionId(byte[] packet) => packet[ConnIdOffset];
    public static bool ValidateConnectionId(byte[] packet) => packet[ConnIdOffset] == _connId;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="flags"></param>
    /// <param name="data"></param>
    /// <param name="seq"></param>
    /// <param name="ack"></param>
    public static byte[] PreparePacket(PacketFlags flags, uint seq, uint ack, byte[] data)
    {
        byte[] packet = new byte[HeaderSize + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(SeqOffSet), seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(AckOffSet), ack);
        packet[FlagOffSet] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(LenOffset), (ushort)data.Length);
        packet[ConnIdOffset] = _connId;
        data.CopyTo(packet.AsSpan(PacketHeaderLength));

        WriteCrc(packet);
        return packet;
    }
    /// <summary>
    /// Reads acknowledgment number from packet header and returns
    /// </summary>
    public static uint ReadAckNum(byte[] packet) =>
        BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(AckOffSet));
    
    /// <summary>
    /// Reads sequence number from packet header and returns
    /// </summary>
    public static uint ReadSeqNum(byte[] packet) =>
        BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(SeqOffSet));
    
    /// <summary>
    /// Reads data length from packet and returns
    /// </summary>
    public static ushort ReadDataLength(byte[] packet) =>
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(LenOffset));

    

    private static void WriteCrc(byte[] packet)
    {
        uint crc = Crc32.HashToUInt32(packet);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(CrcOffset), crc);
    }
    
    /// <summary>
    ///  Checks if arrived packet cyclic redundancy check is same for actual calculation
    /// </summary>
    /// <param name="arrivedPacket"></param>
    /// <returns></returns>
    public static bool CheckCrc(byte[] arrivedPacket)
    {
        if (arrivedPacket.Length < HeaderSize)
            return false;
        
        var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(arrivedPacket.AsSpan(CrcOffset));
        // zero out crc for calculation
        BinaryPrimitives.WriteUInt32BigEndian(arrivedPacket.AsSpan(CrcOffset), 0);
        
        var calculatedCrc = Crc32.HashToUInt32(arrivedPacket);
        return calculatedCrc == storedCrc;
    }
}
