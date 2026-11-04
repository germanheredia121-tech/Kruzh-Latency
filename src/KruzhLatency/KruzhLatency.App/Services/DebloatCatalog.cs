using System;
using System.Collections.Generic;
using System.Linq;
using KruzhLatency.Core.Debloat;
using KruzhLatency.Core.Tweaks;
using KruzhLatency.Infra.Debloat;
using KruzhLatency.Models;
using Microsoft.Win32;
using RiskLevel = KruzhLatency.Models.RiskLevel;
using TweakCategory = KruzhLatency.Models.TweakCategory;

namespace KruzhLatency.Services;

/// <summary>
/// T41 / U13: pestaña "Apps". Apps AppX que no hacen falta para jugar (quitar por usuario, reversible), desaprovisionar
/// (solo Entusiasta, irreversible), widgets, Copilot y publicidad/sugerencias de Windows. Solo aparece lo instalado.
/// </summary>
public sealed partial class DeclarativeTweakEngine
{
    private static HashSet<string> _installedAppxCache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _installedAppxAt = DateTime.MinValue;

    /// <summary>Paquetes instalados con caché de 2 s: el refresco de estados no enumera AppX una vez por ítem.</summary>
    private static bool IsAppxInstalled(string fullName)
    {
        if (DateTime.UtcNow - _installedAppxAt > TimeSpan.FromSeconds(2))
        {
            try
            {
                _installedAppxCache = AppxStore.Installed().Select(p => p.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                App.LogError("AppX: listar", ex);
            }
            _installedAppxAt = DateTime.UtcNow;
        }
        return _installedAppxCache.Contains(fullName);
    }

    private void BuildDebloatItems()
    {
        IReadOnlyList<AppxRecommendation> plan;
        try
        {
            plan = AppxCatalog.Plan(AppxStore.Installed());
        }
        catch (Exception ex)
        {
            App.LogError("AppX: plan", ex);
            plan = Array.Empty<AppxRecommendation>();
        }

        foreach (var rec in plan)
        {
            _allTweaks.Add(BuildAppxItem(rec));
        }
        if (plan.Count > 0)
        {
            _allTweaks.Add(BuildDeprovisionItem(plan));
        }

        _allTweaks.Add(RegistryPolicyItem("WidgetsPolicyOff", "Widgets y noticias de la barra de tareas", AppxCatalog.Assistant,
            "Apaga el panel de widgets por política: deja de cargar noticias y clima en segundo plano.",
            "Microsoft Learn: Policy CSP NewsAndInterests/AllowNewsAndInterests", Profile.Competitivo,
            new[] { (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0) },
            blockedHint: "Para sacarlos igual, usá \"Widgets (Web Experience Pack)\" en esta misma pestaña: quita el paquete y los widgets desaparecen."));

        _allTweaks.Add(RegistryPolicyItem("CopilotPolicyOff", "Copilot de Windows (política)", AppxCatalog.Assistant,
            "Política \"Desactivar Windows Copilot\". En versiones nuevas Copilot es una app: quitala también en \"Copilot (app)\" si aparece.",
            "Microsoft Learn: Policy CSP WindowsAI/TurnOffWindowsCopilot", Profile.Competitivo,
            new[]
            {
                (RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)
            }));

        _allTweaks.Add(RegistryPolicyItem("WindowsSuggestionsOff", "Publicidad y sugerencias de Windows", "Publicidad y sugerencias",
            "Apaga apps sugeridas que se instalan solas, sugerencias del menú Inicio y de Configuración, consejos, recomendaciones del Inicio de Windows 11 " +
            "y avisos de OneDrive en el Explorador. DisableWindowsConsumerFeatures solo tiene efecto en ediciones Enterprise/Education.",
            "Microsoft Learn: Manage connections from Windows components (ContentDeliveryManager) · Policy CSP Experience/AllowWindowsConsumerFeatures",
            Profile.Competitivo,
            new[]
            {
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353694Enabled", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353696Enabled", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SoftLandingEnabled", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1)
            }));
    }

    private DeclarativeTweakItem BuildAppxItem(AppxRecommendation rec)
    {
        string id = $"Appx:{rec.Rule.Id}";
        var packages = rec.Packages.ToList();
        string list = string.Join(", ", packages.Select(p => p.Name));
        return new DeclarativeTweakItem
        {
            Id = id,
            CodeNumber = "AP",
            Title = packages.Count > 1 ? $"{rec.Rule.Title} ({packages.Count})" : rec.Rule.Title,
            Category = TweakCategory.Apps,
            CategoryDisplay = rec.Rule.Section,
            TechnicalDescription = rec.Rule.Why + " Se quita solo para tu usuario y se puede revertir." +
                                   (rec.Rule.Warning is null ? "" : $"\n⚠ {rec.Rule.Warning}"),
            WhatItTouches = $"Quitar para el usuario (PackageManager.RemovePackageAsync): {list}",
            SourceAttribution = "Microsoft Learn: PackageManager (Windows.Management.Deployment) · catálogo KRUZH de apps no necesarias para jugar (T41)",
            Risk = RiskLevel.Low,
            InMinimal = rec.Rule.From == Profile.Normal,
            InCompetitivo = rec.Rule.From <= Profile.Competitivo,
            InProfesional = rec.Rule.From <= Profile.Profesional,
            InEntusiasta = true,
            EvaluateGate = _ => rec.Rule.Warning is null
                ? EvaluationResult.Recommended($"Se quita desde el perfil {rec.Rule.From}.")
                : EvaluationResult.Neutral(rec.Rule.Warning),
            ReadLiveState = () => packages.All(p => !IsAppxInstalled(p.FullName)),
            ApplyAction = _ =>
            {
                var failed = packages
                    .Select(p => (p, r: _audit.RemoveAppxForUser(id, p.FullName, p.FamilyName)))
                    .Where(x => !x.r.Ok)
                    .ToList();
                _installedAppxAt = DateTime.MinValue;
                return failed.Count == 0
                    ? (true, $"{rec.Rule.Title}: quitada para tu usuario.")
                    : (false, $"No se pudo quitar {string.Join(", ", failed.Select(f => f.p.Name))}: {failed[0].r.Detail}");
            }
        };
    }

    /// <summary>Solo Entusiasta: que las apps quitadas no vuelvan con usuarios nuevos ni con actualizaciones. Irreversible.</summary>
    private DeclarativeTweakItem BuildDeprovisionItem(IReadOnlyList<AppxRecommendation> plan)
    {
        var families = plan.SelectMany(r => r.Packages).Select(p => p.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new DeclarativeTweakItem
        {
            Id = "AppxDeprovision",
            CodeNumber = "AP",
            Title = "Que las apps quitadas no vuelvan (desaprovisionar)",
            Category = TweakCategory.Apps,
            CategoryDisplay = "Entusiasta",
            TechnicalDescription = "Saca las apps de esta lista de la imagen de Windows: no se reinstalan para usuarios nuevos ni con actualizaciones grandes. " +
                                   "⚠ Irreversible desde KRUZH: para recuperarlas hay que instalarlas desde la Microsoft Store.",
            WhatItTouches = "PackageManager.DeprovisionPackageForAllUsersAsync: " + string.Join(", ", families),
            SourceAttribution = "Microsoft Learn: PackageManager.DeprovisionPackageForAllUsersAsync",
            Risk = RiskLevel.High,
            InMinimal = false,
            InCompetitivo = false,
            InProfesional = false,
            InEntusiasta = true,
            EvaluateGate = _ => EvaluationResult.Neutral("Irreversible: solo si estás seguro de no querer estas apps nunca más."),
            ReadLiveState = () => _audit.HasBackupForTweak("AppxDeprovision"),
            ApplyAction = _ =>
            {
                var failed = families.Select(f => (f, r: _audit.DeprovisionAppx("AppxDeprovision", f))).Where(x => !x.r.Ok).ToList();
                return failed.Count == 0
                    ? (true, $"{families.Count} apps desaprovisionadas.")
                    : (false, $"No se pudieron desaprovisionar {failed.Count}: {failed[0].r.Detail}");
            },
            CustomRevertFallback = () => (false, "Desaprovisionar no se puede deshacer desde KRUZH: reinstalá las apps que quieras desde la Microsoft Store.")
        };
    }

    /// <summary>Ítem de valores DWORD de registro/política con journal; aplicado = todos los valores en el objetivo.</summary>
    private DeclarativeTweakItem RegistryPolicyItem(string id, string title, string section, string why, string source, Profile from,
        (RegistryHive Hive, string Path, string Name, int Value)[] values, string? blockedHint = null) => new()
    {
        Id = id,
        CodeNumber = "AP",
        Title = title,
        Category = TweakCategory.Apps,
        CategoryDisplay = section,
        TechnicalDescription = why,
        WhatItTouches = string.Join(" · ", values.Select(v => $@"{(v.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{v.Path}\{v.Name} = {v.Value}")),
        SourceAttribution = source,
        Risk = RiskLevel.Low,
        InMinimal = from == Profile.Normal,
        InCompetitivo = from <= Profile.Competitivo,
        InProfesional = from <= Profile.Profesional,
        InEntusiasta = true,
        EvaluateGate = _ => EvaluationResult.Recommended($"Desde el perfil {from}."),
        ReadLiveState = () => values.All(v => ReadRegDword(v.Hive, v.Path, v.Name) == v.Value),
        ApplyAction = _ =>
        {
            try
            {
                foreach (var v in values)
                {
                    _audit.SetRegistryValue(id, v.Hive, v.Path, v.Name, v.Value, RegistryValueKind.DWord);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Administradores tiene permiso sobre la clave, pero Windows 11 bloquea la escritura (protección del sistema, ej. UCPD).
                return (false, $"{title}: Windows bloquea este cambio aunque KRUZH corra como administrador (protección de Windows 11). {blockedHint}".TrimEnd());
            }
            return (true, $"{title}: aplicado.");
        }
    };
}
