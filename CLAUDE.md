# KRUZH LATENCY

Optimizador de latencia para CS2 competitivo. Windows 10/11, app de escritorio que corre como admin y modifica registro, servicios, BCD y powercfg.

## Stack
.NET 8, WinUI 3 (Windows App SDK 1.5, unpackaged, self-contained x64), CommunityToolkit.Mvvm, xUnit.

## Documentos (leer antes de codear)
- Spec: `specs/001-kruzh-latency-mvp.md` · Plan: `docs/PLAN.md` · Tareas: `tasks.md`
- Bugs conocidos y veredicto por tweak: `docs/AUDITORIA.md`
- Decisiones: `docs/decisions/` (respetarlas; si una tarea las contradice, frenar y preguntar)

## Comandos (desde `src/`)
- Build: `dotnet build -c Debug -p:Platform=x64 -warnaserror`
- Tests: `dotnet test`
Antes de decir "listo": build + tests en verde.
(Si `dotnet` no está en PATH: `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`.)

## Reglas
- Lógica en `KruzhLatency.Core`. Nada de `Microsoft.Win32.Registry`, `Process` ni `ServiceController` fuera de `KruzhLatency.Infra`.
- Toda escritura al sistema es una `Operation` con `Capture` (backup) → `Apply` → `Read` (verificación). Sin backup no se escribe.
- Nunca parsear texto localizado de herramientas de Windows (bcdedit/powercfg). Usar APIs/WMI.
- Nunca correr operaciones de sistema en el hilo de UI.
- Nada de rutas hardcodeadas a la PC del autor.
- La descripción de un tweak sale de sus operaciones; no escribir texto que prometa algo que el código no hace.
- No agregar tweaks sin `Source` y sin cumplir ADR-002.
- NO ejecutar la app ni tests de integración en la máquina de desarrollo sin que el usuario lo pida: modifican el sistema real.
- No instalar paquetes NuGet nuevos sin preguntar.
- `research/` es material de referencia: no se compila ni se modifica.
