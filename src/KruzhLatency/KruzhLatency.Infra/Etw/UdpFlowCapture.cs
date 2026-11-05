using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace KruzhLatency.Infra.Etw;

/// <summary>Momentos (ms desde el inicio de la captura) de los paquetes de un flujo, por dirección.</summary>
public sealed class UdpFlowTimes
{
    public List<double> Received { get; } = new();
    public List<double> Sent { get; } = new();
}

/// <summary>
/// Paquetes UDP que recibe y manda un proceso, por dirección remota, con su hora (eventos de red del kernel por ETW).
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
    /// Captura durante <paramref name="duration"/>. Devuelve, por "ip:puerto" remoto, los milisegundos (desde el inicio de
    /// la sesión, mismo reloj para las dos direcciones) de cada paquete recibido y de cada paquete enviado.
    /// En el envío el momento es cuando el juego entrega el paquete a Windows. Error legible si no se pudo (sin administrador, etc.).
    /// </summary>
    public static (Dictionary<string, UdpFlowTimes>? Flows, string? Error) Capture(IReadOnlyCollection<int> pids, TimeSpan duration, CancellationToken cancel)
    {
        if (!(TraceEventSession.IsElevated() ?? false)) return (null, "Se necesita KRUZH como administrador para leer los eventos de red.");
        if (pids.Count == 0) return (null, "CS2 no está abierto.");

        var local = LocalAddresses();
        var flows = new Dictionary<string, UdpFlowTimes>();
        var gate = new object();
        void Add(string remote, double ms, bool received)
        {
            lock (gate)
            {
                if (!flows.TryGetValue(remote, out var f)) flows[remote] = f = new UdpFlowTimes();
                (received ? f.Received : f.Sent).Add(ms);
            }
        }
        // Los eventos traen las dos puntas: la remota es la que no es de esta PC.
        string Remote4(UdpIpTraceData d) => local.Contains(d.daddr.ToString()) ? $"{d.saddr}:{d.sport}" : $"{d.daddr}:{d.dport}";
        string Remote6(UpdIpV6TraceData d) => local.Contains(d.daddr.ToString()) ? $"[{d.saddr}]:{d.sport}" : $"[{d.daddr}]:{d.dport}";
        try
        {
            using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            // (UpdIpV6TraceData: así se llama el tipo en TraceEvent)
            session.Source.Kernel.UdpIpRecv += d => { if (pids.Contains(d.ProcessID)) Add(Remote4(d), d.TimeStampRelativeMSec, true); };
            session.Source.Kernel.UdpIpRecvIPV6 += d => { if (pids.Contains(d.ProcessID)) Add(Remote6(d), d.TimeStampRelativeMSec, true); };
            session.Source.Kernel.UdpIpSend += d => { if (pids.Contains(d.ProcessID)) Add(Remote4(d), d.TimeStampRelativeMSec, false); };
            session.Source.Kernel.UdpIpSendIPV6 += d => { if (pids.Contains(d.ProcessID)) Add(Remote6(d), d.TimeStampRelativeMSec, false); };

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
