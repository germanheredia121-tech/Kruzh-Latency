using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace KruzhLatency.Controls;

/// <summary>
/// Abre una ventana WinUI 3 sin el flash blanco/gris del primer instante: la ventana se activa "encubierta" (DWM cloak:
/// existe y renderiza, pero no se ve), y se descubre recién cuando XAML ya pintó sus primeros frames. Además la trae
/// al frente: una ventana creada segundos después del lanzamiento puede quedar detrás de otras (Windows limita el foco).
/// </summary>
internal static class WindowReveal
{
    private const int DWMWA_CLOAK = 13;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_RESTORE = 9;
    private const int FramesBeforeReveal = 3;
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromMilliseconds(1500);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    /// <summary>Reemplaza a window.Activate(). <paramref name="onRevealed"/> corre en el hilo de UI cuando la ventana ya se ve.</summary>
    public static void ActivateWithoutFlash(Window window, Action? onRevealed = null)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(window);
        SetCloak(hwnd, true);
        window.Activate();

        bool revealed = false;
        int frames = 0;
        var timer = window.DispatcherQueue.CreateTimer();

        void Reveal()
        {
            if (revealed) return;
            revealed = true;
            CompositionTarget.Rendering -= OnRendering;
            timer.Stop();
            SetCloak(hwnd, false);
            BringToFront(hwnd);
            onRevealed?.Invoke();
        }

        void OnRendering(object? sender, object e)
        {
            try
            {
                bool loaded = window.Content is not FrameworkElement fe || fe.IsLoaded;
                if (loaded && ++frames >= FramesBeforeReveal) Reveal();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Detach(); // la ventana se cerró antes de mostrarse
            }
        }

        void Detach()
        {
            revealed = true;
            CompositionTarget.Rendering -= OnRendering;
            timer.Stop();
        }

        window.Closed += (_, _) => Detach();
        CompositionTarget.Rendering += OnRendering;
        timer.Interval = SafetyTimeout; // nunca dejar una ventana invisible si algo no renderiza
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Reveal();
        timer.Start();
    }

    /// <summary>Restaura si está minimizada y la pone en primer plano aunque otro proceso tenga el foco.</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        try
        {
            ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOWNORMAL);
            if (SetForegroundWindow(hwnd)) return;

            // Windows bloquea SetForegroundWindow si el foco lo tiene otro proceso: se adjunta temporalmente
            // la cola de entrada del hilo que tiene el foco (técnica documentada en AttachThreadInput).
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint current = GetCurrentThreadId();
            if (foregroundThread != 0 && foregroundThread != current && AttachThreadInput(current, foregroundThread, true))
            {
                SetForegroundWindow(hwnd);
                AttachThreadInput(current, foregroundThread, false);
            }
        }
        catch (Exception ex)
        {
            App.LogError("BringToFront", ex);
        }
    }

    private static void SetCloak(IntPtr hwnd, bool cloak)
    {
        int value = cloak ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }
}
