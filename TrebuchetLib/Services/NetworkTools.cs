using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TrebuchetLib.Services;

public sealed record NetworkAdapterInfo(string Name, string Address, string MacAddress)
{
    public override string ToString() => $"{Address} — {Name}";
}

public sealed record PortTestResult(string Label, int Port, ProtocolType Protocol, bool Available, string Message);

/// <summary>
/// Network helpers for the server settings (DSL "Network Configuration" block).
/// </summary>
public static class NetworkTools
{
    public static List<NetworkAdapterInfo> GetLocalAdapters()
    {
        var result = new List<NetworkAdapterInfo>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var mac = FormatMac(nic.GetPhysicalAddress());
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    result.Add(new NetworkAdapterInfo(nic.Description, address.Address.ToString(), mac));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // no adapter information available
        }
        return result;
    }

    /// <summary>
    /// Check whether a port can be bound locally. A port used by the running server is reported as in use.
    /// Reachability from the internet still depends on router port forwarding and firewall rules.
    /// </summary>
    public static PortTestResult TestPort(string label, int port, ProtocolType protocol, string? bindAddress)
    {
        if (port is < 1 or > 65535)
            return new PortTestResult(label, port, protocol, false, "Invalid port");

        var address = IPAddress.Any;
        if (!string.IsNullOrWhiteSpace(bindAddress) && IPAddress.TryParse(bindAddress.Trim(), out var parsed))
            address = parsed;

        try
        {
            if (protocol == ProtocolType.Udp)
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.ExclusiveAddressUse = true;
                udp.Client.Bind(new IPEndPoint(address, port));
            }
            else
            {
                var tcp = new TcpListener(address, port) { ExclusiveAddressUse = true };
                tcp.Start();
                tcp.Stop();
            }
            return new PortTestResult(label, port, protocol, true, "Free");
        }
        catch (SocketException ex)
        {
            // Show the actual WinSock error like DSL 1.9.3.
            return new PortTestResult(label, port, protocol, false, $"{ex.SocketErrorCode}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new PortTestResult(label, port, protocol, false, ex.Message);
        }
    }

    private static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? string.Empty : string.Join("-", bytes.Select(b => b.ToString("X2")));
    }
}
