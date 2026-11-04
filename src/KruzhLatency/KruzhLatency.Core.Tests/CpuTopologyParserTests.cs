using System.Buffers.Binary;
using KruzhLatency.Core.Hardware;

namespace KruzhLatency.Core.Tests;

public class CpuTopologyParserTests
{
    private static byte[] Fixture(string name) =>
        Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    // ------------------------------------------------------------ buffer REAL (Ryzen 5 5600X del autor)

    [Fact]
    public void Ryzen5600X_Real_6Nucleos12Hilos_UnSoloL3_SinECores()
    {
        var topo = CpuTopologyParser.Parse(Fixture("topology-ryzen5600x-core.b64"), Fixture("topology-ryzen5600x-cache.b64"));

        Assert.Equal(12, topo.Count);
        Assert.Equal(6, topo.Select(t => t.PhysicalCoreIndex).Distinct().Count());
        Assert.All(topo, t => Assert.False(t.IsEfficiencyCore));
        Assert.Single(topo.Select(t => t.L3GroupIndex).Distinct());
        Assert.All(topo, t => Assert.Equal(32 * 1024, t.L3SizeKb)); // 32 MB de L3 (E7: así se reconoce el CCD con V-Cache)
        Assert.Equal(6, topo.Count(t => t.IsSmtSibling));
        // B3: con el offset viejo (+40) la topología salía vacía; con +32 el hilo 0 y 1 son el núcleo 0.
        Assert.Equal(topo.Single(t => t.Index == 0).PhysicalCoreIndex, topo.Single(t => t.Index == 1).PhysicalCoreIndex);
        Assert.Equal(0UL, CpuTopologyParser.BackgroundReserveMask(topo)); // single-CCD: no se reserva nada
    }

    // ------------------------------------------------------------ buffers sintéticos (mismo layout de Windows)

    [Fact]
    public void IntelHibrido_13700K_ECoresDetectados_YReservados()
    {
        // 8 P-cores con SMT (hilos 0-15, EfficiencyClass 1) + 8 E-cores sin SMT (hilos 16-23, EfficiencyClass 0)
        var cores = new List<(ulong Mask, byte Eff)>();
        for (int i = 0; i < 8; i++) cores.Add((0b11UL << (i * 2), 1));
        for (int i = 0; i < 8; i++) cores.Add((1UL << (16 + i), 0));
        var topo = CpuTopologyParser.Parse(BuildCoreBuffer(cores), BuildCacheBuffer(new[] { (1UL << 24) - 1 }));

        Assert.Equal(24, topo.Count);
        Assert.Equal(8, topo.Count(t => t.IsEfficiencyCore));
        Assert.All(topo.Where(t => t.Index >= 16), t => Assert.True(t.IsEfficiencyCore));
        Assert.Equal(0xFF0000UL, CpuTopologyParser.BackgroundReserveMask(topo));
    }

    [Fact]
    public void Ryzen7950X3D_DosCcd_SegundoCcdReservado()
    {
        // 16 núcleos con SMT (32 hilos); L3 #0 = hilos 0-15, L3 #1 = hilos 16-31
        var cores = Enumerable.Range(0, 16).Select(i => (0b11UL << (i * 2), (byte)0)).ToList();
        var topo = CpuTopologyParser.Parse(BuildCoreBuffer(cores), BuildCacheBuffer(new[] { 0xFFFFUL, 0xFFFF0000UL }));

        Assert.Equal(32, topo.Count);
        Assert.Equal(2, topo.Select(t => t.L3GroupIndex).Distinct().Count());
        Assert.All(topo.Where(t => t.Index < 16), t => Assert.Equal(0, t.L3GroupIndex));
        Assert.All(topo.Where(t => t.Index >= 16), t => Assert.Equal(1, t.L3GroupIndex));
        Assert.Equal(0xFFFF0000UL, CpuTopologyParser.BackgroundReserveMask(topo));
    }

    [Fact]
    public void BufferVacioOCortado_NoLanza()
    {
        Assert.Empty(CpuTopologyParser.Parse(Array.Empty<byte>(), Array.Empty<byte>()));
        Assert.Empty(CpuTopologyParser.Parse(new byte[] { 0, 0, 0, 0, 99, 0, 0, 0 }, Array.Empty<byte>()));
    }

    // ------------------------------------------------------------ builders con el layout real

    private static byte[] BuildCoreBuffer(IEnumerable<(ulong Mask, byte Eff)> cores)
    {
        var ms = new MemoryStream();
        foreach (var (mask, eff) in cores)
        {
            var rec = new byte[48];
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(0), 0);   // RelationProcessorCore
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), 48);
            rec[8] = (byte)(BitOperations(mask) > 1 ? 1 : 0);             // LTP_PC_SMT
            rec[9] = eff;
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(30), 1);  // GroupCount
            BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(32), mask);
            ms.Write(rec);
        }
        return ms.ToArray();
    }

    private static byte[] BuildCacheBuffer(IEnumerable<ulong> l3Masks)
    {
        var ms = new MemoryStream();
        foreach (var mask in l3Masks)
        {
            var rec = new byte[56];
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(0), 2);   // RelationCache
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), 56);
            rec[8] = 3;                                                   // Level
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(38), 1);  // GroupCount
            BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(40), mask);
            ms.Write(rec);
        }
        return ms.ToArray();
    }

    private static int BitOperations(ulong v) => System.Numerics.BitOperations.PopCount(v);
}
