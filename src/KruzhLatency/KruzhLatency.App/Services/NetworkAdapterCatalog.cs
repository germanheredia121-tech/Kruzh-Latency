using System;
using System.Collections.Generic;
using System.Linq;
using KruzhLatency.Core.Network;
using KruzhLatency.Infra.Devices;
using KruzhLatency.Infra.Network;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

/// <summary>
/// Spec 002 / B4: un ítem por placa de red física con un chip conocido (Realtek / Intel / Killer).
/// Los valores salen de NicTuningPlanner (Core) validados contra Ndi\Params del driver instalado;
/// al aplicar se reinicia el adaptador en el momento (corte de red de unos segundos).
/// </summary>
public sealed partial class DeclarativeTweakEngine
{
    private const int PnPCapabilitiesNoPowerOff = 24; // 0x18: desmarca "Permitir que el equipo apague este dispositivo"

    private void BuildNicItems()
    {
        IReadOnlyList<NetworkAdapterFact> adapters;
        try
        {
            adapters = DeviceContextProbe.NetworkAdapters();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Lectura de placas de red falló: {ex.Message}");
            return;
        }

        int cores = Math.Max(1, _hwService.CurrentProfile.CpuPhysicalCores);
        foreach (var nic in adapters.Where(a => a.InstanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) ||
                                                a.InstanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)))
        {
            if (TransactionalAuditService.IsDeviceDisabled(nic.InstanceId)) continue; // deshabilitada: no se toca ni se reinicia
            var props = NdiParamsReader.Read(nic.ClassKeyPath);
            if (props is null || NicTuningPlanner.Plan(props, cores) is not { } plan || plan.Changes.Count == 0) continue;
            _allTweaks.Add(BuildNicItem(nic, props, plan, cores));
        }
    }

    private DeclarativeTweakItem BuildNicItem(NetworkAdapterFact nic, NicAdvancedProperties props, NicTuningPlan plan, int cores)
    {
        string id = $"Nic:{nic.InstanceId}";
        string changes = string.Join("\n", plan.Changes.Select(c => $"• {c.Label}: {Describe(props, c.Keyword, c.Current)} → {Describe(props, c.Keyword, c.Target)}. {c.Why}"));

        return new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "N01",
            Title = $"Placa de red: {props.DriverDesc}{(nic.HasLink ? "" : " (sin cable)")}",
            Category = TweakCategory.NetworkDesync,
            CategoryDisplay = "Red & Desync",
            TechnicalDescription = $"{plan.Chip.Name}. Solo se cambian opciones que tu driver tiene y con valores que acepta:\n{changes}\n" +
                                   "• Administración de energía: no permitir que Windows apague la placa.\n" +
                                   "Al aplicar, la placa se reinicia en el momento (la red se corta unos segundos).",
            WhatItTouches = $@"HKLM\{nic.ClassKeyPath} -> " +
                            string.Join(", ", plan.Changes.Select(c => $"{c.Keyword}={c.Target}")) +
                            $", PnPCapabilities={PnPCapabilitiesNoPowerOff} · reinicio del adaptador",
            SourceAttribution = "Microsoft Learn: Standardized INF Keywords for Network Devices (interrupt moderation, RSS, offload, power management) · Ndi\\Params del driver instalado",
            Risk = RiskLevel.Low,
            RequiresReboot = false,
            InMinimal = false,
            InCompetitivo = true,
            InProfesional = true,
            EvaluateGate = _ => EvaluationResult.Recommended(nic.HasLink
                ? "Es la placa por la que jugás."
                : "Sin cable: si no la usás, el Device Manager propone deshabilitarla."),
            ReadLiveState = () =>
            {
                var now = NdiParamsReader.Read(nic.ClassKeyPath);
                return now is not null && NicTuningPlanner.Plan(now, cores) is { IsFullyApplied: true } &&
                       ReadRegDword(RegistryHive.LocalMachine, nic.ClassKeyPath, "PnPCapabilities") == PnPCapabilitiesNoPowerOff;
            },
            ApplyAction = _ =>
            {
                // Se recalcula al aplicar: el driver pudo cambiar desde que se armó el catálogo.
                var now = NdiParamsReader.Read(nic.ClassKeyPath);
                if (now is null || NicTuningPlanner.Plan(now, cores) is not { } current)
                {
                    return (false, "No se pudieron leer las opciones de la placa (¿se desinstaló el driver?).");
                }

                foreach (var c in current.Changes.Where(c => !c.IsAlreadySet))
                {
                    _audit.SetRegistryValue(id, RegistryHive.LocalMachine, nic.ClassKeyPath, c.Keyword, c.Target, RegistryValueKind.String);
                }
                _audit.SetRegistryValue(id, RegistryHive.LocalMachine, nic.ClassKeyPath, "PnPCapabilities", PnPCapabilitiesNoPowerOff, RegistryValueKind.DWord);

                var restart = new CfgMgrDeviceInventory().Restart(nic.InstanceId, TimeSpan.FromSeconds(20));
                return restart.IsHealthy
                    ? (true, $"{props.DriverDesc}: {current.Changes.Count(c => !c.IsAlreadySet)} opciones ajustadas y placa reiniciada.")
                    : (true, $"{props.DriverDesc}: opciones guardadas. {restart.Detail} Se aplican al reiniciar la PC.");
            }
        };
    }

    /// <summary>Valor con su etiqueta del driver si es un enum (ej. "0 (Disabled)").</summary>
    private static string Describe(NicAdvancedProperties props, string keyword, string? value)
    {
        if (value is null) return "default";
        return props.Get(keyword)?.Enum is { } e && e.TryGetValue(value, out var label) ? label : value;
    }
}
