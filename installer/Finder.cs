using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace XMInstaller;

sealed record FoundDevice(string Ip, int Port)
{
    public override string ToString() => Ip + "  порт " + Port;
}

static class Finder
{
    public static List<FoundDevice> Probe(string host)
    {
        var found = new List<FoundDevice>();
        foreach (var port in new[] { 34568, 34567 })
        {
            if (PortOpen(host, port))
                found.Add(new FoundDevice(host, port));
        }
        return found;
    }

    public static List<FoundDevice> Scan(CancellationToken cancel)
    {
        var hosts = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var bytes = address.Address.GetAddressBytes();
                if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254))
                    continue;
                for (var host = 1; host <= 254; host++)
                {
                    if (host == bytes[3])
                        continue;
                    hosts.Add(bytes[0] + "." + bytes[1] + "." + bytes[2] + "." + host);
                }
            }
        }

        var found = new System.Collections.Concurrent.ConcurrentBag<FoundDevice>();
        var ports = new[] { 34568, 34567 };
        Parallel.ForEach(hosts, new ParallelOptions { MaxDegreeOfParallelism = 80, CancellationToken = cancel }, ip =>
        {
            foreach (var port in ports)
            {
                if (cancel.IsCancellationRequested)
                    return;
                if (PortOpen(ip, port))
                    found.Add(new FoundDevice(ip, port));
            }
        });
        return found.OrderBy(item => item.Ip, StringComparer.Ordinal).ThenByDescending(item => item.Port).ToList();
    }

    static bool PortOpen(string ip, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(ip, port);
            if (!task.Wait(280))
                return false;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
