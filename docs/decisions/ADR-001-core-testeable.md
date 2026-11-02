# ADR-001 — Lógica en una librería Core testeable, Windows detrás de interfaces

Fecha: 2026-11-02 · Estado: PROPUESTA

## Contexto
Toda la lógica vive hoy en `DeclarativeTweakEngine.cs` (2.400 líneas de lambdas) y `MainWindow.xaml.cs` (2.200 líneas), llamando directo a `Registry`, `Process` y `ServiceController`. No se puede testear sin tocar el sistema real, por eso no hay tests y los bugs B1–B9 pasaron desapercibidos.

## Decisión
- `KruzhLatency.Core` (net8.0, sin WinUI) contiene modelos, catálogo, engine, journal y parsers.
- Todo acceso al sistema pasa por `IRegistry`, `IProcessRunner`, `IServiceManager`, `IPowerSettings`, `IBcdStore`, `IRestorePoint`.
- Los tweaks son **datos** (lista de `Operation`), no lambdas con lógica ad-hoc.

## Consecuencias
- + Tests rápidos con fakes; los agentes se autocorrigen con `dotnet test`.
- + Revert y estado genéricos (fin de los fallbacks duplicados).
- − Migración inicial de ~40 tweaks al formato nuevo (T30).
