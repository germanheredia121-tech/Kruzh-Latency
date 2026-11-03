using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using KruzhLatency.Services;

namespace KruzhLatency;

public partial class App : Application
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static Window? MainAppWindow { get; private set; }

    public static TransactionalAuditService AuditService { get; private set; } = null!;
    public static AntiTamperAndLicenseService LicenseService { get; private set; } = null!;
    public static HardwareChassisService HardwareService { get; private set; } = null!;
    public static IrqAndMsiService IrqMsiService { get; private set; } = null!;
    public static PowerCatalogService PowerService { get; private set; } = null!;
    public static DeclarativeTweakEngine TweakEngine { get; private set; } = null!;
    public static GameOptimizationService GameOptimizer { get; private set; } = null!;
    public static NvidiaAndDriversService NvidiaDriversService { get; private set; } = null!;
    public static NvramAdvisoryService NvramService { get; private set; } = null!;
    public static RealtimeMonitorAndScoreService MonitorScoreService { get; private set; } = null!;

    public App()
    {
        this.UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        Controls.BoxLoader.ConfigureUserDataFolder();
        this.InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            // 1. Mostrar Splash INMEDIATO (< 50ms)
            var splash = new SplashWindow();
            splash.Activate();

            // 2. Ejecutar detección pesada en hilo secundario (async)
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    splash.SetStatusText("Iniciando servicios de auditoría...");
                    AuditService = new TransactionalAuditService();
                    LicenseService = new AntiTamperAndLicenseService();

                    splash.SetStatusText("Detectando perfil de hardware...");
                    HardwareService = new HardwareChassisService();
                    HardwareService.DetectHardwareProfile();

                    splash.SetStatusText("Analizando controladores e interrupciones PCI...");
                    IrqMsiService = new IrqAndMsiService(AuditService, HardwareService);
                    PowerService = new PowerCatalogService(HardwareService);

                    splash.SetStatusText("Cargando catálogo de optimizaciones...");
                    TweakEngine = new DeclarativeTweakEngine(AuditService, HardwareService, IrqMsiService, PowerService);
                    TweakEngine.RefreshAll();
                    GameOptimizer = new GameOptimizationService(AuditService);

                    splash.SetStatusText("Verificando firmware y telemetría...");
                    NvidiaDriversService = new NvidiaAndDriversService(HardwareService, AuditService);
                    NvramService = new NvramAdvisoryService(HardwareService);
                    MonitorScoreService = new RealtimeMonitorAndScoreService();

                    // 3. Cuando termina la inicialización, abrir MainWindow y cerrar Splash
                    splash.DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            MainAppWindow = new MainWindow();
                            MainAppWindow.Activate();
                            splash.Close();
                        }
                        catch (Exception ex)
                        {
                            LogAndDisplayFatalError("MainWindow Initialization Error", ex);
                        }
                    });
                }
                catch (Exception ex)
                {
                    splash.DispatcherQueue.TryEnqueue(() =>
                    {
                        LogAndDisplayFatalError("Background Initialization Error", ex);
                        splash.Close();
                    });
                }
            });
        }
        catch (Exception ex)
        {
            LogAndDisplayFatalError("OnLaunched Error", ex);
        }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogAndDisplayFatalError("UI UnhandledException", e.Exception);
    }

    private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogAndDisplayFatalError("AppDomain UnhandledException", ex);
        }
    }

    /// <summary>Registra un error no fatal en el log de diagnóstico, sin mostrar MessageBox.</summary>
    public static void LogError(string context, Exception ex)
    {
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KruzhLatency");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "errors.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}]\n{ex}\n\n");
        }
        catch { }
    }

    public static void LogAndDisplayFatalError(string context, Exception ex)
    {
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KruzhLatency");
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "startup_diagnostic.log");
            string details = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}]\n{ex}\n\n";
            File.AppendAllText(logPath, details);
            MessageBoxW(IntPtr.Zero, $"{context}:\n{ex.Message}\n\nDetalle guardado en:\n{logPath}", "KRUZH LATENCY — Diagnóstico", 0x10);
        }
        catch { }
    }
}
