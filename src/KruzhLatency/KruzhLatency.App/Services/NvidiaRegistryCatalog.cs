using System;
using System.Collections.Generic;
using System.Linq;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

/// <summary>
/// E10/E11: valores de registro que lee el driver de NVIDIA (nvlddmkm). Se escriben solo en las claves de clase de
/// adaptadores NVIDIA (no en la iGPU) y en la clave del servicio; el estado se lee de las mismas ubicaciones.
/// Son claves comunitarias sin documentación de NVIDIA (ADR-002): ninguna entra en Seguro/Competitivo y las de más
/// riesgo quedan solo en Entusiasta o fuera de perfiles.
/// </summary>
public sealed partial class DeclarativeTweakEngine
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string NvService = @"SYSTEM\CurrentControlSet\Services\nvlddmkm";
    private const string NvTweak = @"SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak";

    private enum NvWhere { AdapterClass, Service, NvTweak }

    private sealed record NvValue(NvWhere Where, string Name, int Value);

    /// <summary>Claves de clase (00XX) de adaptadores NVIDIA instalados.</summary>
    private static IReadOnlyList<string> NvidiaAdapterKeys()
    {
        var list = new List<string>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var cls = hklm.OpenSubKey(DisplayClassKey);
        if (cls is null) return list;
        foreach (string sub in cls.GetSubKeyNames().Where(s => s.Length == 4 && s.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(sub);
            string provider = k?.GetValue("ProviderName") as string ?? "";
            string matching = k?.GetValue("MatchingDeviceId") as string ?? "";
            if (provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                matching.StartsWith(@"pci\ven_10de", StringComparison.OrdinalIgnoreCase))
            {
                list.Add($@"{DisplayClassKey}\{sub}");
            }
        }
        return list;
    }

    private static IEnumerable<string> PathsFor(NvWhere where) => where switch
    {
        NvWhere.AdapterClass => NvidiaAdapterKeys(),
        NvWhere.Service => new[] { NvService },
        _ => new[] { NvTweak },
    };

    private static string Describe(NvValue v) => v.Where switch
    {
        NvWhere.AdapterClass => $@"{DisplayClassKey}\<adaptador NVIDIA> -> {v.Name} = {Hex(v.Value)}",
        NvWhere.Service => $@"HKLM\{NvService} -> {v.Name} = {Hex(v.Value)}",
        _ => $@"HKLM\{NvTweak} -> {v.Name} = {Hex(v.Value)}",
    };

    private static string Hex(int v) => v > 9 ? $"0x{v:X}" : v.ToString();

    private DeclarativeTweakItem NvidiaRegistryItem(string id, string code, string title, string description, string source,
        RiskLevel risk, bool inProfesional, bool inEntusiasta, EvaluationResult evaluation, params NvValue[] values) =>
        new()
        {
            Id = id,
            CodeNumber = code,
            Title = title,
            Category = TweakCategory.GpuDisplay,
            CategoryDisplay = "GPU & Display",
            TechnicalDescription = description,
            WhatItTouches = string.Join(" · ", values.Select(Describe)),
            SourceAttribution = source,
            Risk = risk,
            RequiresReboot = true,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = inProfesional,
            InEntusiasta = inEntusiasta,
            ApplicableGate = hw => hw.GpuVendor == GpuVendorKind.Nvidia && NvidiaAdapterKeys().Count > 0
                ? ApplicabilityResult.Applicable("Driver NVIDIA instalado")
                : ApplicabilityResult.NotApplicable("Solo para GPUs NVIDIA con el driver instalado."),
            EvaluateGate = hw => hw.Chassis == ChassisKind.Laptop && values.Any(v => v.Name.StartsWith("PowerMizer") || v.Name == "EnableRuntimePowerManagement")
                ? EvaluationResult.NotRecommended("En laptop la GPU no se apagaría nunca: más consumo y temperatura, menos batería.")
                : evaluation,
            ReadLiveState = () => values.All(v =>
            {
                var paths = PathsFor(v.Where).ToList();
                return paths.Count > 0 && paths.All(p => ReadRegDword(RegistryHive.LocalMachine, p, v.Name) == v.Value);
            }),
            ApplyAction = _ =>
            {
                int writes = 0;
                foreach (var v in values)
                {
                    foreach (string p in PathsFor(v.Where))
                    {
                        _audit.SetRegistryValue(id, RegistryHive.LocalMachine, p, v.Name, v.Value, RegistryValueKind.DWord);
                        writes++;
                    }
                }
                return writes > 0
                    ? (true, $"{title}: {writes} valores escritos. Reiniciá para que el driver los lea.")
                    : (false, "No se encontró el driver de NVIDIA.");
            }
        };

    private void BuildNvidiaRegistryItems()
    {
        const string community = "Claves de nvlddmkm documentadas por la comunidad (sin documentación oficial de NVIDIA) · Fase E";

        // E10: preempción. Riesgo real de TDR (pantalla negra/reinicio del driver): solo Entusiasta.
        _allTweaks.Add(NvidiaRegistryItem("NvPreemptionOff", "G09", "NVIDIA: preempción de la GPU apagada (experimental)",
            "La GPU deja de interrumpir un trabajo de gráficos o CUDA a mitad de camino para atender otro: menos cambios de contexto en el driver. " +
            "⚠ Experimental: si un trabajo tarda mucho, Windows puede reiniciar el driver (TDR: pantalla negra unos segundos o cuelgue). " +
            "Medí DPC y frametime antes y después y revertí si empeora.",
            community, RiskLevel.High, inProfesional: false, inEntusiasta: true,
            EvaluationResult.Neutral("Experimental: medí antes y después."),
            new NvValue(NvWhere.Service, "DisablePreemption", 1),
            new NvValue(NvWhere.Service, "DisableCudaContextPreemption", 1)));

        // E10: DPC en el núcleo de origen, logging del RM, ahorro de energía de pantalla.
        _allTweaks.Add(NvidiaRegistryItem("NvDpcPerCoreAndLogging", "G10", "NVIDIA: DPC en el núcleo que pidió el trabajo + logging del driver apagado",
            "RmGpsPsEnablePerCpuCoreDpc = 1: el driver atiende sus DPC en el núcleo que originó el trabajo en vez de concentrarlos en uno. " +
            "RmLogonRC = 0: apaga el registro de eventos interno del Resource Manager. DisableKmRender = 0 deja el render en modo kernel como viene. " +
            "DisplayPowerSaving = 0: sin ahorro de energía del panel desde el driver. Experimental: medí y revertí si empeora.",
            community, RiskLevel.Medium, inProfesional: false, inEntusiasta: true,
            EvaluationResult.Neutral("Experimental: medí antes y después."),
            new NvValue(NvWhere.AdapterClass, "RmGpsPsEnablePerCpuCoreDpc", 1),
            new NvValue(NvWhere.AdapterClass, "DisableKmRender", 0),
            new NvValue(NvWhere.AdapterClass, "RmLogonRC", 0),
            new NvValue(NvWhere.NvTweak, "DisplayPowerSaving", 0)));

        // E11: la GPU no se apaga en ejecución (D3Cold): el chat le atribuye el pico al abrir un video. Va con ASPM apagado (S02).
        _allTweaks.Add(NvidiaRegistryItem("NvRuntimePowerOff", "G11", "NVIDIA: sin apagado de la GPU en ejecución (D3Cold)",
            "Con EnableRuntimePowerManagement = 0 el driver no apaga la GPU ni su enlace PCIe en reposo, así que no tiene que despertarla " +
            "cuando arranca un video o un juego. Conviene junto con \"Desactivar PCIe ASPM\" (S02).",
            community, RiskLevel.Low, inProfesional: true, inEntusiasta: true,
            EvaluationResult.Recommended("En escritorio: la GPU queda lista sin despertar."),
            new NvValue(NvWhere.AdapterClass, "EnableRuntimePowerManagement", 0)));

        // E11: relojes. PowerMizer en la clave del adaptador (donde lo lee el driver; G03 lo escribía en Global y se quitó).
        _allTweaks.Add(NvidiaRegistryItem("NvPowerMizerMax", "G12", "NVIDIA: PowerMizer en máximo rendimiento",
            "Fija el nivel de rendimiento del driver en el más alto (PerfLevelSrc = 0x2222, PowerMizerEnable = 0, PowerMizerLevel y PowerMizerLevelAC = 1) " +
            "y apaga el tiling de memoria del sistema. Complementa \"Force P0\" (G02): la GPU no baja relojes entre escenas.",
            community, RiskLevel.Medium, inProfesional: true, inEntusiasta: true,
            EvaluationResult.Recommended("En escritorio: relojes estables, más consumo en reposo."),
            new NvValue(NvWhere.AdapterClass, "PerfLevelSrc", 0x2222),
            new NvValue(NvWhere.AdapterClass, "PowerMizerEnable", 0),
            new NvValue(NvWhere.AdapterClass, "PowerMizerLevel", 1),
            new NvValue(NvWhere.AdapterClass, "PowerMizerLevelAC", 1),
            new NvValue(NvWhere.AdapterClass, "EnableSystemMemoryTiling", 0)));

        // E11: opción de depuración. Solo manual, nunca en perfiles.
        _allTweaks.Add(NvidiaRegistryItem("NvWriteCombiningOff", "G13", "NVIDIA: DisableWriteCombining (solo manual)",
            "⚠⚠ Opción de depuración del driver: desactiva la escritura combinada hacia la VRAM. En muchas PCs baja MUCHO los FPS. " +
            "Está porque aparece en guías de latencia; aplicala solo para probar, medí y revertí si los FPS caen.",
            community, RiskLevel.Critical, inProfesional: false, inEntusiasta: false,
            EvaluationResult.NotRecommended("Puede bajar mucho el rendimiento: solo para pruebas."),
            new NvValue(NvWhere.AdapterClass, "DisableWriteCombining", 1)));
    }
}
