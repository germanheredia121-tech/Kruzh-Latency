namespace KruzhLatency.Core.Latency;

/// <summary>
/// ADR-010 / E12: qué pide el modo residente del timer y cuándo. Solo detecta los juegos por nombre de proceso
/// (ADR-004: nunca se abre, lee ni inyecta nada en el juego).
/// </summary>
public static class TimerResidentPolicy
{
    /// <summary>Nombres de proceso sin ".exe" (CS2 y VALORANT).</summary>
    public static readonly IReadOnlyList<string> GameProcesses = new[] { "cs2", "VALORANT-Win64-Shipping" };

    /// <summary>0,5 ms en unidades de 100 ns (lo que recibe NtSetTimerResolution).</summary>
    public const uint ResolutionHundredNs = 5000;

    /// <summary>Cada cuánto se mira si abrió un juego (espera larga: el residente no genera carga).</summary>
    public static readonly TimeSpan GameCheckInterval = TimeSpan.FromSeconds(5);

    public const string AgentExeName = "KruzhLatency.TimerAgent.exe";
    public const string TaskPath = @"\KRUZH\Timer 0.5 ms";

    public static bool IsGame(string processName) =>
        GameProcesses.Any(g => string.Equals(g, processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// XML de la tarea programada: al iniciar sesión del usuario, sin elevar, sin límite de tiempo, una sola instancia,
    /// también con batería. <paramref name="userId"/> = DOMINIO\usuario.
    /// </summary>
    public static string TaskXml(string agentPath, string userId)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>KRUZH LATENCY</Author>
                <Description>Pide 0,5 ms de resolución de timer solo mientras CS2 o VALORANT están abiertos (ADR-010).</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{X(userId)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{X(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{X(agentPath)}"</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
