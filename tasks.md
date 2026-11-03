# tasks.md — KRUZH LATENCY v4

Spec: [specs/001-kruzh-latency-mvp.md](specs/001-kruzh-latency-mvp.md) · Plan: [docs/PLAN.md](docs/PLAN.md) · Bugs: [docs/AUDITORIA.md](docs/AUDITORIA.md) · Decisiones: [docs/decisions/](docs/decisions/)

Regla: cada tarea = 1 rama = 1 PR, con `dotnet build -warnaserror` + `dotnet test` en verde.
`[P]` = paralelizable con las otras `[P]` de la misma fase (worktree propio). Máx. 3 agentes a la vez.

## Fase 0 — Repo
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T00 ✅ | `git init`, `.gitignore`, research a `research/`, README propio | – | commit `d541a0b` |
| T01 ✅ | Crear `KruzhLatency.Core`, `.Infra`, `.Core.Tests` (xUnit) en el `.sln`; renombrar app a `KruzhLatency.App` | T00 | `dotnet test` corre (0 tests) |
| T02 ✅ | GitHub Actions `windows-latest`: build `-warnaserror` + chequeo resources.pri + test | T01 | CI verde |
| T03 ✅ | Hotfix B1: apply en `Task.Run` + try/catch por tweak + reporte `Failed` | T00 | lote con excepción termina sin crash |
| T04 ✅ | Hotfix NVRAM: no autocargar `nvram.txt` desde rutas fijas ni mostrar catálogo antes de importar | T00 | vista vacía hasta importar |

## Fase 1 — Contratos (secuencial, bloquea todo)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| T10 ✅ | Core: `Operation`, `TweakDefinition`, `TweakState`, `ApplyResult`, `Profile` (Normal/Competitivo/Profesional/Entusiasta), interfaces `IRegistry/IProcessRunner/IServiceManager/IPowerSettings/IBcdStore/IRestorePoint/IDeviceManager` + fakes | T01 | compila; fakes testeados |
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

## Pedidos de UI fuera del plan original (2026-11-02) — hechos sobre la app v3, se migran a Pages en T50
| ID | Tarea | Estado |
|---|---|---|
| U1 | Overlay de aplicar + box-loader blanco + splash | ✅ |
| U2 | Juegos: modal solo con opciones reales y aplicables a la PC, hover de cards, solo CS2/VALORANT | ✅ |
| U3 | Independent Flip sin contradicciones FSO/MPO (ADR-006) | ✅ |
| U4 | Sidebar sin "pop" al abrir | ✅ |
| U5 | Optimizaciones minimalista | ✅ |
| U6 | GPU minimalista y dinámica por fabricante | ✅ |
| U7 | Monitor legible (4 indicadores, 1 gráfico, cambios aplicados en lenguaje humano) | ✅ |
| U8 | Quitar del catálogo los placebo de mouse/GPU/audio (M02-M04, G03, I03, F02) — es T31. **G07 (HDCP) se mantiene** por decisión del dueño (spec 002) | pendiente |
| U9 | Ronda 3: sidebar LeftCompact, titlebar unificada, overlay en toda acción, B2, Agregar juego inline, Interrupciones en Optimizaciones, import .nip | ✅ |
| U10 | Monitor: drivers con más latencia (ETW DPC/ISR por driver, base de T70) | ✅ verificado por el usuario |
| U11 | Ronda 4: ancho de vistas fijo, sidebar que empuja con cierre animado, modal propio, medición DPC continua | ✅ |
| U12 | Optimizaciones: pestañas Tweaks · Servicios · Device Manager (catálogo dinámico por PC, perfiles aplican todo) — adelanta T40 y parte de F6 | ✅ |
| U13 | Debloat de apps AppX (pestaña aparte) — T41 | pendiente |

## Spec 002 — DPC al mínimo: Red, Device Manager, USB, GPU y Pantalla
Spec: [specs/002-dpc-red-dispositivos-pantalla.md](specs/002-dpc-red-dispositivos-pantalla.md) · ADR-007 (nunca-tocar + Rescate) · ADR-008 (timer y MPO) · ADR-009 (EDID)
`[P]` = paralelizable dentro de la fase.

### Fase A — Base (secuencial, bloquea todo)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| A1 ✅ | Fix B3: topología de CPU con structs (`PROCESSOR_RELATIONSHIP` mask en offset 32) | – | test con buffers grabados; máscaras no vacías |
| A2 ✅ | `DeviceInventory` en Infra (SetupAPI/CfgMgr32): presentes + no presentes, padre/hijo, IDs, clase, bus/device/function, problem code, driver, servicio | T25 | fixture del árbol del autor exportado y cargado en tests |
| A3 ✅ | Fixtures reales del autor: árbol de dispositivos (JSON), EDID del GS25F2, opciones de la RTL8125 (`Ndi\Params`) | A2 | archivos en `KruzhLatency.Core.Tests/Fixtures` |
| A4 ✅ | Lista de nunca-tocar en Core (ADR-007) + tests contra el fixture | A2, A3 | ningún bridge/GPU activa/USB del mouse/NIC con enlace/audio default/TPM sale como "deshabilitable" |
| A5 ✅ | `System.Management` en Infra + adaptador WMI (`MSPower_DeviceEnable`, SystemRestore real) | – | test de integración marcado |

### Fase B — Reglas y módulos `[P]` (después de A)
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| B1 ✅ | Motor de reglas de dispositivos: hermanos de GPU (audio fn1, xHCI USB-C, UCSI), NVPCF/Virtual Audio, nodos XInput/HID game controller de teclados, Wi-Fi con Ethernet conectado, 2ª NIC sin enlace, HPET/Wavetable/RRAS/Print Queue/Voice Clarity/MIDI 2.0 | A4 | tests con fixture: detecta lo del chat |
| B2 ✅ | Fantasmas: listado por categoría + `pnputil /remove-device` (desde Competitivo, restore point antes) | A2 | test con fixture; UI avisa que es irreversible |
| B3 ✅ | Dispositivos con error: Code 43 (deshabilitar + aviso físico), Code 28 (link driver chipset) | A2 | test con fixture (RAID Controller, Unknown USB Device) |
| B4 ✅ | Red por chip: tabla RTL8125/RTL8111/I225/I226/I219/Killer, solo keywords y valores permitidos, Speed/Duplex = negociada, reinicio inmediato del adaptador | A3 | tests con `Ndi\Params` del fixture |
| B5 ✅ | Red: detección de filtros NDIS (cFos, MSI LAN Manager, Dragon Center) y driver NetAdapterCx vs NDIS → recomendación con link | A2 | test con fixture |
| B6 ✅ | USB/HID: EnhancedPowerManagement + AllowIdleIrpInD3 en todo USB; casilla "permitir apagar" off vía WMI; consejo de puerto CPU vs chipset | A2, A5 | lectura posterior confirma valores |
| B7 ✅ | GPU: HDCP off (G07 vuelve), MPO estable `OverlayMinFPS=0` (ADR-008), PROCTHROTTLEMIN 100 en el plan | – | tweaks en catálogo con Source |
| B8 ✅ | Timer (ADR-008): fuera de perfiles en W11; experimentales en Entusiasta; limpiar `useplatformclock` | – | presets actualizados |
| B9 ✅ | Hyper-V/VBS off solo Entusiasta + modal anticheat genérico para todo ítem con impacto | – | todo ítem con `AnticheatGate` inseguro abre modal |
| B10 🟡 | Rescate (ADR-007): reactiva lo deshabilitado por KRUZH | A2 | prueba manual en VM (implementado; falta la prueba en VM) |

### Fase C — Pantalla
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| C1 ✅ | Parser EDID en Core: base, Established/Standard/Detailed, CTA-861 (data blocks), DisplayID 1.3/2.0 (Type I/VII), Hz exactos | A3 | fixture GS25F2: 200.000 vs 200.003 Hz |
| C2 ✅ | Infra: monitores reales/fantasma, EDID y `EDID_OVERRIDE` del registro, modo actual (QueryDisplayConfig), cálculo DSC | A2 | lectura en la PC del autor |
| C3 [P] | Sidebar "Pantalla" (ícono monitor): análisis completo del EDID (timings, data blocks, extensiones) | C1, C2 | revisión manual |
| C4 | Limpieza: monitores fantasma + `GraphicsDrivers\Configuration/Connectivity` con export previo, reinicio | C2, B2 | revisión manual |
| C5 | Modo manual: guía de CRU generada con datos reales + panel NVIDIA + OSD + recuperación + Verificar | C3 | revisión manual |
| C6 | Modo automático: generador de EDID (Core, tests) + escritura `EDID_OVERRIDE` + auto-rollback por tarea programada (60 s) | C1, C5 | tests del generador + prueba en VM |

### Fase D — Monitor con diagnóstico
| ID | Tarea | Deps | Hecho cuando |
|---|---|---|---|
| D1 | Objetivos (pico ≤ 250–300 µs, promedio < 8 µs) y mapeo driver → causa → botón "Arreglar" | B1, B4, B6 | cada driver alto tiene acción |
| D2 | Mientras mide: sin gráfico en vivo + sugerencia de minimizar | – | dxgkrnl de la propia app no suma |

### Orden sugerido
1. A1 → A2 → A3 → A4 (+ A5 en paralelo)
2. B1 · B4 · B7 en paralelo → B2 · B3 · B5 · B6 → B8 · B9 · B10
3. C1 → C2 → C3 → C4 → C5 → C6
4. D1 · D2
