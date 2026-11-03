# ADR-007 — Dispositivos: lista de nunca-tocar por topología + Rescate

Fecha: 2026-11-03 · Estado: ACEPTADA

## Contexto
En `chat.md` el autor deshabilitó por error un **PCI-to-PCI Bridge** (la GPU quedó en Code 45 / Microsoft Basic Display Driver), frenó `NVDisplay.ContainerLocalSystem` (desapareció el panel de NVIDIA) y borró cachés de pantalla sin backup. Un programa que deshabilita dispositivos automáticamente puede repetir eso en miles de PCs.

## Decisión
1. **Nunca tocar** (se evalúa por topología y clase, no por nombre):
   - Clases/IDs: PCI bridges y root ports, host bridges, ACPI/sistema, procesador, TPM, AMD PSP / Intel ME, IOMMU.
   - La **GPU activa** (la que tiene el escritorio) — sí sus funciones hermanas (audio, USB-C) según reglas.
   - La controladora de almacenamiento del **disco de arranque**.
   - Toda controladora USB / hub en la **cadena de padres** de un mouse o teclado presente.
   - La placa de red **con enlace activo**.
   - La controladora de audio de la **salida predeterminada**.
   - Servicios de display de NVIDIA/AMD (nunca se detienen).
2. **Rescate**: botón que reactiva todos los dispositivos que KRUZH deshabilitó (según journal) y reinicia; no toca lo que el usuario deshabilitó a mano.
3. Las reglas viven en Core, se prueban con fixtures del árbol real del autor y la lista de nunca-tocar tiene su propio test.

## Consecuencias
- Requiere inventario con relaciones padre/hijo (F10) antes de cualquier regla nueva.
- Algunas optimizaciones "agresivas" del chat quedan fuera por definición (no se puede deshabilitar un bridge para "aislar" nada).
