using KruzhLatency.Core.Latency;

namespace KruzhLatency.Core.Tests;

public class InterruptDriversTests
{
    [Fact]
    public void Usb_IncluyeWdf_PorqueUsbxhciEsKmdf()
    {
        var d = InterruptDrivers.For("USB xHCI", "USBXHCI.SYS");
        Assert.Contains("Wdf01000.sys", d);
        Assert.Single(d, x => x.Equals("USBXHCI.SYS", StringComparison.OrdinalIgnoreCase)); // sin duplicados
    }

    [Fact]
    public void RedRealtekNdis_IncluyeNdis() =>
        Assert.Contains("ndis.sys", InterruptDrivers.For("Red", "rt640x64.sys"));

    [Fact]
    public void Gpu_IncluyeDxgkrnl()
    {
        var d = InterruptDrivers.For("GPU", "nvlddmkm.sys");
        Assert.Equal(new[] { "nvlddmkm.sys", "dxgkrnl.sys" }, d);
    }

    [Fact]
    public void SinImagen_SoloLosDelTipo() =>
        Assert.Equal(new[] { "stornvme.sys", "storport.sys" }, InterruptDrivers.For("NVMe", null));
}
