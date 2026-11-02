# Plan técnico — KRUZH LATENCY v4

Implementa [specs/001-kruzh-latency-mvp.md](../specs/001-kruzh-latency-mvp.md). Decisiones en [docs/decisions/](decisions/).

## 1. Estructura de la solución (objetivo)

```
src/
├─ KruzhLatency.Core/          net8.0 (sin WinUI, sin Microsoft.Win32 directo)
│   ├─ Tweaks/                 TweakDefinition, Operation*, TweakState, Presets
│   ├─ Catalog/                catálogo declarativo (1 archivo por categoría)
│   ├─ Engine/                 TweakEngine: Evaluate / Plan / Apply / Verify / Revert
│   ├─ Journal/                JournalDocument v2, JournalStore (atómico)
│   ├─ Hardware/               HardwareProfile + parsers puros (topología, VDF, cs2_video.txt)
│   ├─ Abstractions/           IRegistry, IProcessRunner, IServiceManager, IPowerSettings, IBcdStore, IClock
│   └─ Measurement/            parser de CSV de PresentMon + comparador A/B
├─ KruzhLatency.Infra/         net8.0-windows: implementaciones reales de Abstractions
├─ KruzhLatency.Core.Tests/    xUnit + FakeRegistry / FakeProcessRunner / buffers grabados
└─ KruzhLatency.App/           WinUI 3 (el proyecto actual), MVVM con CommunityToolkit.Mvvm
```

Regla: **toda lógica en Core**. La UI solo bindea ViewModels. El agente itera con `dotnet test`.

## 2. Contrato central (se define primero, en T10, y no se rompe después)

```csharp
public abstract record Operation(string Id)           // una escritura atómica y reversible
{
    public abstract OpState Read(ISystem sys);          // Target | NotTarget | Missing
    public abstract Snapshot Capture(ISystem sys);      // valor original (para journal)
    public abstract void Apply(ISystem sys);
    public abstract void Restore(ISystem sys, Snapshot s);
    public abstract string Describe();                  // texto que ve el usuario (fuente única)
}
// Implementaciones: RegistryValueOp, ServiceStartOp, BcdElementOp, PowerSettingOp(schemeGuid explícito),
//                   ScheduledTaskOp, NicAdvancedPropertyOp (solo si existe en Ndi\Params), IfeoOp

public sealed record TweakDefinition(
    string Id, string Code, string Title, TweakCategory Category,
    string Why, string Source, RiskLevel Risk, bool RequiresReboot,
    Func<HardwareProfile, Gate> Applicable, Func<HardwareProfile, Gate> Recommended,
    AnticheatRisk Anticheat, PresetMembership Presets,
    IReadOnlyList<TweakOption> Options);                // toggle = 1 opción "On"; cada opción = lista de Operation

public enum TweakState { NotApplicable, NotApplied, Partial, Applied, Unknown }
public sealed record ApplyResult(string TweakId, ApplyOutcome Outcome, string Detail); // Ok | AlreadyOk | Failed | NeedsReboot
```

Consecuencias:
- Estado del tweak = agregación de `Read` de **todas** sus operaciones → arregla B7.
- Revert es genérico (journal) → desaparecen los `CustomRevertFallback` y los `*_Rev` → arregla B2.
- `Describe()` alimenta la UI → la descripción no puede mentir (B21).

## 3. Engine

1. `Evaluate(hw)` → gates + estado (lectura pura, en background).
2. `Plan(selección)` → lista de operaciones a ejecutar (diff contra estado actual).
3. `Apply(plan, progress, ct)`:
   - por operación: `Capture` → journal (atómico) → `Apply` → `Read` de verificación;
   - try/catch **por tweak**; un fallo no corta el lote (B1).
4. `Revert(tweakId | all)` → `Restore` en orden inverso; luego borra entradas del journal.

Corre en `Task.Run`; la UI recibe `IProgress<ApplyResult>`.

## 4. Journal v2
- `%ProgramData%\KruzhLatency\journal.json` (máquina) + `schemaVersion`.
- Escritura: `journal.tmp` → `File.Replace(tmp, journal, journal.bak)` (B14).
- Clave de baseline: `(tweakId, operationId)`; se guarda solo la primera vez.
- Test de crash: matar entre Capture y Apply → al reiniciar, revert sigue funcionando.

## 5. Infra (adaptadores a Windows)
| Abstracción | Implementación | Arregla |
|---|---|---|
| `IProcessRunner` | lee stdout/stderr async, exit code, timeout con kill | B5 |
| `IPowerSettings` | `PowerReadACValueIndex` / `PowerWriteACValueIndex` (powrprof) con GUID de esquema explícito; nada de parsear texto | B9 |
| `IBcdStore` | WMI `root\WMI` BcdStore/BcdObject (independiente del idioma); fallback `bcdedit` con exit code | B8 |
| `IRegistry` | Microsoft.Win32 Registry64; para HKCU usa el hive del usuario interactivo (SID de explorer.exe) | B20 |
| `IRestorePoint` | WMI `SystemRestore.CreateRestorePoint` con resultado real | B4 |
| Topología CPU | `GetLogicalProcessorInformationEx` con structs `[StructLayout]` (no offsets a mano) | B3 |

## 6. UI (WinUI)
- ViewModels: `DashboardVm`, `TweaksVm`, `Cs2AdvisorVm`, `BenchmarkVm`, `JournalVm`.
- Nada de `DispatcherTimer` de 1 s refrescando todo: refresh al abrir la vista + al terminar un apply + watchers con debounce 500 ms, **pausados durante el apply**.
- Telemetría (CPU/GPU/DPC) solo si la vista Monitor está visible.
- Partir `MainWindow.xaml(.cs)` (4.000 líneas) en Pages por vista.

## 7. Verificación
- `dotnet build -warnaserror` + `dotnet test` en cada PR (GitHub Actions, `windows-latest`).
- Tests obligatorios: cada `Operation` (apply/read/restore con fakes), engine (partial, failed, revert all = snapshot inicial), journal (atómico, corrupción), topología (buffers grabados de 3 CPUs), parser VDF, parser cs2_video.txt, parser PresentMon CSV.
- E2E manual: VM Hyper-V con checkpoint → preset Profesional → reiniciar → revert all → comparar export de registro. Checklist en `docs/E2E.md` (T62).

## 8. Migración desde v3
No se reescribe todo de cero: se **extrae** a Core lo que sirve (modelos, gates, catálogo) y se reemplaza la mecánica (engine/journal/infra). La UI actual sigue funcionando apuntando al nuevo engine vía ViewModels.
