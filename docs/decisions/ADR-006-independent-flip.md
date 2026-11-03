# ADR-006 — La presentación se optimiza hacia Independent Flip (FSO y MPO activos)

Fecha: 2026-11-02 · Estado: ACEPTADA (decisión del dueño del producto)

## Contexto
El catálogo v3 se contradecía: G05 decía "forzar Independent Flip" pero a la vez desactivaba FSO globalmente
(`GameDVR_FSEBehavior=2`, `DXGIHonorFSEWindowsCompatible=1`), F01 desactivaba FSO por juego
(`~ DisableDXMaximizedWindowedMode`) y G06 recomendaba desactivar MPO. En Windows 11, FSO + flip model es lo que
permite que el juego presente en **Independent Flip** (sin composición de DWM, latencia equivalente a fullscreen
exclusivo) y MPO es lo que habilita el Independent Flip por hardware (overlays).

## Decisión
- El objetivo de presentación es Independent Flip.
- Ningún tweak desactiva FSO (ni global ni por juego).
- G05 = "Optimizaciones para juegos en ventana" (`SwapEffectUpgradeEnable=1`, agregado sin pisar el resto de
  `DirectXUserGlobalSettings`) + FSO en sus valores por defecto. Solo Windows 11.
- G06 (desactivar MPO) queda como herramienta de solución de problemas (flickering / pantallas negras):
  NO RECOMENDADO y fuera de todos los perfiles, incluido Entusiasta.

## Consecuencias
- Test de catálogo (T30/T32): ningún perfil incluye operaciones que desactiven FSO o MPO.
- Quien ya tenía aplicado el G05 viejo: al aplicar el nuevo, FSO vuelve a quedar activo (el journal conserva el
  valor original del primer apply para revertir).
- Si el usuario había desactivado FSO por juego con F01 viejo, se limpia revirtiendo F01 desde el journal.
