# KRUZH LATENCY

Optimizador de latencia para CS2 y VALORANT (FACEIT AC / Vanguard).
Límite duro (ADR-004): nunca inyectar ni leer/escribir archivos o memoria de los juegos; por juego solo `.nip`, IFEO, CPU sets, GPU pref, FSO. Windows 10/11, app de escritorio que corre como admin y modifica registro, servicios, BCD y powercfg.

## Stack
.NET 8, WinUI 3 (Windows App SDK 1.8, unpackaged, self-contained x64), CommunityToolkit.Mvvm, xUnit.

## Documentos (leer antes de codear)
- Specs: `specs/001-kruzh-latency-mvp.md`, `specs/002-dpc-red-dispositivos-pantalla.md` · Plan: `docs/PLAN.md` · Tareas: `tasks.md`
- Bugs conocidos y veredicto por tweak: `docs/AUDITORIA.md`
- Decisiones: `docs/decisions/` (respetarlas; si una tarea las contradice, frenar y preguntar)

## Proyectos (`src/KruzhLatency/`)
- `KruzhLatency.Core` (net8.0, lógica pura) · `KruzhLatency.Infra` (adaptadores Windows) · `KruzhLatency.Core.Tests` (xUnit) · `KruzhLatency.Infra.Tests` (integración: se saltean salvo `KRUZH_INTEGRATION=1`) · `KruzhLatency.App` (WinUI, exe `KruzhLatency.exe`)

## Comandos (desde `src/KruzhLatency/`)
- Build: `dotnet build KruzhLatency.sln -c Debug -warnaserror` (la config "Any CPU" de la solución compila la app en x64)
- Tests: `dotnet test KruzhLatency.sln`
Antes de decir "listo": build + tests en verde.
(Si `dotnet` no está en PATH: `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`.)
Si KRUZH LATENCY está abierto, el build hacia `bin` se corta a propósito (un build a medias deja exe viejo + XAML nuevo = "XAML parsing failed"). Con la app abierta: compilar con `-p:OutDir=<carpeta temporal>` y testear los proyectos de test directamente (no el `.sln`, que compila la app en `bin`).

## Reglas
- Lógica en `KruzhLatency.Core`. Nada de `Microsoft.Win32.Registry`, `Process` ni `ServiceController` fuera de `KruzhLatency.Infra`.
- Toda escritura al sistema es una `Operation` con `Capture` (backup) → `Apply` → `Read` (verificación). Sin backup no se escribe.
- Nunca parsear texto localizado de herramientas de Windows (bcdedit/powercfg). Usar APIs/WMI.
- Nunca correr operaciones de sistema en el hilo de UI.
- Nada de rutas hardcodeadas a la PC del autor.
- La descripción de un tweak sale de sus operaciones; no escribir texto que prometa algo que el código no hace.
- No agregar tweaks sin `Source` y sin cumplir ADR-002.
- NO ejecutar la app ni tests de integración en la máquina de desarrollo sin que el usuario lo pida: modifican el sistema real.
- No instalar paquetes NuGet nuevos sin preguntar. Aprobados: `Microsoft.Diagnostics.Tracing.TraceEvent` y `System.Management` (solo en Infra: ETW y WMI).
- `research/` es material de referencia: no se compila ni se modifica.
