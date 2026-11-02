using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace KruzhLatency;

public sealed partial class SplashWindow : Window
{
    private Storyboard? _spinStoryboard;

    public SplashWindow()
    {
        this.InitializeComponent();
        ConfigureWindow();
        StartAnimation();
        InitializeBoxLoaderAsync();
    }

    private async void InitializeBoxLoaderAsync()
    {
        try
        {
            await SplashBoxLoaderWebView.EnsureCoreWebView2Async();
            SplashBoxLoaderWebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

            string htmlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "box-loader.html");
            if (System.IO.File.Exists(htmlPath))
            {
                SplashBoxLoaderWebView.CoreWebView2.Navigate(htmlPath);
                SplashFallbackSpinner.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // Fallback spinner remains active if WebView2 is not initialized
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
                presenter.SetBorderAndTitleBar(false, false);
            }

            int width = 420;
            int height = 300;
            appWindow.Resize(new SizeInt32(width, height));

            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var centerPoint = new PointInt32(
                    (displayArea.WorkArea.Width - width) / 2,
                    (displayArea.WorkArea.Height - height) / 2);
                appWindow.Move(centerPoint);
            }
        }
        catch { }
    }

    private void StartAnimation()
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

