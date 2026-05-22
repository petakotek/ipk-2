using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Ipk2;

public class Server
{
    public static (Socket, AddressFamily) CreateServerSocket(AppConfig config)
    {
        IPAddress bindAddr = IPAddress.Parse(config.Address ?? "::");

        AddressFamily family = bindAddr.AddressFamily;
        Socket socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        if (family == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = true; // enable both IPv4/IPv6
        }

        if (!bindAddr.Equals(IPAddress.Any) && !bindAddr.Equals(IPAddress.IPv6Any) && !IsLocalAddress(bindAddr))
        {
            Console.Error.WriteLine("Server: Entered address is not a local address on this machine.");
            socket.Dispose();
            Environment.Exit(1);
        }

        try
        {
            socket.Bind(new IPEndPoint(bindAddr, config.Port));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressNotAvailable)
        {
            Console.Error.WriteLine("[Server] Error: cannot bind, address not available.");
            socket.Dispose();
            Environment.Exit(1);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Console.Error.WriteLine("[Server] Error: port is already in use.");
            socket.Dispose();
            Environment.Exit(1);
        }
        return (socket, family);
    }
    /// <summary>
    /// Checking if entered adress for server is local address on local machine
    /// </summary>
    /// <param name="address">address entered by user</param>
    /// <returns>If address is local, returns true, else returns false</returns>
    private static bool IsLocalAddress(IPAddress address)
    {
        foreach (NetworkInterface iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (UnicastIPAddressInformation uni in iface.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.Equals(address))
                    return true;
            }
        }
        return false;
    }
}