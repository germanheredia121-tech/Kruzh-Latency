# Auditoría KRUZH LATENCY — 2026-11-02

Estado analizado: `src/KruzhLatency` (WinUI 3, .NET 8, ~8.000 líneas C#) + scripts legacy en la raíz.
Build: `dotnet build` OK, 0 warnings. **No hay tests, no hay git, no hay CI.**

> Verdad incómoda primero: "0 input lag" no existe. Lo que sí se puede lograr es **minimizar la latencia del sistema y la varianza de frametime**. Las ganancias grandes en CS2 vienen de: NVIDIA Reflex On+Boost, cap de FPS estable, fullscreen, monitor a su Hz máximo, drivers limpios y BIOS (XMP/EXPO, C-states). Los tweaks de registro aportan poco y varios son placebo. El producto tiene que ser **honesto y medible**, si no es un "tweaker más".

---

## 1. Lo que está BIEN (mantener)

| Qué | Dónde | Por qué |
|---|---|---|
| Journal de backup por valor (guarda el valor original **una sola vez**, no pisa el baseline) | [TransactionalAuditService.cs:77-102](../src/KruzhLatency/KruzhLatency/Services/TransactionalAuditService.cs#L77-L102) | Es la idea correcta para reversibilidad real. |
| Revert no destructivo (borra el *valor*, nunca la rama) | [TransactionalAuditService.cs:477-500](../src/KruzhLatency/KruzhLatency/Services/TransactionalAuditService.cs#L477-L500) | Evita el clásico "borré GameConfigStore y rompí Windows". |
| Modelo "Triple Gate" (Aplicable / Recomendado / Anticheat) | [DeclarativeTweakEngine.cs:33-36](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L33-L36) | Buena idea de producto: el tweak se adapta al hardware. |
| Estado "pendiente" (desired vs live) antes de aplicar | [DeclarativeTweakEngine.cs:92-98](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L92-L98) | UX correcta: el usuario ve qué va a cambiar. |
| Advertencia roja para tweaks que bajan seguridad / riesgo anticheat | [MainWindow.xaml.cs:790-802](../src/KruzhLatency/KruzhLatency/MainWindow.xaml.cs#L790-L802) | Imprescindible si se vende. |
| Inicialización en background + splash | [App.xaml.cs:42-90](../src/KruzhLatency/KruzhLatency/App.xaml.cs#L42-L90) | Correcto. |
| Detección de CS2 vía Steam + `libraryfolders.vdf` | [DeclarativeTweakEngine.cs:2042-2074](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L2042-L2074) | Bien encarado (parser frágil, ver §2). |
| Self-contained build + SHA256 | [build-release.ps1](../src/KruzhLatency/build-release.ps1) | Base buena para distribuir. |

---

## 2. BUGS (ordenados por severidad)

### Críticos — rompen el producto o el sistema del usuario

| # | Bug | Dónde | Consecuencia |
|---|---|---|---|
| B1 | **Aplicar corre en el hilo de UI y sin try/catch por tweak.** Una excepción corta el lote a la mitad y crashea la app. | [MainWindow.xaml.cs:828-867](../src/KruzhLatency/KruzhLatency/MainWindow.xaml.cs#L828-L867) | Es el crash real del log (`ArgumentException ... RegistryValueKind`, `startup_diagnostic.log` 2026-11-02 18:53). Además la UI se congela (bcdedit/netsh/powershell con waits de 8 s). Sistema queda a medio aplicar. |
| B2 | **"Revertir todo" re-aplica tweaks.** Los fallbacks de revert escriben al journal con IDs falsos (`MouseAccelOff_Rev`, `MarkC_Rev`, `HAGS_Rev`, …). `RevertAllFromJournal` después "revierte" esos IDs → restaura los valores tweakeados. | [DeclarativeTweakEngine.cs:680-686](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L680-L686), [2281-2430](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L2281-L2430), [TransactionalAuditService.cs:454-475](../src/KruzhLatency/KruzhLatency/Services/TransactionalAuditService.cs#L454-L475) | El botón de pánico hace lo contrario de lo que promete. |
| B3 | **Offset incorrecto al leer la topología de CPU.** En `PROCESSOR_RELATIONSHIP` la máscara está en offset 32, el código lee 40. | [HardwareChassisService.cs:278](../src/KruzhLatency/KruzhLatency/Services/HardwareChassisService.cs#L278) | Todas las máscaras dan 0 → `Topology` vacía → `ReservedCpuSets` nunca aplicable, IRQ affinity cae al fallback y puede pinnear la GPU a un hilo SMT o E-core. |
| B4 | **Punto de restauración siempre "creado ✓"** aunque falle (output ignorado, System Protection apagada, límite de 24 h). | [TransactionalAuditService.cs:547-551](../src/KruzhLatency/KruzhLatency/Services/TransactionalAuditService.cs#L547-L551), [MainWindow.xaml.cs:817-818](../src/KruzhLatency/KruzhLatency/MainWindow.xaml.cs#L817-L818) | El usuario cree que tiene red de seguridad y no la tiene. |
| B5 | **`RunHiddenProcess` ignora exit code, no lee stderr (posible deadlock), no mata el proceso en timeout.** Todos los errores de bcdedit/powercfg/netsh/schtasks quedan silenciados. | [TransactionalAuditService.cs:560-585](../src/KruzhLatency/KruzhLatency/Services/TransactionalAuditService.cs#L560-L585) | "Aplicado ✓" sin haber aplicado nada. |
| B6 | **Licencia inexistente en la práctica:** cualquier clave que empiece con `KRUZH-` y tenga ≥14 chars activa; además al primer arranque se auto-emite un token "AUTHOR-MASTER"; la clave HMAC está en el binario. | [SecurityServices.cs:177-182](../src/KruzhLatency/KruzhLatency/Services/SecurityServices.cs#L177-L182), [266-268](../src/KruzhLatency/KruzhLatency/Services/SecurityServices.cs#L266-L268), [223-227](../src/KruzhLatency/KruzhLatency/Services/SecurityServices.cs#L223-L227) | Si se vende, cualquiera lo usa gratis. El anti-debug solo agrega falsos positivos de antivirus. |

### Altos — estado mentiroso o revert incorrecto

| # | Bug | Dónde |
|---|---|---|
| B7 | **Detección de estado parcial**: cada `ReadLiveState` mira 1 valor de N. Si ese valor ya está, `ExecuteApply` hace skip "idempotente" y el resto nunca se aplica. Ej: `ServicesAndTasksDebloat` solo mira DiagTrack. | [DeclarativeTweakEngine.cs:589-601](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L589-L601), [1076](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L1076) |
| B8 | **BCD: "aplicado" = el output contiene "disabledynamictick" Y la palabra "Yes" en cualquier línea.** En Windows en español es "Sí". Además el backup guarda "Sí" y el revert hace `bcdedit /set ... Sí` (falla). | [DeclarativeTweakEngine.cs:893-897](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L893-L897), [TransactionalAuditService.cs:219-232](../src/KruzhLatency/KruzhLatency/Services/TransactionalAuditService.cs#L219-L232) |
| B9 | **powercfg: parseo por texto localizado (solo EN/ES)**; si no parsea, guarda baseline `"0"` y el revert escribe 0. Además usa `scheme_current`: si después cambiás de plan, el revert toca otro plan. | [TransactionalAuditService.cs:260-295](../src/KruzhLatency/KruzhLatency/Services/TransactionalAuditService.cs#L260-L295) |
| B10 | **Dos fuentes de verdad para energía**: el tweak C06/S02 modifica `scheme_current` y la vista "Planes" crea/borra planes propios y los activa → pisa C06/S02 y deja el journal apuntando a un plan borrado. | [PowerCatalogService.cs:129-216](../src/KruzhLatency/KruzhLatency/Services/PowerCatalogService.cs#L129-L216) vs [DeclarativeTweakEngine.cs:1001-1006](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L1001-L1006) |
| B11 | Acciones sin backup: `powercfg /h off`, `netsh rsc` (baseline hardcodeado "enabled"), `netsh rss/ecn`, `w32tm`, perfil NVIDIA (.nip importado sobre el Base Profile sin exportar el anterior). | [DeclarativeTweakEngine.cs:1455-1456](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L1455-L1456), [1788](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L1788), [NvidiaAndDriversService.cs:109-122](../src/KruzhLatency/KruzhLatency/Services/NvidiaAndDriversService.cs#L109-L122) |
| B12 | **NIC: escribe keywords que el driver no tiene** (`*EEE`, `GigaLite`, `ULPMode`… como string "0" en todas las placas, incluidas USB/WiFi). Deja basura en el registro del driver. Debe escribir solo keywords existentes en `Ndi\Params`. | [DeclarativeTweakEngine.cs:1312-1320](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L1312-L1320) |
| B13 | `DirectXUserGlobalSettings` se **sobrescribe entero** (borra VRR optimizations / Auto HDR del usuario). `gpupreference=2` no es válido a nivel global. | [DeclarativeTweakEngine.cs:1233](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L1233) |
| B14 | Journal se escribe con `File.WriteAllText` (no atómico). Corte de luz durante el apply = journal corrupto = se pierden los originales. | [TransactionalAuditService.cs:70-75](../src/KruzhLatency/KruzhLatency/Services/TransactionalAuditService.cs#L70-L75) |

### Medios — detección de hardware y rendimiento de la propia app

| # | Bug | Dónde |
|---|---|---|
| B15 | Rutas hardcodeadas a tu escritorio (`c:\Users\kruzh\...\nvram.txt`, `nvidiaProfileInspector.exe`). En otra PC lee archivos ajenos o nada. **Además `nvram.txt` de tu PC decide el "modo OC" de cualquier usuario.** | [HardwareChassisService.cs:430](../src/KruzhLatency/KruzhLatency/Services/HardwareChassisService.cs#L430), [NvidiaAndDriversService.cs:50](../src/KruzhLatency/KruzhLatency/Services/NvidiaAndDriversService.cs#L50) |
| B16 | Storage siempre `NvmeOrSsd` (los dos lados del ternario son iguales). | [HardwareChassisService.cs:546](../src/KruzhLatency/KruzhLatency/Services/HardwareChassisService.cs#L546) |
| B17 | `RamModulesCount = 2` hardcodeado → "Dual Channel" siempre, aunque tengas 1 stick. | [HardwareProfile.cs:86](../src/KruzhLatency/KruzhLatency/Models/HardwareProfile.cs#L86) |
| B18 | GPU = primer adaptador de la clase Display (puede ser la iGPU o un adaptador virtual tipo Parsec/Meta). `GpuPnpInstanceId` queda con el **último** Display. | [HardwareChassisService.cs:457-519](../src/KruzhLatency/KruzhLatency/Services/HardwareChassisService.cs#L457-L519) |
| B19 | **La app genera la latencia que dice eliminar**: cada 1 s timer en UI; cada 3 s `RefreshAll()` **dos veces** (también dentro de `CalculateDynamicScore`), cada uno enumera todo `Enum\PCI` y lanza `bcdedit.exe`. `CalculateDynamicScore` además llama `SampleCpuAndKernelLoad` y corrompe el cálculo de CPU %. Los registry watchers disparan refresh por los cambios de la propia app. | [MainWindow.xaml.cs:1945-1977](../src/KruzhLatency/KruzhLatency/MainWindow.xaml.cs#L1945-L1977), [410-425](../src/KruzhLatency/KruzhLatency/MainWindow.xaml.cs#L410-L425), [NvramAndMonitorService.cs:486](../src/KruzhLatency/KruzhLatency/Services/NvramAndMonitorService.cs#L486), [512](../src/KruzhLatency/KruzhLatency/Services/NvramAndMonitorService.cs#L512) |
| B20 | Tweaks HKCU con app elevada: si un usuario estándar eleva con otra cuenta admin, los tweaks de mouse/GameDVR van al perfil del admin, no al del jugador. | Global (todas las escrituras `RegistryHive.CurrentUser`) |
| B21 | Descripciones que mienten sobre lo que hace el código: C06 dice `PERFBOOSTMODE=2` (no lo aplica); I03 dice "APO FxProperties" (no lo toca); M01 revert no restaura `MouseSensitivity`. | [DeclarativeTweakEngine.cs:978](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L978), [1677](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L1677), [682-685](../src/KruzhLatency/KruzhLatency/Services/DeclarativeTweakEngine.cs#L682-L685) |
| B22 | Score gamificado y en parte falso ("FACEIT LEVEL 10 / RADIANT", dual channel hardcodeado, 5 puntos regalados). | [NvramAndMonitorService.cs:482-553](../src/KruzhLatency/KruzhLatency/Services/NvramAndMonitorService.cs#L482-L553) |
| B23 | Manifest de drivers apunta a `api.kruzhlatency.com` (no existe) y "descarga verificada" en realidad abre la web del fabricante (`DYNAMIC_OFFICIAL` saltea el hash). | [NvidiaAndDriversService.cs:186](../src/KruzhLatency/KruzhLatency/Services/NvidiaAndDriversService.cs#L186), [272-283](../src/KruzhLatency/KruzhLatency/Services/NvidiaAndDriversService.cs#L272-L283) |

---

## 3. Veredicto por tweak (pensando en CS2 competitivo)

> **Decisión del dueño (2026-11-02), prevalece sobre esta tabla:** se **mantienen todos los tweaks de red** (N01–N08, incluidos N03, N04, N06, N08). Se **quitan** M02, M03, M04 (mouse), G03, G07 (GPU) e I03 (audio). F02 también sale. Ver spec 001 v2.
> **Presentación (ADR-006):** todo apunta a Independent Flip. G05 rehecho (flip model + FSO activo), G06 fuera de perfiles (solo troubleshooting), F01 ya no desactiva FSO.

Criterio: **¿hay mecanismo documentado + efecto medible en CS2?** CS2 usa UDP, DX11 por defecto, soporta Reflex nativo.

| Código | Tweak | Veredicto | Motivo |
|---|---|---|---|
| M01 | Mouse accel OFF | ✅ Mantener | Efecto real (1:1). Corregir revert. |
| M02 | MarkC curves | ❌ Quitar | Con "Enhance Pointer Precision" OFF (M01) las curvas no se usan. No hace nada. |
| M03 | Mouse/Kbd DataQueueSize | ❌ Quitar | Es tamaño de buffer, no agrega latencia; bajarlo arriesga perder eventos a 4-8 kHz. |
| M04 | RawMouseThrottleDuration | ❌ Quitar | Solo afecta ventanas en **segundo plano**; CS2 está en foreground. |
| C01 | Win32PrioritySeparation | 🟡 Mantener como opción | Efecto chico; dejar 0x26 como default, marcar "medir". |
| C02 | GlobalTimerResolutionRequests | ✅ Mantener | Real en Win11/Server 2022+. Falta gate de build. |
| C03 | disabledynamictick | 🟡 Opcional | Evidencia mixta. Arreglar lectura BCD (B8). |
| C04 | ReservedCpuSets | 🟡 Avanzado | Útil solo en Intel híbrido / Ryzen dual-CCD. Hoy roto (B3). En X3D dual-CCD hay que reservar el CCD **sin** V-Cache, verificar. |
| C05 | PowerThrottling OFF | ✅ Mantener (desktop) | |
| C06 | Idle disable + core parking | ✅ Mantener (desktop, opt-in Profesional) | Unificar con planes (B10). |
| C07 | Game DVR OFF | ✅ Mantener | |
| C08 | GameMode + Copilot + VisualFX | ✂️ Partir | Game Mode ON se queda. Copilot/VisualFX no son latencia → sacar o mover a "Debloat". |
| C09 | Telemetría servicios/tareas | 🟡 Mantener fuera de presets competitivos | Impacto mínimo; reversible. |
| G01 | HAGS | ✅ Mantener como selector ON/OFF | Depende de GPU/driver; medir. |
| G02 | DisableDynamicPstate | 🟡 Avanzado desktop | Sube consumo idle; medir. |
| G03 | "NVIDIA ULL registry" (`EnableRID73777`, PowerMizer en `Global`, NvTray) | ❌ Quitar | Claves no documentadas / NvTray no existe en drivers actuales. Placebo. |
| G04 | AMD ULPS off | 🟡 Mantener (solo multi-GPU/stutter conocido) | |
| G05 | DXGI flip / FSE | 🔧 Rehacer | Mezcla dos cosas opuestas (forzar flip model + forzar FSE). Hacerlo selector y **merge** del string, no overwrite (B13). |
| G06 | MPO off | 🟡 Mantener con gate (no recomendado en 24H2+) | |
| G07 | HDCP off | ❌ Quitar | No afecta latencia. Rompe streaming DRM. |
| N01 | NIC power saving | ✅ Mantener | Solo keywords existentes (B12). |
| N02 | Interrupt moderation | ✅ Mantener como selector | `*InterruptModerationRate` no es estándar; usar valores de `Ndi\Params`. |
| N03 | Ring buffers | ❌ Quitar | Sin beneficio de latencia medible; arriesga drops. |
| N04 | LSO / RSC / FlowControl | ❌ Quitar | Afecta TCP; CS2 juega por UDP. |
| N05 | MMCSS / NetworkThrottlingIndex | 🟡 Opcional | Solo aplica a procesos registrados en MMCSS. Efecto chico. |
| N06 | Nagle / TcpAckFrequency / ECN | ❌ Quitar | Nagle es TCP. CS2 = UDP. Placebo. |
| N07 | QoS DSCP 46 | 🟡 Mantener, explicado | Solo sirve si tu router hace QoS/SQM; el ISP borra el DSCP. |
| N08 | NTP | ❌ Quitar | El reloj de Windows no influye en el desync de CS2 (subtick usa su propio timing). |
| I01 | MSI mode | 🟡 Mantener avanzado | GPUs NVIDIA modernas ya usan MSI. Mostrar estado, no forzar en todo. |
| I02 | IRQ affinity | 🟡 Avanzado | Depende de arreglar B3. Sin medición, puede empeorar. |
| I03 | Audio "ProtectedAudioDG" | ❌ Quitar | Clave no documentada; lo que dice hacer (APO) no está implementado. |
| S01 | USB/HID selective suspend | ✅ Mantener | Mejor vía powercfg (USB selective suspend) + flag por dispositivo. |
| S02 | ASPM | 🔧 Unificar | Solo la parte powercfg es real; las claves `pci\Parameters\EnableASPM` no están documentadas. Mover al plan de energía. |
| S03 | Fast Startup/hibernación off | ✅ Mantener | Arreglar revert de `/h`. |
| S04 | NTFS 8.3 / LastAccess | ❌ Sacar de presets | No impacta gaming. |
| S05 | SysMain / DisablePagingExecutive | 🟡 Opcional | Efecto marginal. |
| F01 | IFEO prioridad CS2 + GPU pref + FSO | ✅ Mantener (CS2 primero) | Separar FSO en su propio toggle. |
| F02 | IFEO csrss/dwm + clave Steam | ❌ Quitar | IFEO no aplica a procesos protegidos; `gpuacceleration` en HKCU\Valve\Steam no es la config real de Steam. |
| E01 | CFG/MitigationOptions off | ⚠️ Entusiasta | Bytes `0x02×16` apagan **todas** las mitigaciones, no solo CFG. FACEIT puede bloquear. |
| E02 | Spectre/Meltdown off | ⚠️ Entusiasta | Riesgo FACEIT. |
| E03 | Defender exclusion | ⚠️ Entusiasta | |
| E04 | Pausar WU 1 año | ⚠️ Entusiasta | |

---

## 4. Lo que hay que AGREGAR (lo que realmente mueve la aguja)

1. **Medición A/B integrada (PresentMon)**: frametime avg / 1% low / p99 y PC latency antes vs después de cada preset. Es lo que diferencia un producto serio de un tweaker placebo.
2. **Asesor CS2 (solo lectura)**: leer `cs2_video.txt` / convars de `userdata` y verificar Reflex On+Boost, fullscreen, `fps_max`, resolución/Hz del monitor, launch options. Hoy no existe y es lo de mayor impacto.
3. **Verificación post-apply real**: cada operación se re-lee; resultado por tweak = `Applied | Partial | Failed | NeedsReboot`.
4. **Chequeos de hardware que importan**: Hz real del monitor vs máximo soportado, XMP/EXPO (velocidad RAM real vs nominal), canales de RAM reales (SMBIOS), driver GPU, cable/puerto (DP vs HDMI).
5. **Modo "no corro mientras jugás"**: todo persistente; la app no necesita quedar abierta. Telemetría solo cuando la vista está visible.
6. **Tests + CI** (ver [PLAN.md](PLAN.md)).

## 5. Lo que hay que QUITAR / mover del repo

- Tweaks marcados ❌ en §3.
- Licencia + anti-debug actuales (B6) hasta tener backend real (ver ADR-003).
- "Descarga verificada de drivers" (B23) → reemplazar por "versión detectada + link oficial".
- Exportar NVRAM parcheado para SCEWIN: riesgo de brick. Dejar **solo lectura/consejo** en el MVP.
- Score "FACEIT LEVEL 10" (B22) → checklist honesto + métricas medidas.
- Raíz del repo: `*.py`, `*.json` de scraping (~28 MB), `thread_list.txt`, `nvram*.txt`, `test*.ps1`, `fix.ps1`, los dos `optimizacion-input-lag-definitivo*.ps1` (legacy duplicado), `*_replacement.txt`, `test_bb.html` → mover a `research/` fuera del build (o a otro repo).
- `README.md` es una copia de PC-Tuning (tercero) → reemplazar por README propio y citar la fuente.
- `.pow` de terceros (calypto, kizzimo, khorvie…): verificar licencia antes de redistribuir; si no, no incluirlos.
- `src/KruzhLatency/publish/`, `startup_diagnostic.log`, `bin/`, `obj/` → `.gitignore`.
- Copias duplicadas de `nvidiaProfileInspector.exe` y `Base Profile.nip` (raíz + Assets + publish).
