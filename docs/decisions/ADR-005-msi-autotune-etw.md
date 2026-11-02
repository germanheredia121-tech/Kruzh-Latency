# ADR-005 — Auto-tune de MSI/prioridad midiendo ISR/DPC con ETW

Fecha: 2026-11-02 · Estado: PROPUESTA

## Contexto
El dueño quiere que la app elija sola, por dispositivo, modo MSI, límite de mensajes y prioridad, probando cada combinación y quedándose con la de menor latencia. Hacerlo por categoría ("GPU = high") es adivinar.

## Decisión
- **Métrica**: latencia de ISR y DPC por driver (`.sys`) obtenida de trazas ETW del kernel (proveedor NT Kernel Logger: `Interrupt` + `DPC`), igual que LatencyMon/xperf. Se usa p99 como criterio, con promedio y máximo como desempate.
- **Carga**: ventana de medición fija (ej. 20-30 s) con carga reproducible generada por la app (GPU: render simple; NIC: tráfico UDP local; USB: se pide mover el mouse; storage: lectura secuencial). Sin tocar ningún juego.
- **Capacidades**: modo MSI y máximo de mensajes se leen del hardware (SetupAPI / `DEVPKEY_PciDevice_InterruptSupport`, `InterruptMessageMaximum`), nunca se supera.
- **Aplicar una combinación**: escribir `MessageSignaledInterruptProperties` y `Affinity Policy\DevicePriority` → reiniciar el dispositivo (CfgMgr `CM_Disable_DevNode`/`CM_Enable_DevNode`) → verificar que arrancó sin código de error → medir. Si no arranca: restaurar y reiniciar el dispositivo.
- **No reiniciable en caliente** (disco de booteo, a veces GPU): ciclo de reinicios guiado con tarea programada que retoma la prueba después del boot.
- Librería ETW: `Microsoft.Diagnostics.Tracing.TraceEvent` (NuGet, MIT). **Requiere aprobación** (CLAUDE.md: no agregar paquetes sin preguntar).

## Alternativas descartadas
- PresentMon: mide frames del juego, no latencia por dispositivo; además implicaría medir con el juego abierto.
- Tabla fija por categoría/vendor: rápida pero no "exprime la build"; queda como **valor inicial** de la búsqueda para acortar pruebas.

## Consecuencias
- + Resultado específico para cada PC, con datos que el usuario puede ver.
- − Proceso largo (minutos, posibles reinicios) y riesgoso → punto de restauración obligatorio, rollback automático, se ejecuta solo a pedido.
- − Necesita spike técnico antes de comprometer el diseño (T70).
