# tasks.md — KRUZH LATENCY v4

Spec: [specs/001-kruzh-latency-mvp.md](specs/001-kruzh-latency-mvp.md) · Plan: [docs/PLAN.md](docs/PLAN.md) · Bugs: [docs/AUDITORIA.md](docs/AUDITORIA.md) · Decisiones: [docs/decisions/](docs/decisions/)

Regla: cada tarea = 1 rama = 1 PR, con `dotnet build -warnaserror` + `dotnet test` en verde.
`[P]` = paralelizable con las otras `[P]` de la misma fase (worktree propio). Máx. 3 agentes a la vez.

## Fase 0 — Repo
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T00 ✅ | `git init`, `.gitignore`, research a `research/`, README propio | – | commit `d541a0b` |
| T01 ✅ | Crear `KruzhLatency.Core`, `.Infra`, `.Core.Tests` (xUnit) en el `.sln`; renombrar app a `KruzhLatency.App` | T00 | `dotnet test` corre (0 tests) |
| T02 🟡 | GitHub Actions `windows-latest`: build `-warnaserror` + test (workflow escrito; falta remoto de GitHub para verlo correr) | T01 | CI verde |
| T03 ✅ | Hotfix B1: apply en `Task.Run` + try/catch por tweak + reporte `Failed` | T00 | lote con excepción termina sin crash |
| T04 | Hotfix NVRAM: no autocargar `nvram.txt` desde rutas fijas ni mostrar catálogo antes de importar | T00 | vista vacía hasta importar |

## Fase 1 — Contratos (secuencial, bloquea todo)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T10 | Core: `Operation`, `TweakDefinition`, `TweakState`, `ApplyResult`, `Profile` (Normal/Competitivo/Profesional/Entusiasta), interfaces `IRegistry/IProcessRunner/IServiceManager/IPowerSettings/IBcdStore/IRestorePoint/IDeviceManager` + fakes | T01 | compila; fakes testeados |
| T11 | Journal v2 atómico (`schemaVersion`, baseline `(tweakId, opId)`) | T10 | tests baseline único / atómico / corrupción |
| T12 | Lista de servicios/features protegidos por anticheat (FACEIT, Vanguard) + críticos de Windows, versionada, con fuentes | T10 | documento + test que el perfil Profesional no la toca |

## Fase 2 — Núcleo `[P]`
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T20 [P] | `TweakEngine`: Evaluate/Plan/Apply/Verify/Revert, `Partial`, aislamiento de errores, `IProgress` + cancelación | T10, T11 | revert-all == snapshot inicial (B1, B2, B7) |
| T21 [P] | Operaciones: `RegistryValueOp`, `ServiceStartOp`, `ScheduledTaskOp`, `IfeoOp`, `NicAdvancedPropertyOp` (solo keywords de `Ndi\Params`), `NetshGlobalOp` con lectura previa | T10 | tests por operación (B11, B12, B13) |
| T22 [P] | Infra: `ProcessRunner`, `RegistryAdapter` (hive del usuario interactivo), `RestorePoint` WMI | T10 | tests `[Trait("Integration")]` (B4, B5, B20) |
| T23 [P] | Infra: `PowerSettings` vía powrprof + `BcdStore` vía WMI | T10 | igual en es-ES / en-US (B8, B9) |
| T24 [P] | Hardware: topología con structs, GPU discreta, storage, RAM canales/velocidad, monitor Hz, sin rutas fijas | T10 | buffers grabados Intel híbrido / Ryzen 1-CCD / 2-CCD X3D (B3, B15–B18) |
| T25 [P] | Infra `DeviceManager`: enumerar PnP con código de error, driver (proveedor, versión, fecha, INF), capacidades PCI de interrupción, disable/enable de devnode | T10 | tests con datos simulados |

## Fase 3 — Catálogo y perfiles
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T30 | Migrar tweaks al formato declarativo. **Red: se mantienen todos** (N01–N08). Fuera: M02, M03, M04, G03, G07, I03, F02 | T20, T21, T23 | test: toda definición con ops + Source + Describe()==ops |
| T31 [P] | Borrar código viejo de tweaks quitados y fallbacks `*_Rev` | T30 | grep `_Rev` = 0 |
| T32 [P] | Perfiles Normal/Competitivo/Profesional/Entusiasta + gates laptop + gates anticheat | T30, T12 | tests de membresía |
| T33 [P] | Energía: una sola fuente de verdad (plan KRUZH + `PowerSettingOp`) | T23, T30 | sin journal huérfano (B10) |
| T34 [P] | CS2 + VALORANT: detección de exe (Steam VDF multi-biblioteca, Riot Client), IFEO, GPU pref, FSO, CPU sets — **sin tocar archivos del juego** | T21, T24 | test: ninguna op apunta a carpetas del juego (ADR-004) |
| T35 [P] | `.nip` NVIDIA: export del perfil actual antes de importar, perfiles por exe de CS2/VALORANT, revert | T21 | test revert restaura el export |

## Fase 4 — Servicios, debloat, drivers `[P]`
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T40 [P] | Catálogo de servicios por perfil (nombre, función, motivo, riesgo) usando `ServiceStartOp`; respeta T12 | T12, T21 | lista por perfil == ops aplicadas |
| T41 [P] | Debloat: AppX por usuario (reversible re-registrando), tareas de telemetría, widgets/Copilot/sugerencias; desaprovisionar solo Entusiasta | T21 | tests con fakes; Profesional sin ops no reversibles |
| T42 [P] | Auditor de drivers: errores (10/28/43…), sin driver, driver genérico donde hay de fabricante, catálogo local de versiones problemáticas | T25 | tests con dispositivos simulados |

## Fase 5 — MSI auto-tune (riesgo alto, empieza con spike)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T70 | **Spike**: ETW ISR/DPC por driver con TraceEvent (pedir aprobación del NuGet) + reinicio de devnode en caliente en tu PC; medir ruido entre corridas | T25 | informe: precisión, ruido, qué dispositivos se reinician en caliente |
| T71 | `MsiTuner` en Core: espacio de búsqueda por dispositivo (modo × límite ≤ máx × prioridad), orden de prueba, criterio p99 + margen de ruido | T70 | tests con mediciones simuladas |
| T72 | Ejecución: aplicar combinación → reiniciar devnode → verificar sin error → medir → rollback automático si falla; modo con reinicios guiados para no-reiniciables | T71, T22 | prueba manual en VM + en PC real con punto de restauración |
| T73 | Generadores de carga reproducible (GPU, NIC UDP local, storage, USB guiado) | T70 | varianza documentada |

## Fase 6 — UI
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T50 | Partir `MainWindow` en Pages + ViewModels, bindear al engine | T20 | MainWindow.xaml.cs < 300 líneas |
| T51 [P] | Progreso por tweak en background, quitar polling, watchers con debounce pausados en apply | T50 | minimizada < 0,1 % CPU (B19) |
| T52 [P] | Vista Perfiles → lista de servicios/debloat a aplicar, destildar, 1 click | T50, T40, T41 | revisión manual |
| T53 [P] | Vista Drivers | T50, T42 | revisión manual |
| T54 [P] | Vista MSI auto-tune: progreso, reporte por dispositivo, revert | T50, T72 | revisión manual |
| T55 [P] | Dashboard honesto (sin score inventado), restore point real | T50, T22 | (B4, B22) |
| T56 [P] | Quitar descarga de drivers / licencia vieja / anti-debug | T50 | (B6, B23) |

## Fase 7 — BIOS / NVRAM (solo consejo) `[P]` con Fase 2+
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T60 [P] | Parser SCEWIN `nvram.txt` (Setup Question, opciones, valor actual `*`) | T10 | test con `research/data/nvram.txt` copiado a fixtures |
| T61 [P] | Base de datos de settings AMI: nombre + variantes por fabricante, recomendación por perfil/plataforma (Intel/AMD, X3D, laptop), motivo, fuente | T60 | ≥ todos los settings conocidos del research cubiertos |
| T62 | Vista NVRAM: vacía hasta importar; Bien / Cambiar a X / Sin recomendación; sin export | T50, T61 | revisión manual |

## Fase 8 — Release
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T80 | Licencias de terceros (`.pow`, nvidiaProfileInspector), créditos | T31 | – |
| T81 | Build firmado + instalador | T56 | instala/desinstala limpio |
| T82 | E2E en VM Hyper-V: perfil → reboot → revert all → diff vacío; prueba FACEIT y Vanguard con perfil Profesional | todo | checklist firmado |
| T83 | Licencias (spec propia, Ed25519) | T81 | spec 00X aprobada |

## Orden sugerido
1. T01 → T02 · en paralelo hotfixes **T03 | T04**
2. T10 → T11 → T12
3. **T20 | T21 | T24** → **T22 | T23 | T25** (+ T60 en paralelo)
4. T30 → **T31 | T32 | T33 | T34 | T35** (de a 3) · T70 spike en paralelo
5. **T40 | T41 | T42** · T50 → **T51 | T52 | T55** · T61
6. T71 → T72/T73 → T54 · T53, T56, T62
7. T80 → T81 → T82 → T83
