using System;
using KruzhLatency.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace KruzhLatency;

/// <summary>Overlay de pasos: relleno verde de progreso en el carrusel y panel "mové el mouse" de las mediciones del USB.</summary>
public sealed partial class MainWindow
{
    private double _overlayProgress;

    /// <summary>
    /// Barra fina + relleno verde del contenedor del carrusel. El relleno es un ancho fijo dentro de un contenedor recortado
    /// al tamaño del carrusel (antes era una escala animada que se deformaba con las esquinas y al deslizar las filas).
    /// </summary>
    private void SetOverlayProgress(double percent)
    {
        _overlayProgress = Math.Clamp(percent, 0, 100);
        OverlayProgressBar.Value = _overlayProgress;
        LayoutCarouselProgress();
    }

    private void CarouselProgressHost_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutCarouselProgress();

    private void LayoutCarouselProgress()
    {
        double w = CarouselProgressHost.ActualWidth, h = CarouselProgressHost.ActualHeight;
        if (w <= 0 || h <= 0) return;
        CarouselProgressClip.Rect = new Windows.Foundation.Rect(0, 0, w, h);
        CarouselProgressFill.Width = w * _overlayProgress / 100.0;
    }

    /// <summary>
    /// Línea para los diálogos de medición: si hay software de periféricos abierto, puede no dejar reiniciar los USB
    /// (Windows lo registra como un veto) y esos dispositivos quedarían sin medir.
    /// </summary>
    private static string PeripheralNotice()
    {
        var apps = KruzhLatency.Infra.Devices.PeripheralSoftware.Running();
        return apps.Count == 0
            ? string.Empty
            : $"• Cerrá {string.Join(", ", apps)} antes de empezar (también desde la bandeja del sistema): no deja reiniciar el USB y esos dispositivos quedarían sin medir.\n";
    }

    /// <summary>Línea para los diálogos de medición con los reinicios de Windows que puede pedir.</summary>
    private static string RebootLine(int reboots) => reboots == 0
        ? "• No hace falta reiniciar Windows: todos los dispositivos se reinician en caliente.\n"
        : $"• Va a reiniciar Windows hasta {reboots} veces: Windows no deja reiniciar en caliente algunas controladoras (las USB del teclado y el mouse). KRUZH se abre solo al volver y sigue. Para reducirlo, en esas solo se prueba lo esencial.\n";

    private DispatcherTimer? _mouseGuideTimer;
    private DateTime _mouseGuideStart;
    private TimeSpan _mouseGuideExpected;

    /// <summary>Muestra el panel del mouse y lo actualiza cada 200 ms con los reportes/s y el tiempo que falta.</summary>
    private void StartMouseGuide(TimeSpan expected)
    {
        _mouseGuideStart = DateTime.UtcNow;
        _mouseGuideExpected = expected;
        MouseGuidePanel.Visibility = Visibility.Visible;
        _mouseGuideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _mouseGuideTimer.Tick -= MouseGuideTick;
        _mouseGuideTimer.Tick += MouseGuideTick;
        MouseGuideTick(null, null);
        _mouseGuideTimer.Start();
    }

    private void StopMouseGuide()
    {
        _mouseGuideTimer?.Stop();
        MouseGuidePanel.Visibility = Visibility.Collapsed;
    }

    private void MouseGuideTick(object? sender, object? e)
    {
        var res = Application.Current.Resources;
        int rate = MouseActivity.RatePerSecond();
        var left = _mouseGuideExpected - (DateTime.UtcNow - _mouseGuideStart);
        string time = left.TotalSeconds > 0 ? $"Quedan ~{Math.Ceiling(left.TotalSeconds):0} s" : "Terminando…";
        string notice = InterruptMeasure.Notice;

        if (!MouseActivity.Available)
        {
            MouseGuideTitleText.Text = "Mové el mouse en círculos, sin parar";
            MouseGuideDetailText.Text = $"{time} · no pude leer el mouse, pero movelo igual";
            MouseGuideRateText.Text = "—";
            return;
        }

        bool moving = rate >= 50;
        MouseGuideRateText.Text = rate.ToString();
        MouseGuideTitleText.Text = moving ? "Bien, seguí moviendo el mouse en círculos" : "¡Mové el mouse en círculos!";
        MouseGuideDot.Background = (Brush)res[moving ? "KruzhPositiveBrush" : "KruzhAmberBrush"];
        MouseGuidePanel.BorderBrush = (Brush)res[moving ? "KruzhPositiveBorderBrush" : "KruzhAmberBorderBrush"];
        string peak = MouseActivity.PeakRate > 0 ? $" · polling visto: {MouseActivity.PeakRate:0} Hz" : string.Empty;
        MouseGuideDetailText.Text = string.IsNullOrEmpty(notice) ? time + peak : notice;
    }
}
