# ADR-008 — Temporizador (BCD) y MPO en Windows 11 reciente

Fecha: 2026-11-03 · Estado: ACEPTADA

## MPO
**Contexto.** En Windows 11 24H2, `OverlayTestMode = 5` ya no desactiva MPO (reportes de la comunidad). El problema que se buscaba resolver (apps que oscilan entre *Composed: Flip* y *Hardware Composed: Independent Flip*, redibujados/freezes tras Alt-Tab) se reporta resuelto con `HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayMinFPS = 0`, que elimina el mínimo de FPS que exige DWM para asignar swapchains a planos de overlay. Evidencia: comunitaria (PresentMon antes/después), no hay benchmark oficial; Microsoft indica que el valor puede cambiar en versiones futuras.

**Decisión.**
- G06 "Desactivar MPO" sigue fuera de perfiles y en 24H2+ se marca **No aplicable** (no hace nada).
- Nuevo tweak "MPO estable (OverlayMinFPS = 0)": borra `OverlayTestMode` si existe y escribe `OverlayMinFPS = 0`. Perfil **Competitivo**. Requiere reiniciar DWM o la PC. Coherente con ADR-006 (Independent Flip).
- Validación: el programa sugiere medir DPC antes/después.

## Temporizador
**Contexto.** En `chat.md` se sugiere `useplatformtick yes` + `disabledynamictick yes`; nuestro C03 hacía lo contrario con `useplatformtick`. La búsqueda no encontró documentación de Microsoft ni benchmarks controlados que muestren mejora en Windows 11; sí hay reportes de que esas banderas causan desincronización o timer trabado en algunos sistemas, y de que Windows 11 ya usa un tick RTC por defecto adecuado.

**Decisión.**
- En **Windows 11 ningún perfil toca `useplatformtick` ni `disabledynamictick`**. Si quedaron seteados por otra herramienta, el programa lo muestra y ofrece volver al default.
- Solo se limpia `useplatformclock` (forzar HPET) si alguien lo dejó en `yes` (perfil Normal: es volver al default).
- `useplatformtick yes` y `disabledynamictick yes` quedan como **experimentales en Entusiasta**, con la recomendación de medir DPC/frametime antes y después y revertir si empeora.
- En Windows 10 se mantiene C03 (`disabledynamictick`) como opción, fuera de perfiles.

## Fuentes
- Comunidad r/Windows11 (OverlayMinFPS, 24H2) y foros BlurBusters (useplatformclock / useplatformtick en W11). Evidencia anecdótica: por eso ambas decisiones se validan con medición.
