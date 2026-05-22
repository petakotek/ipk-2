using System.Net;
using System.Net.Sockets;

namespace Ipk2;

public class Client
{
    /// <summary>
    /// Creates client socket, 
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    public static (Socket, IPAddress) CreateClientSocket(AppConfig config)
    {
        if (config.Address != null)
        {
            IPAddress address = ResolveAddress(config.Address);
            AddressFamily family = address.AddressFamily;
        
            Socket socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        
            socket.Bind(new IPEndPoint(
                family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));

            return (socket, address);
        }
        return  (null, null)!;
    }

    private static IPAddress ResolveAddress(string host)
    {
        // validate host address
        if (IPAddress.TryParse(host, out IPAddress? parsed))
            return parsed;

        IPAddress[] addresses = Dns.GetHostAddresses(host);
        IPAddress? ipv6 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetworkV6);
        IPAddress? ipv4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);

        if (ipv6 != null)
        {
            return ipv6;
        }

        if (ipv4 != null)
        {
            return ipv4;
        }

        throw new ArgumentException($"Cannot resolve host: {host}");
    }
}