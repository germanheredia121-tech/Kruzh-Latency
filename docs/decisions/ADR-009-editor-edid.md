# ADR-009 — Pantalla: guía de CRU (manual) + editor de EDID propio (automático) con auto-rollback

Fecha: 2026-11-03 · Estado: ACEPTADA

## Contexto
El flujo de CRU del chat (borrar CTA-861, pasar a DisplayID, crear 4:3 al Hz nativo exacto, borrar FreeSync range, base block con PTM de respaldo) mejora el modo de escalado (Display Scaling real) y los Hz exactos, pero un EDID mal armado deja al usuario sin imagen. CRU es de un tercero (ToastyX) y no se redistribuye.

## Decisión
- **Manual**: guía paso a paso generada con los datos reales del usuario (EDID leído, timings nativos, 4:3 elegida, DisplayID recomendado), link oficial a CRU, recuperación y botón **Verificar** (relee EDID y Hz).
- **Automático**: editor propio que escribe `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<modelo>\<instancia>\Device Parameters\EDID_OVERRIDE`:
  - Parser/generador en Core (checksums, Type I y Type VII, cálculo de pixel clock y Hz exactos) con tests sobre el EDID real del autor.
  - DisplayID 1.3 vs 2.0: se elige el que dé el refresco **exacto**; si ambos son exactos, 1.3 (más compatible).
  - Backup del EDID/override original en el journal.
  - **Auto-rollback**: tarea programada que, al iniciar sesión tras el reinicio, espera 60 s la confirmación "se ve bien" en KRUZH; si no llega, borra el override y reinicia. Nunca `restart64`.
- El usuario elige manual o automático en la sección Pantalla.

## Consecuencias
- El modo automático va después del análisis y la guía (fase 2).
- Tests obligatorios del generador antes de habilitar el botón.
