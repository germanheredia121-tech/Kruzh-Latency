using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace KruzhLatency.Controls;

/// <summary>
/// Monta el componente Assets/box-loader.html (cajas blancas animadas, fondo negro) en un WebView2.
/// Si WebView2 no está disponible, queda visible el spinner XAML de respaldo.
/// </summary>
internal static class BoxLoader
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Carpeta de datos de WebView2 fuera de la carpeta del exe (la app corre como admin y puede estar instalada
    /// en Program Files). Debe llamarse ANTES de crear cualquier WebView2.
    /// </summary>
    public static void ConfigureUserDataFolder()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KruzhLatency", "WebView2");
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", dir);
        }
        catch (Exception ex)
        {
            App.LogError("WebView2 user data folder", ex);
        }
    }

    /// <summary>Devuelve true si el box-loader quedó visible; false si se usa el spinner de respaldo.</summary>
    public static async Task<bool> LoadAsync(WebView2 view, UIElement fallback)
    {
        try
        {
            string htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "box-loader.html");
            if (!File.Exists(htmlPath))
            {
                App.LogError("BoxLoader", new FileNotFoundException("No se encontró el componente box-loader.", htmlPath));
                return false;
            }

            // Negro, igual que el fondo del HTML y de los contenedores (splash / tarjeta del overlay): así no se ve
            // ningún recuadro aunque WebView2 no componga bien la transparencia. Se fija ANTES de inicializar (sin flash blanco).
            view.DefaultBackgroundColor = Microsoft.UI.Colors.Black;
            await view.EnsureCoreWebView2Async();

            var settings = view.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsStatusBarEnabled = false;

            // Se muestra cuando la página avisa que YA pintó las cajas (doble requestAnimationFrame en el HTML).
            // NavigationCompleted llega antes del primer frame: mostrar ahí dejaba ver un recuadro un instante.
            var painted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnMessage(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
            {
                if (args.TryGetWebMessageAsString() == "painted") painted.TrySetResult(true);
            }
            void OnCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                if (!args.IsSuccess) painted.TrySetResult(false);
            }
            view.WebMessageReceived += OnMessage;
            view.NavigationCompleted += OnCompleted;

            // Navigate() exige una URI: con una ruta "C:\..." tiraba excepción y siempre quedaba el spinner de respaldo.
            view.Source = new Uri(htmlPath);

            var winner = await Task.WhenAny(painted.Task, Task.Delay(NavigationTimeout));
            view.WebMessageReceived -= OnMessage;
            view.NavigationCompleted -= OnCompleted;
            if (winner != painted.Task || !painted.Task.Result)
            {
                ShowFallback(fallback);
                return false;
            }

            view.Opacity = 1;
            fallback.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex)
        {
            App.LogError("BoxLoader", ex);
            ShowFallback(fallback);
            return false;
        }
    }

    /// <summary>El spinner XAML arranca oculto (evita el salto spinner → cajas); solo aparece si WebView2 falla.</summary>
    private static void ShowFallback(UIElement fallback) => fallback.Visibility = Visibility.Visible;
}
