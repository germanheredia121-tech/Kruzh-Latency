using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using KruzhLatency.Core.Network;

namespace KruzhLatency.Infra.Network;

/// <summary>
/// Prueba de ruta como MTR/WinMTR: pings al destino con TTL limitado. Cada salto que descarta el paquete por TTL contesta
/// "tiempo excedido" con su dirección, así se mide pérdida y demora de cada router hasta el destino.
/// El tiempo se toma con un cronómetro propio: .NET devuelve RoundtripTime = 0 en las respuestas de TTL excedido.
/// </summary>
public static class PathProbe
{
    public const int MaxHops = 30;
    private const int TimeoutMs = 1000;
    private static readonly byte[] Payload = new byte[32];

    /// <summary>Resuelve el destino (IP o nombre). null si no se pudo.</summary>
    public static IPAddress? Resolve(string target)
    {
        if (IPAddress.TryParse(target.Trim(), out var ip)) return ip;
        try { return Dns.GetHostAddresses(target.Trim()).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork); }
        catch (System.Net.Sockets.SocketException) { return null; }
    }

    /// <summary>
    /// Mide la ruta durante <paramref name="rounds"/> rondas (una por segundo). En cada ronda manda un ping por salto, en
    /// paralelo. <paramref name="progress"/> recibe (ronda, total).
    /// </summary>
    public static async Task<IReadOnlyList<HopSamples>> SampleAsync(IPAddress target, int rounds, IProgress<(int Round, int Total)>? progress, CancellationToken cancel)
    {
        int hopCount = await DiscoverHopCountAsync(target, cancel);
        var rtts = Enumerable.Range(0, hopCount).Select(_ => new List<double>()).ToArray();
        var addresses = Enumerable.Range(0, hopCount).Select(_ => new Dictionary<string, int>()).ToArray();
        int sent = 0;

        for (int round = 0; round < rounds && !cancel.IsCancellationRequested; round++)
        {
            var started = Stopwatch.StartNew();
            var tasks = Enumerable.Range(1, hopCount).Select(ttl => ProbeAsync(target, ttl)).ToArray();
            var results = await Task.WhenAll(tasks);
            sent++;
            for (int i = 0; i < hopCount; i++)
            {
                if (results[i] is not { } r) continue;
                rtts[i].Add(r.Ms);
                addresses[i][r.Address] = addresses[i].GetValueOrDefault(r.Address) + 1;
            }
            progress?.Report((round + 1, rounds));
            int wait = 1000 - (int)started.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay(wait, cancel).ContinueWith(_ => { });
        }

        return Enumerable.Range(0, hopCount)
            .Select(i => new HopSamples(i + 1, addresses[i].OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault(), sent, rtts[i]))
            .ToList();
    }

    /// <summary>Cantidad de saltos hasta el destino (o hasta <see cref="MaxHops"/> si no responde).</summary>
    private static async Task<int> DiscoverHopCountAsync(IPAddress target, CancellationToken cancel)
    {
        int lastResponding = 1;
        for (int ttl = 1; ttl <= MaxHops && !cancel.IsCancellationRequested; ttl++)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (await ProbeAsync(target, ttl) is not { } r) continue;
                lastResponding = ttl;
                if (r.Reached) return ttl;
                break;
            }
        }
        return lastResponding;
    }

    private static async Task<(string Address, double Ms, bool Reached)?> ProbeAsync(IPAddress target, int ttl)
    {
        using var ping = new Ping();
        var watch = Stopwatch.StartNew();
        try
        {
            var reply = await ping.SendPingAsync(target, TimeoutMs, Payload, new PingOptions(ttl, true));
            double ms = watch.Elapsed.TotalMilliseconds;
            return reply.Status switch
            {
                IPStatus.Success => (reply.Address.ToString(), reply.RoundtripTime > 0 ? reply.RoundtripTime : ms, true),
                IPStatus.TtlExpired or IPStatus.TimeExceeded => (reply.Address.ToString(), ms, false),
                _ => null,
            };
        }
        catch (PingException) { return null; }
    }
}
