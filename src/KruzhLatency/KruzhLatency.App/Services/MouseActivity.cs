using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace KruzhLatency.Services;

/// <summary>
/// Reportes por segundo del mouse, leídos con Raw Input en la ventana de KRUZH (solo lectura).
/// Se registra en la ventana principal y no como "receptor en segundo plano": Windows 11 agrupa la entrada de las ventanas que
/// no están en primer plano (~125 mensajes/s aunque el mouse vaya a 1000 Hz). Durante las mediciones KRUZH está en primer plano.
/// Sirve para (a) mostrar en vivo si el usuario está moviendo el mouse y (b) repetir una medición de USB si no lo movió.
/// </summary>
public static class MouseActivity
{
    private const int WM_INPUT = 0x00FF;
    private const int RingSize = 16384; // de sobra para 1 s a 8000 Hz

    private static readonly long[] Ring = new long[RingSize];
    private static long _count;
    private static double _peakRate;
    private static SubclassProc? _proc; // referencia viva: el GC no puede juntar el delegate mientras Windows lo llama
    private static bool _attached;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Total de reportes recibidos desde que arrancó KRUZH.</summary>
    public static long Total => Interlocked.Read(ref _count);

    /// <summary>El máximo de reportes/s visto (≈ el polling real del mouse cuando se mueve rápido).</summary>
    public static double PeakRate => Volatile.Read(ref _peakRate);

    /// <summary>Se llama una vez con la ventana principal. Si falla, las mediciones siguen sin el chequeo de movimiento.</summary>
    public static void Attach(IntPtr hwnd)
    {
        if (_attached || hwnd == IntPtr.Zero) return;
        _proc = WndProc;
        if (!SetWindowSubclass(hwnd, _proc, (UIntPtr)0x4B52555A, UIntPtr.Zero)) return;
        var dev = new[] { new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = 0, Target = hwnd } };
        _attached = RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    public static bool Available => _attached;

    /// <summary>Reportes recibidos en el último segundo.</summary>
    public static int RatePerSecond()
    {
        long total = Total;
        if (total == 0) return 0;
        long now = Stopwatch.GetTimestamp();
        long limit = now - Stopwatch.Frequency;
        int n = 0;
        for (long i = total - 1; i >= 0 && i >= total - RingSize; i--)
        {
            if (Ring[i % RingSize] < limit) break;
            n++;
        }
        return n;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (msg == WM_INPUT)
        {
            long i = Interlocked.Increment(ref _count) - 1;
            Ring[i % RingSize] = Stopwatch.GetTimestamp();
            if ((i & 255) == 0)
            {
                int rate = RatePerSecond();
                if (rate > _peakRate) Volatile.Write(ref _peakRate, rate);
            }
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
