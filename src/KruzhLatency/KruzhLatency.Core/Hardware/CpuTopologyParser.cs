using System.Buffers.Binary;

namespace KruzhLatency.Core.Hardware;

/// <summary>
/// Interpreta los buffers de GetLogicalProcessorInformationEx (RelationProcessorCore = 0 y RelationCache = 2)
/// sin P/Invoke: función pura, testeable con buffers grabados de CPUs reales.
///
/// Layout (x64) de SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX:
///   +0  Relationship (int32)   +4  Size (uint32)   +8  unión
/// PROCESSOR_RELATIONSHIP (desde +8):
///   +8 Flags (byte, LTP_PC_SMT = 1)  +9 EfficiencyClass (byte)  +10 Reserved[20]  +30 GroupCount (ushort)
///   +32 GroupMask[0].Mask (KAFFINITY, 8 bytes)   +40 GroupMask[0].Group (ushort)
/// CACHE_RELATIONSHIP (desde +8):
///   +8 Level (byte)  +9 Associativity  +10 LineSize (ushort)  +12 CacheSize (uint)  +16 Type (int)
///   +20 Reserved[18]  +38 GroupCount (ushort)  +40 GroupMask[0].Mask  +48 GroupMask[0].Group
///
/// Bug B3 (v3): se leía la máscara del núcleo en +40 (que es el campo Group) en vez de +32 → todas las máscaras 0.
/// </summary>
public static class CpuTopologyParser
{
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int ProcessorMaskOffset = 32;
    private const int ProcessorGroupCountOffset = 30;
    private const int ProcessorGroupSize = 16; // GROUP_AFFINITY: Mask(8) + Group(2) + Reserved[3](6)
    private const int CacheLevelOffset = 8;
    private const int CacheGroupCountOffset = 38;
    private const int CacheMaskOffset = 40;
    private const int CacheSizeOffset = 12;

    public static IReadOnlyList<LogicalProcessor> Parse(ReadOnlySpan<byte> coreBuffer, ReadOnlySpan<byte> cacheBuffer)
    {
        var cores = new List<(List<int> Threads, byte Efficiency)>();
        foreach (var rec in Records(coreBuffer, RelationProcessorCore))
        {
            byte efficiency = rec[9];
            int groupCount = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(ProcessorGroupCountOffset, 2));
            cores.Add((ReadGroupMasks(rec, ProcessorMaskOffset, Math.Max(1, groupCount)), efficiency));
        }

        // Grupos de L3: cada caché de nivel 3 distinta = un CCD (Ryzen multi-CCD) o el L3 compartido único.
        var l3Groups = new List<HashSet<int>>();
        var l3Sizes = new List<int>();
        foreach (var rec in Records(cacheBuffer, RelationCache))
        {
            if (rec[CacheLevelOffset] != 3) continue;
            int groupCount = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(CacheGroupCountOffset, 2));
            var threads = ReadGroupMasks(rec, CacheMaskOffset, Math.Max(1, groupCount)).ToHashSet();
            if (threads.Count > 0 && !l3Groups.Any(g => g.SetEquals(threads)))
            {
                l3Groups.Add(threads);
                l3Sizes.Add((int)(BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(CacheSizeOffset, 4)) / 1024));
            }
        }

        if (cores.Count == 0) return Array.Empty<LogicalProcessor>();

        byte minEff = cores.Min(c => c.Efficiency);
        byte maxEff = cores.Max(c => c.Efficiency);
        bool hybrid = maxEff > minEff; // en CPUs híbridas los P-cores tienen EfficiencyClass mayor

        var result = new List<LogicalProcessor>();
        for (int coreIndex = 0; coreIndex < cores.Count; coreIndex++)
        {
            var (threads, eff) = cores[coreIndex];
            bool isECore = hybrid && eff == minEff;
            var ordered = threads.OrderBy(t => t).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int thread = ordered[i];
                int l3 = l3Groups.FindIndex(g => g.Contains(thread));
                result.Add(new LogicalProcessor(
                    Index: thread,
                    PhysicalCoreIndex: coreIndex,
                    IsEfficiencyCore: isECore,
                    IsSmtSibling: i > 0,
                    L3GroupIndex: Math.Max(0, l3),
                    L3SizeKb: l3 >= 0 ? l3Sizes[l3] : 0));
            }
        }
        return result.OrderBy(p => p.Index).ToList();
    }

    /// <summary>Máscara de los hilos que conviene reservar para fondo: E-cores (Intel híbrido) o CCDs que no son el 0.</summary>
    public static ulong BackgroundReserveMask(IReadOnlyList<LogicalProcessor> topology)
    {
        bool hybrid = topology.Any(t => t.IsEfficiencyCore);
        bool multiL3 = topology.Select(t => t.L3GroupIndex).Distinct().Count() > 1;
        ulong mask = 0;
        foreach (var t in topology.Where(t => t.Index < 64))
        {
            if ((hybrid && t.IsEfficiencyCore) || (!hybrid && multiL3 && t.L3GroupIndex > 0))
            {
                mask |= 1UL << t.Index;
            }
        }
        return mask;
    }

    private static List<int> ReadGroupMasks(ReadOnlySpan<byte> rec, int firstMaskOffset, int groupCount)
    {
        var threads = new List<int>();
        for (int g = 0; g < groupCount; g++)
        {
            int maskOffset = firstMaskOffset + g * ProcessorGroupSize;
            if (maskOffset + 10 > rec.Length) break;
            ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(rec.Slice(maskOffset, 8));
            int group = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(maskOffset + 8, 2));
            for (int bit = 0; bit < 64; bit++)
            {
                if ((mask & (1UL << bit)) != 0) threads.Add(group * 64 + bit);
            }
        }
        return threads;
    }

    /// <summary>Recorre los registros de tamaño variable devolviendo los de la relación pedida.</summary>
    private static List<byte[]> Records(ReadOnlySpan<byte> buffer, int relationship)
    {
        var list = new List<byte[]>();
        int offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            int rel = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 4, 4));
            if (size <= 0 || offset + size > buffer.Length) break;
            if (rel == relationship) list.Add(buffer.Slice(offset, size).ToArray());
            offset += size;
        }
        return list;
    }
}
