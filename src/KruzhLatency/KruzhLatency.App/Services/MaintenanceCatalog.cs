using KruzhLatency.Core.Debloat;
using KruzhLatency.Infra.Debloat;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

/// <summary>E4: Mantenimiento automático de Windows (interruptor oficial + cada tarea por separado, con journal).</summary>
public sealed partial class DeclarativeTweakEngine
{
    private const string MaintenanceSection = "Mantenimiento automático";

    private void BuildMaintenanceItems()
    {
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "AutoMaintenanceOff",
            CodeNumber = "C14",
            Title = "Mantenimiento automático de Windows apagado",
            Category = TweakCategory.Apps,
            CategoryDisplay = MaintenanceSection,
            TechnicalDescription = "El mantenimiento automático lanza en segundo plano optimización de unidades, escaneos y diagnósticos cuando Windows cree que la PC está libre, " +
                                   "y puede arrancar en medio de una partida. Se apaga con el interruptor oficial de Windows.\n" +
                                   "⚠ La optimización de unidades (TRIM semanal) y los escaneos programados dejan de correr solos: corrélos a mano de vez en cuando.",
            WhatItTouches = $@"HKLM\{MaintenanceTasks.RegistryKey} -> {MaintenanceTasks.RegistryValue} = 1",
            SourceAttribution = "Microsoft Learn: Automatic Maintenance (Task Scheduler) · MaintenanceDisabled",
            Risk = RiskLevel.Low,
            InProfesional = true,
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, MaintenanceTasks.RegistryKey, MaintenanceTasks.RegistryValue) == 1,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("AutoMaintenanceOff", RegistryHive.LocalMachine, MaintenanceTasks.RegistryKey,
                    MaintenanceTasks.RegistryValue, 1, RegistryValueKind.DWord);
                return (true, "Mantenimiento automático apagado.");
            }
        });

        foreach (var (path, why) in MaintenanceTasks.All)
        {
            string id = "MaintTask:" + MaintenanceTasks.Name(path);
            _allTweaks.Add(new DeclarativeTweakItem
            {
                Id = id,
                CodeNumber = "C15",
                Title = $"Tarea: {MaintenanceTasks.Name(path)}",
                Category = TweakCategory.Apps,
                CategoryDisplay = MaintenanceSection,
                TechnicalDescription = why + " Se desactiva la tarea; revertir la vuelve a activar.",
                WhatItTouches = $"Programador de tareas (API COM): {path} -> deshabilitada",
                SourceAttribution = "Microsoft Learn: Automatic Maintenance · Task Scheduler",
                Risk = RiskLevel.Low,
                InProfesional = true,
                ApplicableGate = _ => ScheduledTaskStore.IsEnabled(path) is null
                    ? ApplicabilityResult.NotApplicable("Esta tarea no existe en tu Windows.")
                    : ApplicabilityResult.Applicable(),
                ReadLiveState = () => ScheduledTaskStore.IsEnabled(path) != true,
                ApplyAction = _ => _audit.SetScheduledTaskEnabled(id, path, false)
                    ? (true, $"{MaintenanceTasks.Name(path)} desactivada.")
                    : (false, $"Windows no dejó desactivar {MaintenanceTasks.Name(path)}.")
            });
        }
    }
}
