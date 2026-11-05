using System.Net;

namespace KruzhLatency.Core.Network;

/// <summary>Muestras de un salto de la ruta (pings con TTL limitado, como MTR/WinMTR).</summary>
public sealed record HopSamples(int Ttl, string? Address, int Sent, IReadOnlyList<double> RttMs)
{
    public int Received => RttMs.Count;
    public double LossPct => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
    public double AvgMs => RttMs.Count == 0 ? 0 : RttMs.Average();
    public double BestMs => RttMs.Count == 0 ? 0 : RttMs.Min();
    public double WorstMs => RttMs.Count == 0 ? 0 : RttMs.Max();

    /// <summary>Variación entre respuestas consecutivas (como el jitter de RFC 3550, sin suavizar).</summary>
    public double JitterMs => RttMs.Count < 2 ? 0 : RttMs.Zip(RttMs.Skip(1), (a, b) => Math.Abs(b - a)).Average();
}

public enum HopVerdict
{
    /// <summary>Responde sin pérdida.</summary>
    Ok,
    /// <summary>No responde nunca: router que no contesta pings (normal, no afecta el tráfico del juego).</summary>
    Silent,
    /// <summary>Pierde pings pero los saltos siguientes no: el router no prioriza responder pings. No es pérdida real.</summary>
    RateLimited,
    /// <summary>La pérdida empieza acá y sigue hasta el destino: pérdida real.</summary>
    LossStartsHere,
    /// <summary>Pérdida real que viene de un salto anterior.</summary>
    LossInherited,
}

/// <summary>Dónde está un salto, para explicar a quién reclamar.</summary>
public enum HopZone { Home, IspAccess, Beyond }

public sealed record HopReport(HopSamples Hop, HopVerdict Verdict, HopZone Zone);

public sealed record PathReport(IReadOnlyList<HopReport> Hops, HopSamples? Destination, double DestinationLossPct, double DestinationJitterMs,
                                HopReport? LossOrigin, string Summary);

/// <summary>
/// Lectura correcta de una prueba de ruta. La pérdida de un salto intermedio casi nunca es real: los routers responden los
/// pings con baja prioridad y descartan muchos sin afectar el tráfico que reenvían. Solo es real la pérdida que llega al
/// destino; su origen es el primer salto desde el que se mantiene (con margen) hasta el final.
/// </summary>
public static class PathAnalysis
{
    /// <summary>Pérdida en el destino a partir de la cual se considera real (por debajo es ruido de la propia prueba).</summary>
    public const double RealLossPct = 1.0;

    public static PathReport Analyze(IReadOnlyList<HopSamples> hops, string destinationAddress)
    {
        var ordered = hops.OrderBy(h => h.Ttl).ToList();
        var dest = ordered.LastOrDefault(h => h.Address == destinationAddress) ?? ordered.LastOrDefault(h => h.Received > 0);
        double destLoss = dest?.LossPct ?? 100;

        // Origen de la pérdida real: el primer salto que pierde al menos el 60 % de lo que pierde el destino y desde el que
        // todos los saltos que responden siguen perdiendo eso.
        HopSamples? origin = null;
        if (dest is not null && destLoss >= RealLossPct)
        {
            double threshold = destLoss * 0.6;
            var upToDest = ordered.Where(h => h.Ttl <= dest.Ttl && h.Received > 0).ToList();
            for (int i = 0; i < upToDest.Count; i++)
            {
                if (upToDest.Skip(i).All(h => h.LossPct >= threshold)) { origin = upToDest[i]; break; }
            }
        }

        bool firstPublicSeen = false;
        var reports = new List<HopReport>();
        foreach (var h in ordered)
        {
            HopZone zone;
            if (h.Ttl == 1) zone = HopZone.Home;
            else if (!firstPublicSeen && (h.Address is null || IsPrivateOrCgnat(h.Address))) zone = HopZone.IspAccess;
            else if (!firstPublicSeen) { zone = HopZone.IspAccess; firstPublicSeen = true; } // el primer salto público suele ser aún del proveedor
            else zone = HopZone.Beyond;

            HopVerdict verdict;
            if (h.Received == 0) verdict = HopVerdict.Silent;
            else if (origin is not null && h.Ttl == origin.Ttl) verdict = HopVerdict.LossStartsHere;
            else if (origin is not null && h.Ttl > origin.Ttl && h.LossPct > 0) verdict = HopVerdict.LossInherited;
            else if (h.LossPct > 0) verdict = HopVerdict.RateLimited;
            else verdict = HopVerdict.Ok;
            reports.Add(new HopReport(h, verdict, zone));
        }

        var originReport = origin is null ? null : reports.First(r => r.Hop.Ttl == origin.Ttl);
        return new PathReport(reports, dest, destLoss, dest?.JitterMs ?? 0, originReport, Summary(dest, destLoss, originReport, reports));
    }

    private static string Summary(HopSamples? dest, double destLoss, HopReport? origin, IReadOnlyList<HopReport> reports)
    {
        if (dest is null || dest.Received == 0)
            return "El destino no respondió a ningún ping: no se puede medir la ruta hasta él (muchos servidores de juego no contestan pings).";

        string jitter = $"jitter {dest.JitterMs:0.0} ms, ping {dest.AvgMs:0} ms (mejor {dest.BestMs:0}, peor {dest.WorstMs:0})";
        if (destLoss < RealLossPct)
        {
            bool rateLimited = reports.Any(r => r.Verdict == HopVerdict.RateLimited);
            return $"Sin pérdida real hasta el destino ({destLoss:0.#} %, {jitter})." +
                   (rateLimited ? " La pérdida que aparece en algunos saltos del medio es de routers que no priorizan responder pings: no afecta al juego." : "");
        }

        string where = origin?.Zone switch
        {
            HopZone.Home => "en tu red de casa (router o cable)",
            HopZone.IspAccess => "en la red de tu proveedor (primeros saltos)",
            _ => "más allá de tu proveedor (interconexión o red del servidor)",
        };
        string at = origin is null ? "" : $" Empieza en el salto {origin.Hop.Ttl} ({origin.Hop.Address}), {where}.";
        return $"Pérdida real hasta el destino: {destLoss:0.#} % ({jitter}).{at}";
    }

    /// <summary>Direcciones privadas, CGNAT (100.64/10) o de enlace local: la red de casa o el acceso del proveedor.</summary>
    public static bool IsPrivateOrCgnat(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
               (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254);
    }
}
