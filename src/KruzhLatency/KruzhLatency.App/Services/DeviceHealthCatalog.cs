using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Network;
using KruzhLatency.Infra.Devices;
using KruzhLatency.Infra.Network;
using KruzhLatency.Models;

namespace KruzhLatency.Services;

/// <summary>
/// Spec 002: fantasmas (B2), dispositivos con error (B3), avisos de la pila de red (B5) y puerto del mouse/teclado (B6).
/// Lo accionable entra en perfiles; lo que depende del usuario (instalar un driver, desinstalar un programa, cambiar
/// de puerto) queda como ítem manual que abre la página correspondiente.
/// </summary>
public sealed partial class DeclarativeTweakEngine
{
    private const string GhostSection = "Dispositivos fantasma";
    private const string IssueSection = "Dispositivos con error";

    // ---------------------------------------------------------------- B2

    private void BuildGhostItems(DeviceTree tree)
    {
        foreach (var group in GhostDevicePlanner.Plan(tree))
        {
            string id = $"Ghost:{group.Category}";
            var instances = group.Devices.Select(d => d.InstanceId).ToList();
            _allTweaks.Add(new DeclarativeTweakItem
            {
                Id = id,
                CodeNumber = "DV",
                Title = $"Borrar: {group.Title} ({instances.Count})",
                Category = TweakCategory.Devices,
                CategoryDisplay = GhostSection,
                TechnicalDescription = group.Why +
                    "\n⚠ Irreversible: se borran del registro de dispositivos (no se puede deshacer desde KRUZH). Si volvés a conectar el aparato, Windows lo reinstala solo. Antes de aplicar se crea un punto de restauración.",
                WhatItTouches = "Desinstalar dispositivo no conectado (CM_Uninstall_DevNode): " +
                                string.Join(", ", group.Devices.Select(d => $"{d.DisplayName} ({d.InstanceId})")),
                SourceAttribution = "Microsoft Learn: Device Manager → Mostrar dispositivos ocultos / desinstalar · spec 002 (B2)",
                Risk = RiskLevel.Low,
                RequiresReboot = false,
                InMinimal = false,
                InCompetitivo = true,
                InProfesional = true,
                EvaluateGate = _ => EvaluationResult.Recommended("Se limpian desde el perfil Competitivo."),
                ReadLiveState = () =>
                {
                    var inv = new CfgMgrDeviceInventory();
                    return instances.All(i => inv.GetDevice(i) is null);
                },
                ApplyAction = _ =>
                {
                    var inv = new CfgMgrDeviceInventory();
                    var failures = instances
                        .Select(i => (Id: i, Result: inv.RemovePhantom(i)))
                        .Where(r => !r.Result.Removed)
                        .ToList();
                    return failures.Count == 0
                        ? (true, $"{instances.Count} fantasmas borrados.")
                        : (false, $"{instances.Count - failures.Count} borrados; no se pudieron borrar {failures.Count}: {failures[0].Result.Detail}");
                },
                CustomRevertFallback = () => (false, "Borrar fantasmas no se puede deshacer: reconectá el dispositivo y Windows lo reinstala.")
            });
        }
    }

    // ---------------------------------------------------------------- B3

    private void BuildIssueItems(DeviceTree tree, DeviceSafetyContext safety)
    {
        foreach (var issue in DeviceIssueAdvisor.Evaluate(tree, safety))
        {
            string instance = issue.Device.InstanceId;
            if (issue.Action == DeviceIssueAction.Disable)
            {
                _allTweaks.Add(new DeclarativeTweakItem
                {
                    Id = issue.Id,
                    CodeNumber = "DV",
                    Title = issue.Title,
                    Category = TweakCategory.Devices,
                    CategoryDisplay = IssueSection,
                    TechnicalDescription = issue.Detail,
                    WhatItTouches = $"Deshabilitar en el Administrador de dispositivos: {issue.Device.DisplayName} ({instance})",
                    SourceAttribution = "Microsoft Learn: Device Manager error codes · spec 002 (B3)",
                    Risk = RiskLevel.Low,
                    RequiresReboot = true,
                    InCompetitivo = true,
                    InProfesional = true,
                    EvaluateGate = _ => EvaluationResult.Recommended("Dispositivo en error que no se usa."),
                    ReadLiveState = () => TransactionalAuditService.IsDeviceDisabled(instance),
                    ApplyAction = _ =>
                    {
                        _audit.SetDeviceEnabled(issue.Id, instance, enable: false);
                        return (true, $"{issue.Device.DisplayName}: deshabilitado.");
                    }
                });
                continue;
            }

            _allTweaks.Add(ManualLinkItem(issue.Id, issue.Title, IssueSection, TweakCategory.Devices, issue.Detail,
                issue.Link ?? "ms-settings:windowsupdate-optionalupdates",
                isResolved: () => new CfgMgrDeviceInventory().GetDevice(instance) is not { ProblemCode: not 0 }));
        }
    }

    // ---------------------------------------------------------------- B6: puerto del mouse / teclado

    private void BuildInputPortAdvice(DeviceTree tree)
    {
        var onChipset = UsbPowerPlanner.InputPlacement(tree).Where(p => p.Location == UsbControllerLocation.Chipset).ToList();
        if (onChipset.Count == 0) return;

        string who = string.Join(", ", onChipset.Select(p => p.Input.DisplayName).Distinct());
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "InputOnChipsetUsb",
            CodeNumber = "S01",
            Title = "Mouse/teclado en un puerto USB del chipset",
            Category = TweakCategory.UsbPowerBus,
            CategoryDisplay = "USB, Disco & Memoria",
            TechnicalDescription = $"{who} cuelga de la controladora USB del chipset: cada reporte pasa por el enlace CPU–chipset antes de llegar al CPU. " +
                                   "Conectalo a un puerto de la controladora del CPU (en el manual de tu placa figuran como puertos del procesador; suelen ser USB de atrás). " +
                                   "Después de cambiarlo, refrescá: este aviso desaparece.",
            WhatItTouches = "Nada: es un cambio físico de puerto.",
            SourceAttribution = "Topología PCIe real (controladora xHCI y bridges) · spec 002 (B6)",
            Risk = RiskLevel.Low,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false,
            EvaluateGate = _ => EvaluationResult.Neutral("Cambio físico: KRUZH no puede hacerlo por vos."),
            ReadLiveState = () => UsbPowerPlanner.InputPlacement(CachedDeviceTree())
                                                 .All(p => p.Location != UsbControllerLocation.Chipset),
            ApplyAction = _ => (false, "Mové el mouse/teclado a un puerto USB del CPU y refrescá.")
        });
    }

    // ---------------------------------------------------------------- B5

    private void BuildNetworkAdviceItems()
    {
        IReadOnlyList<NetworkAdvice> advice;
        try
        {
            advice = NetworkStackAdvisor.Evaluate(NetworkStackProbe.Detect());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Análisis de la pila de red falló: {ex.Message}");
            return;
        }

        foreach (var a in advice)
        {
            _allTweaks.Add(ManualLinkItem(a.Id, a.Title, "Red & Desync", TweakCategory.NetworkDesync, a.Detail, a.Link,
                isResolved: () => CachedNetworkAdvice().All(x => x.Id != a.Id)));
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Ítem manual (ningún perfil lo aplica): "Aplicar" abre la página para resolverlo; queda aplicado cuando el problema desaparece.</summary>
    private static DeclarativeTweakItem ManualLinkItem(string id, string title, string section, TweakCategory category, string detail, string link, Func<bool> isResolved) =>
        new()
        {
            Id = id,
            CodeNumber = category == TweakCategory.Devices ? "DV" : "N09",
            Title = title,
            Category = category,
            CategoryDisplay = section,
            TechnicalDescription = detail + "\nAl aplicar se abre la página para resolverlo; KRUZH no instala ni desinstala programas.",
            WhatItTouches = $"Nada en el sistema: abre {link}",
            SourceAttribution = "spec 002 (B3/B5)",
            Risk = RiskLevel.Low,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false,
            EvaluateGate = _ => EvaluationResult.Neutral("Lo resolvés vos: instalar/desinstalar fuera de KRUZH."),
            ReadLiveState = isResolved,
            ApplyAction = _ =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = link, UseShellExecute = true });
                    return (true, $"Se abrió {link}.");
                }
                catch (Exception ex)
                {
                    return (false, $"No se pudo abrir {link}: {ex.Message}");
                }
            }
        };
}
