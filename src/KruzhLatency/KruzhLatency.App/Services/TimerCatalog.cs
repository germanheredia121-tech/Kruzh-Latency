using System;
using System.IO;
using KruzhLatency.Core.Latency;
using KruzhLatency.Infra.Debloat;
using KruzhLatency.Infra.Timer;
using KruzhLatency.Models;
using Microsoft.Win32;

namespace KruzhLatency.Services;

/// <summary>E12 / ADR-010: timer de 0,5 ms mientras se juega (residente opcional) y SerializeTimerExpiration.</summary>
public sealed partial class DeclarativeTweakEngine
{
    private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";

    private static string AgentPath() => Path.Combine(AppContext.BaseDirectory, TimerResidentPolicy.AgentExeName);

    private void BuildTimerItems()
    {
        const string residentId = "TimerResident05ms";
        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = residentId,
            CodeNumber = "C16",
            Title = "Timer de 0,5 ms mientras jugás (modo residente)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Windows usa el timer de 1 ms salvo que un programa pida uno más fino, y el pedido vale solo mientras ese programa está abierto. " +
                                   "Esto deja un proceso liviano (sin ventana) que arranca al iniciar sesión, mira cada 5 s si CS2 o VALORANT están abiertos " +
                                   "y solo entonces pide 0,5 ms; al cerrar el juego lo libera. No abre ni toca el juego: solo ve su nombre en la lista de procesos. " +
                                   "En Windows 11 necesita GlobalTimerResolutionRequests (C02). Revertir detiene el proceso y borra la tarea. " +
                                   "Medí el frametime en el Monitor: si empeora, revertí.",
            WhatItTouches = $@"Tarea programada {TimerResidentPolicy.TaskPath} (al iniciar sesión, sin elevar) -> {TimerResidentPolicy.AgentExeName} · NtSetTimerResolution(0,5 ms) solo con cs2.exe / VALORANT-Win64-Shipping.exe abiertos",
            SourceAttribution = "Microsoft Learn: timeBeginPeriod y GlobalTimerResolutionRequests (Windows 11) · ADR-010",
            Risk = RiskLevel.Low,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false, // opcional (ADR-010): apagado por defecto
            ApplicableGate = _ => File.Exists(AgentPath())
                ? ApplicabilityResult.Applicable()
                : ApplicabilityResult.NotApplicable($"Falta {TimerResidentPolicy.AgentExeName} junto a KRUZH (reinstalá la app)."),
            EvaluateGate = hw => hw.IsWindows11 && ReadRegDword(RegistryHive.LocalMachine, KernelKey, "GlobalTimerResolutionRequests") != 1
                ? EvaluationResult.Neutral("Activá también C02 (GlobalTimerResolutionRequests): sin eso, en Windows 11 el pedido no llega al juego.")
                : EvaluationResult.Recommended("Opcional: activalo si querés 0,5 ms mientras jugás."),
            ReadLiveState = () => ScheduledTaskStore.IsEnabled(TimerResidentPolicy.TaskPath) == true,
            ApplyAction = _ =>
            {
                string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                _audit.CreateScheduledTask(residentId, TimerResidentPolicy.TaskPath, TimerResidentPolicy.TaskXml(AgentPath(), user));
                if (!TimerResidentAgent.IsRunning()) ScheduledTaskStore.Run(TimerResidentPolicy.TaskPath);
                return (true, "Modo residente activo: con CS2 o VALORANT abiertos el timer pasa a 0,5 ms. Arranca solo al iniciar sesión.");
            }
        });

        _allTweaks.Add(new DeclarativeTweakItem
        {
            Id = "SerializeTimerExpirationOff",
            CodeNumber = "C17",
            Title = "SerializeTimerExpiration = 0 (experimental)",
            Category = TweakCategory.SystemCpu,
            CategoryDisplay = "Sistema & CPU",
            TechnicalDescription = "Valor del kernel que controla si el vencimiento de timers se serializa en un solo procesador. " +
                                   "No tiene documentación de Microsoft: aparece en guías de latencia. Experimental: medí DPC y frametime antes y después y revertí si empeora.",
            WhatItTouches = $@"HKLM\{KernelKey} -> SerializeTimerExpiration = 0",
            SourceAttribution = "Guías comunitarias de latencia (sin documentación oficial) · Fase E (E12)",
            Risk = RiskLevel.Medium,
            RequiresReboot = true,
            InMinimal = false, InCompetitivo = false, InProfesional = false, InEntusiasta = false, // sin documentación: solo manual
            EvaluateGate = _ => EvaluationResult.Neutral("Experimental: medí antes y después."),
            ReadLiveState = () => ReadRegDword(RegistryHive.LocalMachine, KernelKey, "SerializeTimerExpiration") == 0,
            ApplyAction = _ =>
            {
                _audit.SetRegistryValue("SerializeTimerExpirationOff", RegistryHive.LocalMachine, KernelKey, "SerializeTimerExpiration", 0, RegistryValueKind.DWord);
                return (true, "SerializeTimerExpiration = 0. Reiniciá para que el kernel lo tome.");
            }
        });
    }
}
