using KruzhLatency.Infra.Network;

namespace KruzhLatency.Infra.Tests;

/// <summary>E3/E6: lecturas reales por WMI (root\StandardCimv2). Solo lectura salvo el ida y vuelta, que restaura el valor.</summary>
[Trait("Category", "Integration")]
public class NetStackStoreTests
{
    [IntegrationFact]
    public void OffloadGlobal_SeLeeComoNumero()
    {
        Assert.InRange(NetTcpGlobalStore.Read(NetTcpGlobalStore.OffloadClass, "ReceiveSegmentCoalescing")!.Value, 0u, 1u);
        Assert.InRange(NetTcpGlobalStore.Read(NetTcpGlobalStore.OffloadClass, "ReceiveSideScaling")!.Value, 0u, 1u);
    }

    [IntegrationFact]
    public void PlantillaInternet_TieneAutoTuning()
    {
        Assert.Contains("Internet", NetTcpGlobalStore.TcpTemplates("AutoTuningLevelLocal"));
        Assert.InRange(NetTcpGlobalStore.Read(NetTcpGlobalStore.Target(NetTcpGlobalStore.TcpClass, "Internet"), "AutoTuningLevelLocal")!.Value, 0u, 4u);
        Assert.Null(NetTcpGlobalStore.Read(NetTcpGlobalStore.Target(NetTcpGlobalStore.TcpClass, "NoExiste"), "AutoTuningLevelLocal"));
    }

    [IntegrationFact]
    public void Rsc_IdaYVuelta_RestauraElValor()
    {
        const string prop = "ReceiveSegmentCoalescing";
        uint original = NetTcpGlobalStore.Read(NetTcpGlobalStore.OffloadClass, prop)!.Value;
        try
        {
            NetTcpGlobalStore.Write(NetTcpGlobalStore.OffloadClass, prop, original == 0 ? 1u : 0u);
        }
        finally
        {
            NetTcpGlobalStore.Write(NetTcpGlobalStore.OffloadClass, prop, original);
        }
        Assert.Equal(original, NetTcpGlobalStore.Read(NetTcpGlobalStore.OffloadClass, prop));
    }

    [IntegrationFact]
    public void TimerResolution_SeLee_EntreLaMasFinaYLaMasGruesa()
    {
        var r = KruzhLatency.Infra.Timer.TimerResolution.Query()!.Value;
        Assert.InRange(r.Current, r.Finest, r.Coarsest);
        Assert.True(r.Finest <= 5000u); // Windows ofrece 0,5 ms o menos
    }

    [IntegrationFact]
    public void Bindings_PlacasFisicasConTcpip()
    {
        var adapters = NetAdapterBindingStore.PhysicalAdapters();
        Assert.NotEmpty(adapters);
        Assert.All(adapters, a => Assert.NotNull(NetAdapterBindingStore.IsEnabled(a.Name, "ms_tcpip")));
        Assert.Null(NetAdapterBindingStore.IsEnabled(adapters[0].Name, "ms_no_existe"));
    }
}
