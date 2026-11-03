using System;
using System.Collections.Generic;
using KruzhLatency.Core.Abstractions;
using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Network;
using KruzhLatency.Infra.Devices;
using KruzhLatency.Infra.Network;
using KruzhLatency.Infra.Wmi;

namespace KruzhLatency.Services;

/// <summary>
/// Cachés cortas de las lecturas caras que hacen los ReadLiveState (inventario PnP, análisis de red por WMI, BCD por WMI).
/// Un refresco completo las pedía decenas de veces; ahora cada una se lee una vez por ventana de tiempo.
/// Se invalidan al aplicar o revertir para que el ítem muestre enseguida su estado nuevo.
/// </summary>
public sealed partial class DeclarativeTweakEngine
{
    private static readonly object CacheLock = new();
    private static (DeviceTree Tree, DateTime At)? _treeCache;
    private static (IReadOnlyList<NetworkAdvice> Advice, DateTime At)? _netAdviceCache;
    private static readonly Dictionary<BcdElement, (BcdValue? Value, DateTime At)> BcdCache = new();

    private static readonly TimeSpan TreeTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NetAdviceTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BcdTtl = TimeSpan.FromSeconds(5);

    public static void InvalidateReadCaches()
    {
        lock (CacheLock)
        {
            _treeCache = null;
            _netAdviceCache = null;
            BcdCache.Clear();
        }
    }

    private static DeviceTree CachedDeviceTree()
    {
        lock (CacheLock)
        {
            if (_treeCache is { } c && DateTime.UtcNow - c.At < TreeTtl) return c.Tree;
        }
        var tree = new DeviceTree(new CfgMgrDeviceInventory().Snapshot());
        lock (CacheLock) _treeCache = (tree, DateTime.UtcNow);
        return tree;
    }

    private static IReadOnlyList<NetworkAdvice> CachedNetworkAdvice()
    {
        lock (CacheLock)
        {
            if (_netAdviceCache is { } c && DateTime.UtcNow - c.At < NetAdviceTtl) return c.Advice;
        }
        var advice = NetworkStackAdvisor.Evaluate(NetworkStackProbe.Detect());
        lock (CacheLock) _netAdviceCache = (advice, DateTime.UtcNow);
        return advice;
    }

    /// <summary>Lee un elemento de {current} por WMI (null = no definido o sin permisos), con caché de 5 s.</summary>
    private static BcdValue? ReadBcd(BcdElement element)
    {
        lock (CacheLock)
        {
            if (BcdCache.TryGetValue(element, out var c) && DateTime.UtcNow - c.At < BcdTtl) return c.Value;
        }
        BcdValue? value;
        try
        {
            value = new WmiBcdStore().Read(element);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            value = null;
        }
        lock (CacheLock) BcdCache[element] = (value, DateTime.UtcNow);
        return value;
    }
}
