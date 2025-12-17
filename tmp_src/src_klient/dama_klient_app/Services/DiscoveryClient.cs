using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace dama_klient_app.Services;

public static class DiscoveryClient
{
    //UDP discovery na LAN. Vrací (host, port) nebo null při chybě.
    public static async Task<(string Host, int Port)?> DiscoverAsync(int discoveryPort = 9999, int timeoutMs = 1500)
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;

        var payload = Encoding.UTF8.GetBytes("DISCOVER\n");
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, discoveryPort);
        var loopbackEndpoint = new IPEndPoint(IPAddress.Loopback, discoveryPort);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await udp.SendAsync(payload, payload.Length, broadcastEndpoint);
                await udp.SendAsync(payload, payload.Length, loopbackEndpoint);
                var receiveTask = udp.ReceiveAsync();
                var timeoutTask = Task.Delay(timeoutMs);
                var finished = await Task.WhenAny(receiveTask, timeoutTask);
                if (finished != receiveTask)
                {
                    continue; // timeout, zkus další pokus
                }
                var result = await receiveTask;
                var text = Encoding.UTF8.GetString(result.Buffer).Trim();
                // očekává: "0;ENDPOINT;host=<ip>;port=<port>"
                var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[1] == "ENDPOINT")
                {
                    string? host = null;
                    int port = 0;
                    for (int i = 2; i < parts.Length; i++)
                    {
                        var kv = parts[i].Split('=', 2);
                        if (kv.Length != 2) continue;
                        if (kv[0] == "host") host = kv[1];
                        else if (kv[0] == "port") int.TryParse(kv[1], out port);
                    }
                    // pokud server vrátí 0.0.0.0 (bind na všech), použij adresu, odkud přišla odpověď
                    if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0")
                    {
                        host = result.RemoteEndPoint.Address.ToString();
                    }
                    if (port == 0)
                    {
                        port = result.RemoteEndPoint.Port;
                    }
                    if (!string.IsNullOrWhiteSpace(host) && port > 0)
                    {
                        return (host, port);
                    }
                }
            }
            catch
            {
                // timeout nebo parsovací chyba -> zkusit znovu
            }
        }

        return null;
    }
}
