using System;
using System.Linq;
using KruzhLatency.Core.Devices;
using KruzhLatency.Core.Latency;
using KruzhLatency.Models;

namespace KruzhLatency.Services;

/// <summary>E7/E8/E9: texto de los planes de interrupciones para esta PC y aviso de puertos de mouse/teclado.</summary>
public sealed partial class DeclarativeTweakEngine
{
    /// <summary>E2: núcleo de la red según el plan de E7 (null si no hay plan). Se calcula una vez por catálogo.</summary>
    private RssPlacement? RssForThisPc()
    {
        try
        {
            return _irqMsi.PlanAffinity(CachedDeviceTree())?.Rss;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Plan de afinidad falló: {ex.Message}");
            return null;
        }
    }

    private static string MsiTargetsText()
    {
        try
        {
            var targets = MsiPlanner.Targets(CachedDeviceTree());
            return targets.Count == 0
                ? "\nEn tu PC ningún dispositivo de esas clases declara MSI."
                : "\nEn tu PC: " + string.Join(" · ", targets.Select(t => $"{t.Device.DisplayName} ({t.Kind}{(t.RemoveMessageLimit ? ", MSI-X" : "")})")) + ".";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Lista de MSI falló: {ex.Message}");
            return "";
        }
    }

    private string AffinityPlanText()
    {
        try
        {
            return _irqMsi.PlanAffinity(CachedDeviceTree()) is { } plan
                ? "En tu PC (GPU en dos núcleos): " + IrqAndMsiService.Describe(plan)
                : "Tu CPU tiene menos de 4 núcleos físicos: no se aplica.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Plan de afinidad falló: {ex.Message}");
            return "";
        }
    }

    // ---------------------------------------------------------------- E9: mouse y teclado en controladoras distintas

    private void BuildInputSeparationAdvice(DeviceTree tree)
    {
        var placement = UsbPowerPlanner.InputPlacement(tree);
        int controllers = InterruptAffinityPlanner.Collect(tree).Count(d => d.Kind == IrqDeviceKind.UsbController);
        if (controllers < 2 || !Shares(tree, placement)) return;

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "InputSharedUsbController",
            CodeNumber = "S01",
            Title = "Mouse y teclado en la misma controladora USB",
            Category = TweakCategory.UsbPowerBus,
            CategoryDisplay = "USB, Disco & Memoria",
            TechnicalDescription = "El mouse y el teclado cuelgan de la misma controladora USB, así que sus interrupciones van al mismo núcleo. " +
                                   $"Tu PC tiene {controllers} controladoras: dejá el mouse en un puerto del CPU y pasá el teclado a un puerto de la otra controladora (la del chipset), " +
                                   "así cada uno tiene su núcleo con el reparto de interrupciones (I02). Después de cambiarlo, refrescá: este aviso desaparece.",
            WhatItTouches = "Nada: es un cambio físico de puerto.",
            SourceAttribution = "Topología PCIe real (controladoras xHCI) · spec 002 (B6) · Fase E (E9)",
            Risk = RiskLevel.Low,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false,
            EvaluateGate = _ => EvaluationResult.Neutral("Cambio físico: KRUZH no puede hacerlo por vos."),
            ReadLiveState = () =>
            {
                var now = CachedDeviceTree();
                return !Shares(now, UsbPowerPlanner.InputPlacement(now));
            },
            ApplyAction = _ => (false, "Pasá el teclado a un puerto de la otra controladora USB y refrescá.")
        });

        // Comparte si un teclado de OTRO aparato USB está en la controladora de un mouse (un receptor inalámbrico
        // expone mouse y teclado a la vez: eso no cuenta).
        static bool Shares(DeviceTree t, System.Collections.Generic.IReadOnlyList<InputPortPlacement> p)
        {
            string? Physical(DeviceNode input) => t.Ancestors(input)
                .FirstOrDefault(a => a.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
                                     !a.InstanceId.Split('\\')[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase))?.InstanceId;

            var mice = p.Where(x => string.Equals(x.Input.ClassName, "Mouse", StringComparison.OrdinalIgnoreCase)).ToList();
            return p.Where(x => string.Equals(x.Input.ClassName, "Keyboard", StringComparison.OrdinalIgnoreCase))
                    .Any(k => mice.Any(m => string.Equals(m.Controller.InstanceId, k.Controller.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                                            !string.Equals(Physical(m.Input), Physical(k.Input), StringComparison.OrdinalIgnoreCase)));
        }
    }
}
