using System.Management;
using KruzhLatency.Core.Abstractions;

namespace KruzhLatency.Infra.Wmi;

/// <summary>
/// Punto de restauración vía root\default:SystemRestore.CreateRestorePoint con el resultado REAL:
/// distingue creado, salteado por el límite de frecuencia de Windows (devuelve 0 pero no crea nada),
/// Protección del sistema apagada y error. No toca SystemRestorePointCreationFrequency.
/// </summary>
public sealed class WmiRestorePoint : IRestorePoint
{
    private const uint ModifySettings = 12;      // RestorePointType
    private const uint BeginSystemChange = 100;  // EventType
    private const uint ErrorServiceDisabled = 1058;
    private const uint ErrorServiceDisabledHr = 0x80070422;

    private static readonly ManagementScope Scope = new(@"\\.\root\default");

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default) =>
        Task.Run(() => Create(description), cancellationToken);

    private static RestorePointResult Create(string description)
    {
        try
        {
            uint before = LatestSequenceNumber();

            using var cls = new ManagementClass(Scope, new ManagementPath("SystemRestore"), null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description.Length > 200 ? description[..200] : description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            uint code = Convert.ToUInt32(output?["ReturnValue"] ?? uint.MaxValue);

            if (code is ErrorServiceDisabled or ErrorServiceDisabledHr)
            {
                return new(RestorePointOutcome.ProtectionDisabled, "Protección del sistema está desactivada en el disco del sistema.");
            }
            if (code != 0)
            {
                return new(RestorePointOutcome.Failed, $"SystemRestore.CreateRestorePoint devolvió 0x{code:X}.");
            }

            uint after = LatestSequenceNumber();
            return after > before
                ? new(RestorePointOutcome.Created, $"Punto de restauración #{after} creado.")
                : new(RestorePointOutcome.SkippedFrequencyLimit, "Windows ya creó un punto en las últimas 24 h y no hizo otro.");
        }
        catch (ManagementException ex)
        {
            return new(RestorePointOutcome.Failed, $"WMI: {ex.ErrorCode} {ex.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            return new(RestorePointOutcome.Failed, "Hace falta ejecutar como administrador.");
        }
    }

    private static uint LatestSequenceNumber()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT SequenceNumber FROM SystemRestore"));
            uint max = 0;
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    max = Math.Max(max, Convert.ToUInt32(o["SequenceNumber"]));
                }
            }
            return max;
        }
        catch (ManagementException)
        {
            return 0; // sin puntos o protección apagada
        }
    }
}
