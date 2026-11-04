using KruzhLatency.Core.Network;

namespace KruzhLatency.Core.Tests;

/// <summary>B4: plan por chip contra las opciones reales (Ndi\Params) de la RTL8125 y la RTL8111 del autor.</summary>
public class NicTuningPlannerTests
{
    private static NicAdvancedProperties Load(string file) =>
        NicAdvancedProperties.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));

    private static Dictionary<string, NicSettingChange> PlanFor(string file, int cores = 6) =>
        NicTuningPlanner.Plan(Load(file), cores)!.Changes.ToDictionary(c => c.Keyword, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Rtl8125_ValoresDelChat_ValidadosContraElDriver()
    {
        var nic = Load("ndi-params-rtl8125.json");
        var plan = NicTuningPlanner.Plan(nic, physicalCores: 6)!;
        var c = plan.Changes.ToDictionary(x => x.Keyword);

        Assert.Equal("Realtek RTL8125 (2.5GbE)", plan.Chip.Name);
        Assert.Equal("0", c["*InterruptModeration"].Target);
        Assert.Equal("512", c["*ReceiveBuffers"].Target);
        Assert.Equal("128", c["*TransmitBuffers"].Target);
        Assert.Equal("2", c["*NumRssQueues"].Target);       // E2: decisión del autor, 2 colas
        Assert.Equal("0", c["*SpeedDuplex"].Target);       // spec 002: negociada
        Assert.Equal("6", c["*SpeedDuplex"].Current);      // hoy está fija en 1 Gbps
        foreach (var k in new[] { "*FlowControl", "*EEE", "*LsoV2IPv4", "*LsoV2IPv6", "AdvancedEEE", "EnableGreenEthernet", "GigaLite", "PowerSavingMode" })
        {
            Assert.Equal("0", c[k].Target);
        }
        // Todo lo que se escribe existe en Ndi\Params y es un valor aceptado.
        Assert.All(plan.Changes, ch => Assert.NotNull(NicTuningPlanner.Validate(nic.Get(ch.Keyword)!, ch.Target)));
        Assert.DoesNotContain(plan.Changes, ch => ch.Keyword == "NetworkAddress");
        Assert.DoesNotContain(plan.Changes, ch => ch.Keyword == "ULPMode"); // keyword de Intel: no existe en Realtek
    }

    [Fact]
    public void Rtl8111_RespetaLosMaximosDelDriver()
    {
        var c = PlanFor("ndi-params-rtl8111.json");

        Assert.Equal("512", c["*ReceiveBuffers"].Target);   // máximo del driver = 512
        Assert.Equal("128", c["*TransmitBuffers"].Target);  // máximo del driver = 128
        Assert.True(c["*TransmitBuffers"].IsAlreadySet);
        Assert.False(c["GigaLite"].IsAlreadySet);           // hoy está activado
    }

    [Fact]
    public void ColasRss_NoSuperanLosNucleosFisicos()
    {
        Assert.Equal("2", PlanFor("ndi-params-rtl8125.json", cores: 2)["*NumRssQueues"].Target);
        Assert.Equal("1", PlanFor("ndi-params-rtl8125.json", cores: 1)["*NumRssQueues"].Target);
        Assert.Equal("2", PlanFor("ndi-params-rtl8125.json", cores: 16)["*NumRssQueues"].Target);
    }

    [Theory]
    [InlineData("ndi-params-rtl8125.json")]
    [InlineData("ndi-params-rtl8111.json")]
    public void E1_EnergiaYWakeOnLan_Apagados(string file)
    {
        var c = PlanFor(file);

        foreach (var k in new[] { "*PMARPOffload", "*PMNSOffload", "*WakeOnMagicPacket", "*WakeOnPattern", "S5WakeOnLan" })
        {
            Assert.Equal("0", c[k].Target);
            Assert.False(c[k].IsAlreadySet); // hoy están activados
        }
        Assert.Equal("2", c["WolShutdownLinkSpeed"].Target); // Not Speed Down
        Assert.True(c["*ModernStandbyWoLMagicPacket"].IsAlreadySet);
        // El driver no declara estas opciones: no se escriben a ciegas.
        Assert.False(c.ContainsKey("PowerDownPll"));
        Assert.False(c.ContainsKey("EEE"));
    }

    [Fact]
    public void ChipDesconocido_SinPlan()
    {
        var nic = Load("ndi-params-rtl8125.json") with { ComponentId = @"PCI\VEN_14E4&DEV_1682" }; // Broadcom

        Assert.Null(NicTuningPlanner.Plan(nic, 6));
    }

    [Fact]
    public void ValorQueElDriverNoAcepta_SeOmite()
    {
        var nic = Load("ndi-params-rtl8125.json");
        var restricted = nic with
        {
            Params = nic.Params.Select(p => p.Keyword == "*FlowControl"
                ? p with { Enum = new Dictionary<string, string> { ["3"] = "Rx & Tx Enabled" } }
                : p).ToList()
        };

        Assert.DoesNotContain(NicTuningPlanner.Plan(restricted, 6)!.Changes, c => c.Keyword == "*FlowControl");
    }

    [Theory]
    [InlineData("128", "64", "4096", "8", "128")]
    [InlineData("32", "64", "4096", "8", "64")]     // debajo del mínimo → mínimo
    [InlineData("9000", "64", "4096", "8", "4096")] // arriba del máximo → máximo
    [InlineData("130", "80", "4096", "8", "128")]   // alinea al paso desde el mínimo
    public void Enteros_ClampYPaso(string target, string min, string max, string step, string expected)
    {
        var p = new NdiParam("*TransmitBuffers", null, "int", "512", min, max, step, null, "512");

        Assert.Equal(expected, NicTuningPlanner.Validate(p, target));
    }

    [Fact]
    public void Intel_I226_UsaKeywordsDeIntel()
    {
        var nic = new NicAdvancedProperties("Intel(R) Ethernet Controller I226-V", "2.1", @"PCI\VEN_8086&DEV_125C&SUBSYS_00001043", new[]
        {
            new NdiParam("*InterruptModeration", null, "enum", "1", null, null, null, new Dictionary<string, string> { ["0"] = "Off", ["1"] = "On" }, "1"),
            new NdiParam("EEELinkAdvertisement", null, "enum", "1", null, null, null, new Dictionary<string, string> { ["0"] = "Off", ["1"] = "On" }, "1"),
            new NdiParam("*ReceiveBuffers", null, "int", "256", "80", "4096", "8", null, "256"),
        });
        var c = NicTuningPlanner.Plan(nic, 8)!.Changes.ToDictionary(x => x.Keyword);

        Assert.Equal(NicChipFamily.Intel, NicTuningPlanner.Identify(nic.ComponentId)!.Family);
        Assert.Equal("0", c["EEELinkAdvertisement"].Target);
        Assert.False(c.ContainsKey("*ReceiveBuffers")); // los buffers de Intel no se tocan (sin fuente)
    }
}
