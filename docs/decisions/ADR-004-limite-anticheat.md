# ADR-004 — Límite anticheat: qué puede y qué no puede tocar la app

Fecha: 2026-11-02 · Estado: ACEPTADA (decisión del dueño del producto)

## Contexto
El producto se usa con FACEIT AC y Riot Vanguard (kernel-level). Cualquier interacción con el proceso o los archivos del juego es un riesgo de ban o de bloqueo.

## Decisión
**Prohibido**: inyección, hooks, lectura/escritura de memoria de los juegos, leer o modificar archivos de configuración de CS2/VALORANT, overlays, launchers que envuelvan al juego.

**Permitido por juego**: perfil NVIDIA `.nip`, IFEO `PerfOptions` (CpuPriorityClass/IoPriority), CPU sets / afinidad persistente vía sistema, `UserGpuPreferences`, `AppCompatFlags\Layers` (FSO).

**Perfil Profesional**: además no toca nada que los anticheats requieren (TPM, Secure Boot, VBS/HVCI cuando el AC lo exige, mitigaciones de CPU, CFG, servicios vgc/vgk/FACEIT, drivers firmados).
**Perfil Entusiasta**: sin límite de anticheat, con advertencia roja y confirmación.

## Consecuencias
- Test sobre el catálogo: ninguna operación apunta a carpetas de juego ni a la lista protegida en Profesional.
- Se descarta el asesor de config in-game.
