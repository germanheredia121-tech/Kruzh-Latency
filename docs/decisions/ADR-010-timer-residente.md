# ADR-010 — Resolución de timer de 0,5 ms con un modo residente opcional

Fecha: 2026-11-03 · Estado: ACEPTADA (decisión del autor sobre la tarea E12)

## Contexto
En la segunda sesión manual (`chat2.md`) el timer de Windows quedaba en 1,0 ms y se buscaba 0,5 ms mientras se juega.
La resolución del timer la pide un proceso con `NtSetTimerResolution` y vale **solo mientras ese proceso vive**; con
`GlobalTimerResolutionRequests` = 1 (Windows 11) el pedido afecta a todo el sistema y no solo a la ventana del que lo pide.
Una escritura de registro no alcanza: hace falta un proceso corriendo.

## Decisión
- KRUZH ofrece un **modo residente opcional, apagado por defecto**, que el usuario activa desde la app.
- Mientras está activo, un proceso liviano (sin ventana, sin gráficos, sin sondeos de WMI/registro en bucle) espera a que
  CS2 o VALORANT estén abiertos, pide 0,5 ms con `NtSetTimerResolution` y lo libera al cerrarse el juego.
- Se registra para iniciar con la sesión (tarea programada del usuario, revertible) solo si el usuario lo pide.
- No toca el juego ni sus archivos (ADR-004): solo detecta el proceso por nombre.
- Monitor muestra la resolución actual para verificar.

## Consecuencias
- Requiere `GlobalTimerResolutionRequests` = 1 en Windows 11 (ya existe en el catálogo).
- El residente no debe generar DPC propios: detección de procesos por eventos/espera larga, nunca polling corto.
- Se mide con Monitor (E13) antes y después; si empeora el frametime, el usuario lo apaga.
