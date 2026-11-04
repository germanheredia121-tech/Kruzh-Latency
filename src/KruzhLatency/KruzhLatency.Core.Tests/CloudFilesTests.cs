using KruzhLatency.Core.Debloat;

namespace KruzhLatency.Core.Tests;

public class CloudFilesTests
{
    [Theory]
    [InlineData(0x00400000 | 0x20, true)]   // RECALL_ON_DATA_ACCESS: placeholder de OneDrive (solo en la nube)
    [InlineData(0x00040000, true)]          // RECALL_ON_OPEN
    [InlineData(0x00001000, true)]          // OFFLINE
    [InlineData(0x00080000 | 0x20, false)]  // PINNED + ARCHIVE: "Mantener siempre en este dispositivo"
    [InlineData(0x20, false)]               // archivo normal
    public void SoloNube_SegunAtributos(int attributes, bool expected) =>
        Assert.Equal(expected, CloudFiles.IsCloudOnly((FileAttributes)attributes));
}
