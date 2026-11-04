namespace KruzhLatency.Core.Debloat;

/// <summary>
/// Archivos de OneDrive que existen solo en la nube (Archivos a petición): si se desinstala OneDrive dejan de poder abrirse
/// desde la PC. Se reconocen por sus atributos: RECALL_ON_DATA_ACCESS (0x400000), RECALL_ON_OPEN (0x40000) u OFFLINE (0x1000).
/// </summary>
public static class CloudFiles
{
    public const int RecallOnDataAccess = 0x00400000;
    public const int RecallOnOpen = 0x00040000;
    public const int Offline = 0x00001000;

    public static bool IsCloudOnly(FileAttributes attributes)
    {
        int a = (int)attributes;
        return (a & (RecallOnDataAccess | RecallOnOpen | Offline)) != 0;
    }
}
