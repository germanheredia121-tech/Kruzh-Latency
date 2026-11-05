using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace KruzhLatency.Infra.Etw;

/// <summary>
/// Paquetes UDP que recibe un proceso, por dirección remota, con su hora de llegada (eventos de red del kernel por ETW).
/// No abre ni toca el proceso del juego (ADR-004): el PID sale de la lista de procesos por nombre y solo se leen los
/// eventos de red de Windows. El contenido de los paquetes no se ve (CS2 los cifra), solo su llegada.
/// </summary>
public static class UdpFlowCapture
{
    private const string SessionName = "KruzhLatency-UdpFlow";

    /// <summary>PIDs del proceso por nombre (sin abrirlo).</summary>
    public static IReadOnlyCollection<int> ProcessIds(string processName)
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(processName)) { using (p) ids.Add(p.Id); }
        return ids;
    }

    /// <summary>
    /// Captura durante <paramref name="duration"/>. Devuelve, por "ip:puerto" remoto, los milisegundos de llegada de cada
    /// paquete. Error legible si no se pudo (sin administrador, etc.).
    /// </summary>
    public static (Dictionary<string, List<double>>? Flows, string? Error) Capture(IReadOnlyCollection<int> pids, TimeSpan duration, CancellationToken cancel)
    {
        if (!(TraceEventSession.IsElevated() ?? false)) return (null, "Se necesita KRUZH como administrador para leer los eventos de red.");
        if (pids.Count == 0) return (null, "CS2 no está abierto.");

        var local = LocalAddresses();
        var flows = new Dictionary<string, List<double>>();
        var gate = new object();
        try
        {
            using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            void OnRecv(UdpIpTraceData d)
            {
                if (!pids.Contains(d.ProcessID)) return;
                // Los eventos traen las dos puntas: la remota es la que no es de esta PC.
                bool dIsLocal = local.Contains(d.daddr.ToString());
                string remote = dIsLocal ? $"{d.saddr}:{d.sport}" : $"{d.daddr}:{d.dport}";
                lock (gate)
                {
                    if (!flows.TryGetValue(remote, out var list)) flows[remote] = list = new List<double>();
                    list.Add(d.TimeStampRelativeMSec);
                }
            }
            void OnRecv6(UpdIpV6TraceData d) // (así se llama el tipo en TraceEvent)
            {
                if (!pids.Contains(d.ProcessID)) return;
                bool dIsLocal = local.Contains(d.daddr.ToString());
                string remote = dIsLocal ? $"[{d.saddr}]:{d.sport}" : $"[{d.daddr}]:{d.dport}";
                lock (gate)
                {
                    if (!flows.TryGetValue(remote, out var list)) flows[remote] = list = new List<double>();
                    list.Add(d.TimeStampRelativeMSec);
                }
            }
            session.Source.Kernel.UdpIpRecv += OnRecv;
            session.Source.Kernel.UdpIpRecvIPV6 += OnRecv6;

            var processing = Task.Run(() => session.Source.Process());
            cancel.WaitHandle.WaitOne(duration);
            session.Stop();
            processing.Wait(TimeSpan.FromSeconds(5));
        }
        catch (UnauthorizedAccessException) { return (null, "Windows no permitió leer los eventos de red (se requiere administrador)."); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return (null, $"No se pudo iniciar la captura de red: {ex.Message}");
        }

        lock (gate) return (flows, null);
    }

    private static HashSet<string> LocalAddresses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IPAddress.Loopback.ToString(), IPAddress.IPv6Loopback.ToString() };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var a in nic.GetIPProperties().UnicastAddresses) set.Add(a.Address.ToString());
        }
        return set;
    }
}
