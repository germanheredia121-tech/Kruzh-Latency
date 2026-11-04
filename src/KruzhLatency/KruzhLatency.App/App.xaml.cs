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

    /// <summary>Una sola instancia: abrirla dos veces duplicaba sesiones ETW y escrituras al sistema.</summary>
    private static System.Threading.Mutex? _singleInstance;

    /// <summary>
    /// Cierra KRUZH y lo vuelve a abrir con una ventana nueva. Se usa después de reiniciar el driver de video: WinUI no siempre
    /// se recupera y la ventana queda en blanco. Llamar desde el hilo de UI (dueño del mutex de instancia única).
    /// </summary>
    public static void RelaunchAndExit()
    {
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        _singleInstance = null;
        if (Environment.ProcessPath is { } exe) KruzhLatency.Infra.Benchmark.SelfRelaunch.Start(exe);
        Current.Exit();
    }

    /// <summary>true cuando la ventana principal ya está visible; antes de eso cualquier error fatal cierra el proceso.</summary>
    private static volatile bool _startupCompleted;

    private static bool AcquireSingleInstance()
    {
        _singleInstance = new System.Threading.Mutex(true, @"Local\KruzhLatency.SingleInstance", out bool createdNew);
        if (createdNew) return true;

        // Ya hay una abierta (quizás detrás de otras ventanas): se la trae al frente y esta se cierra.
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            using (p)
            {
                if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                {
                    Controls.WindowReveal.BringToFront(p.MainWindowHandle);
                    break;
                }
            }
        }
        return false;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            if (!AcquireSingleInstance())
            {
                Exit();
                return;
            }

            // 1. Splash inmediato, sin flash: se ve recién cuando XAML ya pintó (WindowReveal).
            var splash = new SplashWindow();
            Controls.WindowReveal.ActivateWithoutFlash(splash);

            // 2. Ejecutar detección pesada en hilo secundario (async)
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    splash.SetStatusText("Iniciando servicios de auditoría...");
                    AuditService = new TransactionalAuditService();
                    // B2: limpiar entradas "*_Rev" de v3 (revertirlas re-aplicaba tweaks).
                    AuditService.DiscardEntries(TransactionalAuditService.IsLegacyFallbackEntry);
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
                            // La principal se abre encubierta y al frente; el splash se cierra cuando ya se ve
                            // (antes quedaba detrás de otras ventanas y parecía que no abría).
                            Controls.WindowReveal.ActivateWithoutFlash(MainAppWindow, () =>
                            {
                                _startupCompleted = true;
                                splash.Close();
                            });
                        }
                        catch (Exception ex)
                        {
                            LogAndDisplayFatalError("MainWindow Initialization Error", ex);
                            // Sin ventana principal no hay app: cerrar para no dejar un proceso huérfano con el mutex.
                            splash.Close();
                            Current.Exit();
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

        // Si la ventana principal todavía no se mostró, no hay app usable: un error acá dejaba el proceso vivo
        // sin ninguna ventana ni ícono en la barra de tareas (y con el mutex tomado). Se cierra el proceso.
        if (!_startupCompleted)
        {
            Environment.Exit(1);
        }
    }
}
