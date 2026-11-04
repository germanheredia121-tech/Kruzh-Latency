using System.Diagnostics;
using System.Runtime.InteropServices;
using KruzhLatency.Core.Latency;

namespace KruzhLatency.Infra.Timer;

/// <summary>Resolución del timer del kernel (ntdll). Valores en unidades de 100 ns.</summary>
public static partial class TimerResolution
{
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSetTimerResolution(uint desired, [MarshalAs(UnmanagedType.U1)] bool set, out uint current);

    /// <summary>(mínima = la más gruesa, máxima = la más fina, actual), o null si ntdll no responde.</summary>
    public static (uint Coarsest, uint Finest, uint Current)? Query() =>
        NtQueryTimerResolution(out uint min, out uint max, out uint cur) == 0 ? (min, max, cur) : null;

    /// <summary>Pide (set = true) o libera (set = false) una resolución; vale mientras viva este proceso.</summary>
    public static uint Request(uint hundredNs, bool set)
    {
        NtSetTimerResolution(hundredNs, set, out uint current);
        return current;
    }
}

/// <summary>
/// ADR-010: proceso residente sin ventana. Cada 5 s mira la lista de procesos del sistema por nombre (no abre ningún
/// handle al juego: ADR-004, compatible con Vanguard y FACEIT). Mientras CS2 o VALORANT estén abiertos mantiene pedidos
/// 0,5 ms; cuando se cierran, libera el pedido. Sin ventana, sin WMI, sin registro.
/// </summary>
public static class TimerResidentAgent
{
    private const string MutexName = @"Local\KruzhLatency.TimerAgent";

    /// <returns>0 si terminó bien; 1 si ya había otro residente corriendo.</returns>
    public static int Run(CancellationToken cancel)
    {
        using var single = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew) return 1;

        bool requested = false;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                bool gameOpen = IsGameRunning();
                if (gameOpen != requested)
                {
                    TimerResolution.Request(TimerResidentPolicy.ResolutionHundredNs, set: gameOpen);
                    requested = gameOpen;
                }
                cancel.WaitHandle.WaitOne(TimerResidentPolicy.GameCheckInterval);
            }
        }
        finally
        {
            if (requested) TimerResolution.Request(TimerResidentPolicy.ResolutionHundredNs, set: false);
        }
        return 0;
    }

    /// <summary>true si hay un residente corriendo en esta sesión.</summary>
    public static bool IsRunning()
    {
        if (Mutex.TryOpenExisting(MutexName, out var m))
        {
            m.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>Solo nombres de la lista de procesos (NtQuerySystemInformation): ningún handle al juego.</summary>
    private static bool IsGameRunning()
    {
        foreach (string name in TimerResidentPolicy.GameProcesses)
        {
            var found = Process.GetProcessesByName(name);
            foreach (var p in found) p.Dispose();
            if (found.Length > 0) return true;
        }
        return false;
    }
}
