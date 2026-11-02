using System;
using System.Runtime.InteropServices;
using KruzhLatency.Controls;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace KruzhLatency;

/// <summary>Splash chico (420x300) que se muestra al instante mientras la inicialización pesada corre en segundo plano.</summary>
public sealed partial class SplashWindow : Window
{
    private const int SplashWidth = 420;
    private const int SplashHeight = 300;

    // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2 (Windows 11; en Windows 10 se ignora).
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private Storyboard? _spinStoryboard;

    public SplashWindow()
    {
        this.InitializeComponent();
        ConfigureWindow();
        StartFallbackSpinner();
        _ = LoadBoxLoaderAsync();
    }

    private async System.Threading.Tasks.Task LoadBoxLoaderAsync()
    {
        if (await BoxLoader.LoadAsync(SplashBoxLoaderWebView, SplashFallbackSpinner))
        {
            _spinStoryboard?.Stop();
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                // Borde fino SIN barra de título: Windows dibuja la sombra nativa y las esquinas redondeadas.
                presenter.SetBorderAndTitleBar(true, false);
            }

            int round = 2;
            DwmSetWindowAttribute(hWnd, 33, ref round, sizeof(int));

            appWindow.Resize(new SizeInt32(SplashWidth, SplashHeight));

            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var work = displayArea.WorkArea;
                appWindow.Move(new PointInt32(
                    work.X + (work.Width - SplashWidth) / 2,
                    work.Y + (work.Height - SplashHeight) / 2));
            }
        }
        catch (Exception ex)
        {
            App.LogError("Splash ConfigureWindow", ex);
        }
    }

    private void StartFallbackSpinner()
    {
        try
        {
            var anim = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new Duration(TimeSpan.FromSeconds(0.85)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim, SplashSpinnerRotate);
            Storyboard.SetTargetProperty(anim, "Angle");

            _spinStoryboard = new Storyboard();
            _spinStoryboard.Children.Add(anim);
            _spinStoryboard.Begin();
        }
        catch { }
    }

    public void SetStatusText(string text)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            SplashStatusText.Text = text;
        });
    }
}
