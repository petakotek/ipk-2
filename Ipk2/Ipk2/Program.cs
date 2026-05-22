using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using static Ipk2.Server;
using static Ipk2.Client;
using static Ipk2.Packet;
using static Ipk2.PacketSetUpStage;
using static Ipk2.PacketFlags;
namespace Ipk2;

public static class Program
{
    private const int FlagOffset = 8;
    private const int RetransmitMs = 300;

    private enum ConnectionState
    {
        Listen,
        HeySent,
        Established,
        FinWait,
        Closed
    };
    
    /// <summary>
    /// Structure of prepared packet with data and seq number for send function
    /// </summary>
    /// <param name="packetSeqNumber"></param>
    /// <param name="packetData"></param>
    public struct PreparePacketStruct(int packetSeqNumber, byte[] packetData)
    {
        public byte[] _packetData = packetData;
        public int _packetSeqNumber = packetSeqNumber;
    }
    
    public static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Environment.Exit(1);
        }
        await CommandLineHandler(args);
    }

    private static async Task CommandLineHandler(string[] args)
    {
        RootCommand rootCommand = new("Reliable File Transfer over UDP");

        var startServer = new Option<bool>("-s")
        {
            Description = "Start the server and listen for incoming connections on the specified port.",
            Arity = ArgumentArity.Zero
        };

        var startClient = new Option<bool>("-c")
        {
            Description = "Start the client and start sending side of the application.",
            Arity = ArgumentArity.Zero
        };

        var udpPort = new Option<int>("-p")
        {
            Description = "Specific UDP port to use for communication. Must be provided.",
            Arity = ArgumentArity.ExactlyOne
        };

        var modeAddress = new Option<string?>("-a")
        {
            Description =
                "Server mode: specifies the local bind address. Client mode: specifies the destination hostname or IPv4/IPv6 address.",
            Arity = ArgumentArity.ExactlyOne
        };

        // If omitted or if INPUT is -, the client reads from stdin.
        var input = new Option<string?>("-i")
        {
            Description = "Input file to send.",
            Arity = ArgumentArity.ZeroOrOne
        };
        // If omitted or if OUTPUT is -, the server writes the received data to stdout.
        var output = new Option<string?>("-o")
        {
            Description = "Output file to create or overwrite.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var timeoutOption = new Option<int>("-w")
        {
            Description = "Timeout in seconds",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => 1
        };

        rootCommand.Options.Add(startServer);
        rootCommand.Options.Add(startClient);
        rootCommand.Options.Add(udpPort);
        rootCommand.Options.Add(modeAddress);
        rootCommand.Options.Add(input);
        rootCommand.Options.Add(output);
        rootCommand.Options.Add(timeoutOption);
        
        rootCommand.SetAction(async (parseResult, ct) =>
        {
            bool isServer = parseResult.GetValue(startServer);
            bool isClient = parseResult.GetValue(startClient);

            if (isServer == isClient)
            {
                await Console.Error.WriteLineAsync("Exactly one of -s or -c must be specified.");
                Environment.Exit(1);
            }

            var timeout = parseResult.GetValue(timeoutOption);
            
            if (timeout <= 0)
            {
                await Console.Error.WriteLineAsync("Timeout must be greater than zero.");
                Environment.Exit(1);
            }
            
            var config = new AppConfig
            {
                Port       = parseResult.GetValue(udpPort),
                Address    = parseResult.GetValue(modeAddress),
                Input      = parseResult.GetValue(input),
                Output     = parseResult.GetValue(output),
                TimeoutSec = parseResult.GetValue(timeoutOption),
            };

            if (isServer)
            {
                if (config.Port <= 0 || config.Port > 65535)
                {
                    await Console.Error.WriteLineAsync("Invalid port entered for server");
                    Environment.Exit(1);
                }
                await RunServer(config, ct);
            }
            
            else
            {
                if (config.Address == null)
                {
                    await Console.Error.WriteLineAsync("Client mode requires -a option to specify the server address.");
                    Environment.Exit(1);
                }
                if (config.Port <= 0 || config.Port > 65535)
                {
                    await Console.Error.WriteLineAsync("Invalid port entered for client");
                    Environment.Exit(1);
                }
                await RunClient(config, ct);
            }
            return 0;
        });

        await rootCommand.Parse(args).InvokeAsync();
    }
    
    private static async Task RunServer(AppConfig config, CancellationToken ct)
    {
        var serverState = ConnectionState.Listen;
        (Socket socket, AddressFamily family) = CreateServerSocket(config);

        // received data buffer
        byte[] buffer = new byte[1200];

        EndPoint anyEndpoint = new IPEndPoint(
           family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        IPEndPoint? client = null;

        List<byte> receivedData = new();
        uint expectedSeq = 0;
        bool connIdKnown = false;
        Dictionary<uint, byte[]> outOfOrder = new();
        var lastProgress = DateTime.UtcNow;

        while (ct.IsCancellationRequested == false)
        {
            SocketReceiveFromResult? result;
            using (var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                receiveCts.CancelAfter(TimeSpan.FromMilliseconds(RetransmitMs));
                try
                {
                    result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndpoint, receiveCts.Token);
                    if (!CheckCrc(buffer[..result.Value.ReceivedBytes])) continue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested == false)
                {
                    if ((DateTime.UtcNow - lastProgress).TotalSeconds >= config.TimeoutSec)
                    {
                        await Console.Error.WriteLineAsync("[server] No protocol progress, giving up.");
                        Environment.Exit(2);
                    }

                    if (serverState == ConnectionState.Listen)
                    {
                        lastProgress = DateTime.UtcNow;
                        continue;
                    }
                    
                    if (serverState == ConnectionState.HeySent && client != null)
                        await SetUpAndSendPacket(stg: SET_HEY_YES, data: [], socket, client, ct);
                    else if (serverState == ConnectionState.Closed && client != null)
                    {
                        await SetUpAndSendPacket(stg: SET_ACK, data: [], socket, client, ct);
                        await SetUpAndSendPacket(stg: SET_SEE_YOU, data: [], socket, client, ct);
                    }
                    continue;
                }
            }
            // Connection ID validation
            if (connIdKnown && !ValidateConnectionId(buffer)) continue;

            var flag = (PacketFlags)buffer[FlagOffset];
            if (flag == RST)
            {
                break;
            }

            IPEndPoint senderEndpoint = (IPEndPoint)result.Value.RemoteEndPoint;
            if (client == null)
            {
                client = new IPEndPoint(senderEndpoint.Address, senderEndpoint.Port);
            }

            if (!senderEndpoint.Equals(client))
            {
                continue; // message is from other client
            }
            
            switch (serverState)
            {
                // listening to client
                case ConnectionState.Listen:
                    if (flag != HEY) continue; // accept only HEY
                    SetConnectionId(ReadConnectionId(buffer));
                    connIdKnown = true;
                    lastProgress = DateTime.UtcNow;
                    await SetUpAndSendPacket(stg: SET_HEY_YES, data: [], socket, client, ct);
                    serverState = ConnectionState.HeySent;
                    break;
                // server already receive HEY and sent HEY_YES back
                case ConnectionState.HeySent:
                    if (flag == HEY) // client retransmitted, resend HEY_YES immediately
                    {
                        await SetUpAndSendPacket(SET_HEY_YES, [], socket, client, ct);
                        break;
                    }

                    if (flag == DATA) // DATA arrived instead of ACK
                    {
                        var received = buffer[..result.Value.ReceivedBytes];
                        if (AcceptSegment(received, ref expectedSeq, receivedData, outOfOrder))
                            lastProgress = DateTime.UtcNow;
                        byte[] responsePacket = PreparePacket(ACK, 0, ack: expectedSeq, []);
                        await socket.SendToAsync(responsePacket, SocketFlags.None, client, ct);
                        serverState = ConnectionState.Established;
                        break;
                    }
                    if (flag != ACK) continue;
                    lastProgress = DateTime.UtcNow;
                    serverState = ConnectionState.Established;
                    break;

                case ConnectionState.Established:
                    if (flag == DATA)
                    {
                        var received = buffer[..result.Value.ReceivedBytes];
                        if (AcceptSegment(received, ref expectedSeq, receivedData, outOfOrder))
                            lastProgress = DateTime.UtcNow;
                        byte[] responsePacket = PreparePacket(ACK, 0, ack: expectedSeq, []);
                        await socket.SendToAsync(responsePacket, SocketFlags.None, client, ct);
                    }
                    else if (flag == SEE_YOU)
                    {
                        lastProgress = DateTime.UtcNow;
                        await SetUpAndSendPacket(stg: SET_ACK, data: [], socket, client, ct);
                        await SetUpAndSendPacket(stg: SET_SEE_YOU, data: [], socket, client, ct);
                        serverState = ConnectionState.Closed;
                    }
                    break;

                case ConnectionState.Closed:
                    if (flag == SEE_YOU)
                    {
                        // client retransmitted SEE_YOU
                        await SetUpAndSendPacket(stg: SET_ACK, data: [], socket, client, ct);
                        await SetUpAndSendPacket(stg: SET_SEE_YOU, data: [], socket, client, ct);
                    }
                    break;
            }

            if (serverState == ConnectionState.Closed)
            {
                if (flag == ACK)
                {
                    break;
                }
            }
        }

        // After all job done write data to stdout or to output file if specified
        if (config.Output != null && config.Output != "-")
        {
            await File.WriteAllBytesAsync(config.Output, receivedData.ToArray(), ct);
        }
        else
        {
            await using var stdout = Console.OpenStandardOutput();
            await stdout.WriteAsync(receivedData.ToArray(), ct);
        }
    }


    public static bool AcceptSegment(byte[] received, ref uint expectedSeq, List<byte> receivedData, Dictionary<uint, byte[]> outOfOrder)
    {
        uint currentPacketSeq = ReadSeqNum(received);
        ushort dataLen = ReadDataLength(received);
        var payload = received[PacketHeaderLength..];
        if (currentPacketSeq == expectedSeq)
        {
            // adding expected to range
            expectedSeq += dataLen;
            receivedData.AddRange(payload);
            // flush any buffered segments that are now in order
            while (outOfOrder.Remove(expectedSeq, out var packet))
            {
                dataLen = ReadDataLength(packet);
                expectedSeq += dataLen;
                payload = packet[PacketHeaderLength..];
                receivedData.AddRange(payload);
            }
            return true;
        }
        if (currentPacketSeq > expectedSeq && outOfOrder.ContainsKey(currentPacketSeq) == false)
        {
            outOfOrder[currentPacketSeq] = received;
        }
        return false;
    }
    
    /// <summary>
    /// Set up packet with defined stage and data, then asynchronously sent packet to dst
    /// </summary>
    /// <param name="stg"></param>
    /// <param name="data"></param>
    /// <param name="socket"></param>
    /// <param name="client"></param>
    /// <param name="ct"></param>
    private static async Task SetUpAndSendPacket(PacketSetUpStage stg, byte[] data, Socket socket, IPEndPoint client, CancellationToken ct)
    {
        var responsePacket = SetUpPacket(stage: stg, data);
        await socket.SendToAsync(responsePacket, SocketFlags.None, client, ct);
    }
    
    private static async Task RunClient(AppConfig config, CancellationToken ct)
    {
        // init state, waiting for HEY_YES from Server
        var state = ConnectionState.HeySent;

        (Socket socket, IPAddress address) = CreateClientSocket(config);

        IPEndPoint serverEndpoint = new IPEndPoint(address, config.Port);

        // get data to be transfered to server
        var dataBytes = await GetDataBytes(config, ct);
        int windowSize = 20;
        // max length of data to be sent in one packet
        int packetDataMaxLength = MaxPayloadSize;
        // total length of data to be sent
        int totalBytes = dataBytes.Length;
        // number of received duplicate ack packets
        int dupAckCount = 0;
        // first packet to send index
        int startPacketIdx = 0;
        // next packet to send
        int nextPacketIdxInOrder = 0;
        var lastProgress = DateTime.UtcNow;

        int numberOfRequiredPacketToBeSent = ComputeNumberOfReqPackets(totalBytes, packetDataMaxLength);
        List<PreparePacketStruct> packetDataInOrder = await PrepareDataForEachPacket(totalBytes, numberOfRequiredPacketToBeSent, dataBytes);

        SetConnectionId((byte)Random.Shared.Next(256));
        await SetUpAndSendPacket(stg: SET_HEY, data: [], socket, serverEndpoint, ct);

        while (ct.IsCancellationRequested == false)
        {
            byte[] buffer = new byte[1200];
            
            SocketReceiveFromResult? result;
            
            // retransmission when packet does not arrive
            using (var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                receiveCts.CancelAfter(TimeSpan.FromMilliseconds(RetransmitMs));
                try
                {
                    result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, serverEndpoint, receiveCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested == false)
                {
                    if ((DateTime.UtcNow - lastProgress).TotalSeconds >= config.TimeoutSec)
                    {
                        await Console.Error.WriteLineAsync("Error: Client timed out.");
                        Environment.Exit(1);
                    }
                    if (state == ConnectionState.HeySent)
                        await SetUpAndSendPacket(stg: SET_HEY, data: [], socket, serverEndpoint, ct);
                    else if (state == ConnectionState.Established)
                    {
                        for (int i = startPacketIdx; i < nextPacketIdxInOrder; i++)
                        {
                            var pkt = packetDataInOrder[i];
                            var frame = PreparePacket(DATA, (uint)pkt._packetSeqNumber, 0, pkt._packetData);
                            await socket.SendToAsync(frame, SocketFlags.None, serverEndpoint, ct);
                        }
                    }
                    else if (state == ConnectionState.FinWait)
                        await SetUpAndSendPacket(stg: SET_SEE_YOU, data: [], socket, serverEndpoint, ct);
                    continue;
                }
            }
            
            
            // packet is dropped if check not pass
            if (!CheckCrc(buffer[..result.Value.ReceivedBytes])) continue;
            var actualFlag = (PacketFlags) buffer[FlagOffset];

            switch (state)
            {
                case ConnectionState.HeySent:
                    if (actualFlag != HEY_YES) break;
                    await SetUpAndSendPacket(stg: SET_ACK, data: [], socket, serverEndpoint, ct);
                    state = ConnectionState.Established;
                    lastProgress = DateTime.UtcNow;
                    if (totalBytes == 0) // no data to transfer, just close connection with SEE_YOU
                    {
                        await SetUpAndSendPacket(stg: SET_SEE_YOU, data: [], socket, serverEndpoint, ct);
                        state = ConnectionState.FinWait;
                        break;
                    }

                    var maxRangeIndex = startPacketIdx + windowSize;
                    while (nextPacketIdxInOrder < maxRangeIndex && nextPacketIdxInOrder < packetDataInOrder.Count)
                    {
                        var pkt = packetDataInOrder[nextPacketIdxInOrder];
                        var frame = PreparePacket(DATA, (uint)pkt._packetSeqNumber, 0, pkt._packetData);
                        await socket.SendToAsync(frame, SocketFlags.None, serverEndpoint, ct);
                        nextPacketIdxInOrder++;
                    }
                    break;

                case ConnectionState.Established:
                    if (actualFlag != ACK) break;
                    // current expected number of byte by server
                    var currentPacketAckNum = ReadAckNum(buffer);
                    // number of first byte sent in window
                    var oldestUnAckedSeq  = (uint)packetDataInOrder[startPacketIdx]._packetSeqNumber;
                    if (currentPacketAckNum > oldestUnAckedSeq)
                    {
                        while (startPacketIdx < packetDataInOrder.Count &&
                               packetDataInOrder[startPacketIdx]._packetSeqNumber < currentPacketAckNum)
                            startPacketIdx++;

                        lastProgress = DateTime.UtcNow;
                        dupAckCount = 0;
                        windowSize++;

                        // Fill window with new packets
                        while (nextPacketIdxInOrder < (startPacketIdx + windowSize) && nextPacketIdxInOrder < packetDataInOrder.Count)
                        {
                            var pkt = packetDataInOrder[nextPacketIdxInOrder];
                            var frame = PreparePacket(DATA, (uint)pkt._packetSeqNumber, 0, pkt._packetData);
                            await socket.SendToAsync(frame, SocketFlags.None, serverEndpoint, ct);
                            nextPacketIdxInOrder++;
                        }

                        if (startPacketIdx >= packetDataInOrder.Count)
                        {
                            await SetUpAndSendPacket(stg: SET_SEE_YOU, data: [], socket, serverEndpoint, ct);
                            state = ConnectionState.FinWait;
                        }
                    }
                    // arrived duplicit packet of already acked packet
                    else if (currentPacketAckNum == oldestUnAckedSeq)
                    {
                        dupAckCount++;
                        if (dupAckCount >= 5)
                        {
                            dupAckCount = 0;
                            windowSize = Math.Max(1, windowSize / 2);
                            nextPacketIdxInOrder = startPacketIdx;
                            while (nextPacketIdxInOrder < (startPacketIdx + windowSize) && nextPacketIdxInOrder < packetDataInOrder.Count)
                            {
                                var pkt = packetDataInOrder[nextPacketIdxInOrder];
                                var frame = PreparePacket(DATA, (uint)pkt._packetSeqNumber, 0, pkt._packetData);
                                await socket.SendToAsync(frame, SocketFlags.None, serverEndpoint, ct);
                                nextPacketIdxInOrder++;
                            }
                        }
                    }
                    break;

                case ConnectionState.FinWait:
                    if (actualFlag != SEE_YOU) break;
                    await SetUpAndSendPacket(stg: SET_ACK, data: [], socket, serverEndpoint, ct);
                    lastProgress = DateTime.UtcNow;
                    state = ConnectionState.Closed;
                    break;
            }
            // connection is closed
            if (state == ConnectionState.Closed)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Prepares data for each packet with appropriate sequence number and data bytes slice for each packet, then return list of prepared packets in order.
    /// </summary>
    /// <param name="totalBytes"></param>
    /// <param name="numberOfRequiredPacketToBeSent"></param>
    /// <param name="dataBytes"></param>
    /// <returns></returns>
    public static Task<List<PreparePacketStruct>> PrepareDataForEachPacket(int totalBytes, int numberOfRequiredPacketToBeSent, byte[] dataBytes)
    {
        List<PreparePacketStruct> packetDataInOrder = [];
        for (int i = 0; i < numberOfRequiredPacketToBeSent; i++)
        {
            int offset = i * MaxPayloadSize;
            int len = Math.Min(MaxPayloadSize, totalBytes - offset);
            packetDataInOrder.Add(new PreparePacketStruct(packetSeqNumber: offset, dataBytes[offset..(offset + len)]));
        }
        return Task.FromResult(packetDataInOrder);
    }
    /// <summary>
    /// Computes number of required packets to be sent
    /// </summary>
    /// <param name="totalBytes"></param>
    /// <param name="packetDataLength"></param>
    /// <returns></returns>
    public static int ComputeNumberOfReqPackets(int totalBytes, int packetDataLength)
    {
        return (totalBytes + packetDataLength - 1) / packetDataLength;
    }

    /// <summary>
    /// Reads from stdin or file, depends on user arguments, and returns data as byte array.
    /// </summary>
    /// <param name="config">App configuration, using input info in this function</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Data that user provide to client to be sent</returns>
    private static async Task<byte[]> GetDataBytes(AppConfig config, CancellationToken ct)
    {
        if (config.Input != null && config.Input != "-") // path to file is provided
        {
            return await File.ReadAllBytesAsync(config.Input, ct);
        }
        // read from console stdin
        using MemoryStream ms = new MemoryStream();
        using Stream stdin = Console.OpenStandardInput();
        
        await stdin.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}