# tasks.md — KRUZH LATENCY v4

Spec: [specs/001-kruzh-latency-mvp.md](specs/001-kruzh-latency-mvp.md) · Plan: [docs/PLAN.md](docs/PLAN.md) · Bugs: [docs/AUDITORIA.md](docs/AUDITORIA.md)

Regla: cada tarea = 1 rama = 1 PR, con `dotnet build -warnaserror` + `dotnet test` en verde.
`[P]` = se puede correr en paralelo con las otras `[P]` de la misma fase (worktree propio).

## Fase 0 — Repo (secuencial, la hacés vos o 1 agente)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T00 | `git init`, `.gitignore` (bin/obj/publish/*.log/*.json grandes), mover research (`*.py`, json de scraping, txt, ps1 legacy, `test_bb.html`) a `research/` excluido del build | – | `git status` limpio, solo código + docs |
| T01 | Crear `KruzhLatency.Core`, `KruzhLatency.Infra`, `KruzhLatency.Core.Tests` (xUnit) y agregarlos al `.sln`; renombrar app a `KruzhLatency.App` | T00 | `dotnet test` corre (0 tests) |
| T02 | GitHub Actions: `windows-latest`, build `-warnaserror` + test | T01 | CI verde en un PR de prueba |
| T03 | Hotfix B1 en la app actual: apply en `Task.Run` + try/catch por tweak + reporte `Failed` | T00 | lote con un tweak que tira excepción termina sin crash |

## Fase 1 — Contratos (secuencial; bloquea todo lo demás)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T10 | Definir en Core: `Operation`, `TweakDefinition`, `TweakState`, `ApplyResult`, interfaces `IRegistry/IProcessRunner/IServiceManager/IPowerSettings/IBcdStore/IRestorePoint` + fakes en Tests | T01 | compila; fakes con tests propios |
| T11 | Journal v2 (atómico, `schemaVersion`, baseline por `(tweakId, opId)`) | T10 | tests: baseline único, write atómico, journal corrupto → `.bak` |

## Fase 2 — Núcleo `[P]` (después de T10/T11)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T20 [P] | `TweakEngine`: Evaluate/Plan/Apply/Verify/Revert, estado `Partial`, aislamiento de errores, `IProgress` + `CancellationToken` | T10, T11 | tests: partial se completa; error no corta lote; revert-all == snapshot inicial (arregla B1, B2, B7) |
| T21 [P] | Operaciones concretas: `RegistryValueOp`, `ServiceStartOp`, `ScheduledTaskOp`, `IfeoOp`, `NicAdvancedPropertyOp` (solo keywords existentes en `Ndi\Params`) | T10 | tests por operación con FakeRegistry (B12, B13 merge de string) |
| T22 [P] | Infra: `ProcessRunner` (exit code, stderr, kill en timeout), `RegistryAdapter` (hive del usuario interactivo), `RestorePoint` vía WMI | T10 | tests de integración marcados `[Trait("Integration")]` (B4, B5, B20) |
| T23 [P] | Infra: `PowerSettings` vía powrprof (GUID explícito) + `BcdStore` vía WMI | T10 | lectura idéntica en es-ES y en-US (B8, B9) |
| T24 [P] | Hardware: topología con structs (`[StructLayout]`), GPU discreta real, storage real, RAM canales/velocidad (SMBIOS), monitor Hz actual vs máx; borrar rutas hardcodeadas | T10 | tests con buffers grabados: Intel híbrido, Ryzen 1-CCD, Ryzen 2-CCD X3D (B3, B15–B18) |

## Fase 3 — Catálogo (después de T20 + T21)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T30 | Migrar tweaks ✅/🟡 de la auditoría al formato declarativo, 1 archivo por categoría; `Why`/`Source` obligatorios | T20, T21, T23 | test: toda definición tiene ≥1 op, Source no vacío, Describe() == operaciones |
| T31 [P] | Borrar tweaks ❌ y sus fallbacks `*_Rev` | T30 | grep `_Rev` = 0 |
| T32 [P] | Presets recalculados (Seguro/Competitivo/Profesional/Entusiasta) + gates laptop | T30 | tests de membresía por perfil de hardware |
| T33 [P] | Energía: una sola fuente de verdad (plan KRUZH creado una vez; C06/S02/USB suspend como `PowerSettingOp` sobre ese GUID) | T23, T30 | test: cambiar plan no deja journal huérfano (B10) |

## Fase 4 — UI (después de T20; en paralelo con Fase 3 si el contrato no cambia)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T40 | Partir `MainWindow` en Pages + ViewModels; bindear al nuevo engine | T20 | MainWindow.xaml.cs < 300 líneas |
| T41 [P] | Quitar polling: refresh on-demand + watchers con debounce, pausados durante apply; telemetría solo con vista visible | T40 | app minimizada < 0,1 % CPU (B19) |
| T42 [P] | Dashboard honesto: checklist + métricas; borrar score "FACEIT LEVEL 10" | T40 | (B22) |
| T43 [P] | Flujo de restore point: mostrar resultado real, confirmar si falla | T22, T40 | (B4) |
| T44 [P] | Quitar licencia/anti-debug y descarga de drivers (ADR-003); driver = versión + link | T40 | (B6, B23) |

## Fase 5 — Valor CS2 `[P]` (después de T24)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T50 [P] | Detección CS2: parser VDF real, múltiples bibliotecas | T10 | tests con `libraryfolders.vdf` de ejemplo |
| T51 [P] | Asesor CS2 solo lectura: `cs2_video.txt`/convars (Reflex, fps_max, fullscreen, res/Hz) | T50 | tests con archivos de ejemplo |
| T52 [P] | Medición A/B: lanzar PresentMon, parsear CSV, avg/1 % low/p99, reporte antes/después | T10 | tests del parser con CSV grabado |
| T53 | Vistas UI para T51/T52 | T40, T51, T52 | revisión manual |

## Fase 6 — Release
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T60 | README propio, créditos a fuentes (PC-Tuning, etc.), revisar licencias de `.pow`/nvidiaProfileInspector | T31 | – |
| T61 | `build-release.ps1` limpio + firma Authenticode + instalador | T44 | exe firmado instala/desinstala limpio |
| T62 | `docs/E2E.md` + corrida en VM Hyper-V con checkpoint: preset → reboot → revert all → diff registro vacío | todo | checklist firmado |

## Orden sugerido con agentes en paralelo (máx. 3)
1. T00 → T01 → T02 (+ T03 hotfix) → T10 → T11
2. Paralelo: **T20 | T21 | T24**
3. Paralelo: **T22 | T23 | T50**
4. T30 → paralelo: **T31 | T32 | T33** — y en otro worktree **T40**
5. Paralelo: **T41 | T42 | T51 | T52** → T43, T44, T53
6. T60 → T61 → T62
