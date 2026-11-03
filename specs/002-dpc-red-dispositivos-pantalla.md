# Spec 002 — DPC al mínimo: Red, Device Manager, USB, GPU y Pantalla

Estado: APROBADA en conversación (2026-11-03). Origen: `chat.md` local, no versionado (sesión de optimización manual del autor, RTX 2060 Super + B550 Tomahawk + RTL8125, +3000 ELO FACEIT) + decisiones del dueño del producto.
Depende de: spec 001, ADR-004 (anticheat), ADR-006 (Independent Flip). Decisiones nuevas: ADR-007, ADR-008, ADR-009.

## Objetivo
Automatizar todo lo que en `chat.md` se hizo a mano para bajar la latencia DPC/ISR, de forma **dinámica por PC**, **segura** (sin repetir los errores del chat: PCI Bridge deshabilitado → Code 45, `Stop-Service NVDisplay.Container`, borrar `GraphicsDrivers` sin backup, `restart64` con VBS) y **medible** con el monitor ETW que ya existe.

Meta del producto (del chat): jugando CS2, **ningún driver > 250–300 µs de pico** y **promedios < 8 µs**.

## Decisiones del dueño (2026-11-03)
| Tema | Decisión |
|---|---|
| MPO | Mantener MPO activo y usar `OverlayMinFPS = 0` (Win11 reciente: `OverlayTestMode=5` ya no apaga MPO en 24H2). Ver ADR-008. |
| Temporizador (`useplatformtick` / `disabledynamictick`) | Decisión técnica (ADR-008): en Windows 11 **no se tocan en perfiles**; quedan como experimentales en Entusiasta con medición antes/después. |
| DisplayID 1.3 vs 2.0 | Depende de monitor + GPU. Criterio: **el que dé el refresco exacto** (ej. 200.000 Hz, no 200.003 Hz). El programa calcula ambos y recomienda. |
| HDCP off | **Se agrega** (NVIDIA). El autor nota mejora en GPU vieja. Advertencia: rompe DRM (Netflix/Disney+ en HD). |
| PCI Bridge | Prohibido por diseño (ADR-007: lista de nunca-tocar + botón Rescate). |
| Hyper-V / VBS off | Solo **Entusiasta**, con modal de advertencia: FACEIT AC / Vanguard lo requieren. |
| Modal anticheat | **Todo** lo que pueda afectar a un anticheat muestra un modal de advertencia antes de aplicar. |
| CRU | Las dos opciones: **guía paso a paso (manual)** y **editor automático** con auto-rollback (ADR-009). El usuario elige. |
| Info de EDID | La sección Pantalla muestra todo el EDID: timings, data blocks, extensiones, rangos. |
| Borrar fantasmas | Desde **Competitivo** (son dispositivos no presentes / deshabilitados: no afectan nada). |
| Joysticks | El autor no usa joystick: los nodos XInput/HID game controller que cuelgan de un teclado se deshabilitan. `-nojoy` se omite. |
| Placa de red | Al aplicar se **reinicia en el momento** (corte de ~2 s). |
| NuGet | `System.Management` (WMI) **aprobado**. |

## Alcance funcional

### F10. Inventario real de dispositivos (base de todo)
SetupAPI/CfgMgr32: presentes y **no presentes**, árbol padre/hijo ("Dispositivos por conexión"), hardware IDs, clase, bus/device/function, código de problema, driver (proveedor/versión/fecha/INF), servicio, capacidades de interrupción.

### F11. Device Manager inteligente (pestaña existente, ampliada)
Cada dispositivo se clasifica: **Mantener · Deshabilitar (desde perfil X) · Borrar fantasma · Instalar driver · Problema físico · Nunca tocar**.
Reglas dinámicas (por topología, no por nombre):
- **Hermanos de la GPU** en el mismo bus PCIe: controladora HD Audio (función 1), xHCI USB-C y UCSI Port Policy (VirtualLink Turing) → deshabilitar. Endpoint "NVIDIA High Definition Audio", NVPCF, NVIDIA Virtual Audio → deshabilitar si existen.
- **Nodos de joystick de teclados** (Xbox 360 Controller / HID-compliant game controller hijos de un teclado) → deshabilitar.
- **Wi-Fi** si hay Ethernet conectado → deshabilitar. **Segunda placa Ethernet sin enlace** → deshabilitar.
- **Sistema**: HPET, GS Wavetable Synth, RRAS Root Enumerator (WAN Miniports), Root Print Queue, Voice Clarity, MIDI 2.0 + los existentes (KDNIC, CompositeBus, NdisVirtualBus, RDPBUS, UMBus, vdrvroot).
- **Fantasmas** (no presentes: USB, HID, Xbox, teclados duplicados, Razer Control Device, monitores, NICs viejas) → `pnputil /remove-device` desde **Competitivo**.
- **Con error**: Code 43 "Device Descriptor Request Failed" → deshabilitar + aviso físico (cable/puerto/header). Code 28 (ej. RAID Controller) → link al driver de chipset oficial.

### F12. Red por chip
Opciones avanzadas reales de la placa (`Ndi\Params`, solo keywords existentes y valores permitidos), con tabla por chip (RTL8125, RTL8111/8168, Intel I225/I226, I219, Killer):
EEE/AdvancedEEE/Green/PowerSaving/ULP/GigaLite off · Interrupt Moderation off · RSS on + 4 colas · RX 512 / TX 128 (RTL8125) · LSO v2 off · Flow Control off · checksum offload off (experimental) · Speed/Duplex fijado a la velocidad **ya negociada** · "permitir apagar" off.
Detección de filtros NDIS (cFosSpeed, MSI LAN Manager, Dragon Center) y de driver NetAdapterCx vs NDIS ("Not Support Power Saving") → recomendación con link oficial.
Aplicar = reiniciar el adaptador en el momento.

### F13. USB y periféricos
`EnhancedPowerManagementEnabled=0` + `AllowIdleIrpInD3=0` en todo USB/HID; casilla "Permitir que el equipo apague este dispositivo" off en hubs raíz, Generic Hub, HID, mouse y teclado (WMI `MSPower_DeviceEnable`); suspensión selectiva off. Consejo de puerto: indica si mouse/teclado cuelgan de la controladora del CPU o del chipset.

### F14. GPU (ampliación)
HDCP off (`RMHdcpKeyglobZero`, NVIDIA) · P0 (existe) · HAGS por generación (existe) · ASPM off (existe) · MSI/prioridad/afinidad: auto-tune (T70–72) e I02 (requiere fix B3).

### F15. Energía, temporizador, DWM, hipervisor
CPMINCORES 100 + PROCTHROTTLEMIN 100 en el plan KRUZH · MPO: `OverlayMinFPS=0` y limpiar `OverlayTestMode` · timer: ADR-008 · Hyper-V/VBS off solo Entusiasta + modal.

### F16. Sección "Pantalla" (sidebar nuevo, ícono monitor)
1. **Análisis automático (solo lectura)**: monitores reales y fantasmas; EDID completo decodificado (bloque base, Established/Standard/Detailed timings, CTA-861 con data blocks — video, audio, speaker, VSDB, HDR, FreeSync range —, DisplayID 1.3/2.0 con Type I/VII); pixel clock y **Hz exactos** de cada modo; modo actual vs máximo (QueryDisplayConfig); DSC necesario o no; si hay `EDID_OVERRIDE` (CRU) activo; resoluciones personalizadas de NVIDIA.
2. **Limpieza con backup**: monitores fantasma, `GraphicsDrivers\Configuration`/`Connectivity` exportadas antes, reinicio (nunca `restart64`).
3. **Modo manual**: guía de CRU paso a paso **generada con los datos reales** (monitor, timings nativos, 4:3 elegida, DisplayID recomendado) + panel NVIDIA (Pantalla completa, escalado en Pantalla, sin "anular") + OSD (overdrive, FreeSync off) + recuperación (Code 45 / pantalla negra) + botón **Verificar**.
4. **Modo automático**: editor de EDID propio (ADR-009) con auto-rollback.

### F17. Monitor (DPC) con diagnóstico
Objetivos del chat en pantalla; mapeo driver → causa → acción con botón "Arreglar" (ndis → Red, dxgkrnl/nvlddmkm → GPU, Wdf01000 → USB, HDAudBus → audio de GPU, storport → ASPM). Mientras mide: sin gráfico en vivo y sugerencia de minimizar (la propia UI suma llamadas de dxgkrnl).

### F18. Seguridad (transversal)
Lista de **nunca tocar** + **Rescate** (ADR-007) · modal de anticheat para todo ítem que lo afecte · punto de restauración antes de borrar fantasmas o tocar EDID · todo cambio en el journal salvo borrar fantasmas (irreversible por naturaleza, se avisa).

## Criterios de aceptación
- [ ] Ningún perfil ni acción puede deshabilitar: PCI bridges/root ports, la GPU activa, la controladora del disco de arranque, la controladora USB que tiene el mouse/teclado, la NIC con enlace, el audio que es salida predeterminada, TPM/PSP/IOMMU. Test con el árbol real del autor (fixture).
- [ ] Botón Rescate reactiva todo lo deshabilitado por KRUZH y no toca lo que el usuario deshabilitó a mano.
- [ ] Las reglas de dispositivos se prueban con fixtures del árbol del autor: detectan hermanos de la GPU, nodos XInput del Razer, segunda NIC, fantasmas, Code 43/28.
- [ ] La red solo escribe keywords existentes y valores permitidos; Speed/Duplex nunca baja de la velocidad negociada.
- [ ] El parser de EDID decodifica el EDID real del GS25F2 (fixture) y calcula 200.000 Hz exactos para el modo DisplayID 2.0 y 200.003 Hz para el Type I.
- [ ] El editor automático vuelve solo al EDID original si no se confirma a los 60 s del inicio de sesión.
- [ ] Todo ítem con impacto en anticheat abre el modal de advertencia antes de aplicar.
- [ ] Monitor: cada driver por encima del objetivo tiene una acción sugerida.

## Non-goals
- No reemplazar el driver de red ni de chipset automáticamente (solo link oficial).
- No tocar BIOS (sigue solo consejo).
- No `restart64` ni reinicio en caliente del driver de video.
- No escribir opciones de lanzamiento de Steam (`-nojoy` omitido).
