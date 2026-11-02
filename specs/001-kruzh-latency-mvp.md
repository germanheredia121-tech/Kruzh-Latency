# Spec 001 — KRUZH LATENCY MVP (CS2 competitivo)

Estado: BORRADOR — revisar y aprobar antes de pasar a PLAN.md / tasks.md.

## Objetivo
App de escritorio Windows 10/11 que deja una PC lista para jugar **CS2 competitivo** con la menor latencia de sistema y la menor varianza de frametime posibles, de forma **segura, reversible al 100 % y medible**.

El usuario abre la app, ve qué está bien y qué no en su PC, elige un preset, aplica, reinicia y **ve la diferencia medida** (antes vs después).

## Usuario
Jugador de CS2 (FACEIT / Premier / LAN) en PC desktop o laptop, con conocimientos medios. No sabe qué es un IRQ; sí sabe qué es Reflex y fps_max.

## Alcance funcional (MVP)

1. **Diagnóstico de hardware correcto**: CPU (topología real: P/E-cores, CCDs, SMT), GPU discreta real, RAM (capacidad, canales, velocidad real vs nominal), monitor (Hz actual vs máximo), NIC, chasis (desktop/laptop), build de Windows.
2. **Catálogo de tweaks** solo con los marcados ✅/🟡 en [docs/AUDITORIA.md §3](../docs/AUDITORIA.md). Cada tweak declara: qué toca, por qué, fuente, riesgo, gates (aplicable / recomendado / anticheat), si requiere reinicio.
3. **Presets**: Seguro, Competitivo, Profesional, Entusiasta (opt-in con advertencia roja).
4. **Aplicar / revertir transaccional**: backup exacto de cada valor tocado, revert por tweak y "revertir todo", journal resistente a cortes de luz.
5. **Punto de restauración real** antes del primer apply (si falla, el usuario lo sabe y decide).
6. **Asesor CS2 (solo lectura)**: Reflex, modo de pantalla, fps_max, Hz, resolución, launch options. Muestra qué cambiar en el juego; no edita archivos del juego.
7. **Medición A/B** con PresentMon: captura de N segundos con CS2 abierto → frametime promedio, 1 % low, p99, latencia de PC si está disponible. Guarda "antes" y "después".

## Criterios de aceptación

### Seguridad y reversibilidad
- [ ] Cada operación (registro, servicio, BCD, powercfg, tarea programada, netsh, perfil NVIDIA) guarda el valor original **antes** de escribir, y el revert lo restaura exacto (tipo y valor) — verificado por tests con registro falso.
- [ ] "Revertir todo" deja el sistema igual que antes del primer apply (test: snapshot → apply preset → revert all → snapshot idéntico).
- [ ] El journal se escribe de forma atómica (archivo temporal + replace) y sobrevive a un kill del proceso a mitad de un apply.
- [ ] Ninguna operación sin backup. Si una operación no se puede respaldar, no existe en el catálogo.
- [ ] Si el punto de restauración falla, se muestra el error real y se pide confirmación explícita para seguir.

### Correctitud
- [ ] Estado de cada tweak = `Applied | Partial | NotApplied | NotApplicable`, calculado leyendo **todas** sus operaciones.
- [ ] Apply de un tweak en estado `Partial` completa lo faltante (no hace skip).
- [ ] Un error en un tweak no corta el lote: se reporta `Failed` con el mensaje y se sigue con el resto.
- [ ] Todo proceso externo devuelve exit code + stdout + stderr; exit code ≠ 0 = `Failed`.
- [ ] Funciona igual en Windows en español y en inglés (sin parsear texto localizado).
- [ ] Topología de CPU correcta en: Intel 12-14th/Core Ultra, Ryzen single-CCD, Ryzen dual-CCD (incl. X3D). Test con buffers grabados de cada caso.
- [ ] Cero rutas hardcodeadas a la PC del autor.

### Rendimiento de la propia app
- [ ] Ninguna llamada a registro/proceso externo en el hilo de UI.
- [ ] Con la app minimizada: 0 polling (CPU de la app < 0,1 %).
- [ ] El apply muestra progreso por tweak y no congela la ventana.

### Honestidad
- [ ] La descripción de cada tweak coincide 1:1 con las operaciones que ejecuta (test que compara la lista declarada de operaciones con lo que muestra la UI).
- [ ] No hay score inventado: el dashboard muestra checklist + métricas medidas.

### CS2
- [ ] Detecta CS2 en cualquier biblioteca de Steam (parser VDF real, múltiples bibliotecas).
- [ ] El asesor marca correctamente Reflex/fps_max/modo de pantalla leyendo la config real del usuario.
- [ ] La medición A/B produce un reporte comparativo guardado en disco.

## Non-goals (MVP)
- NO soporte de otros juegos más allá de que el usuario agregue un `.exe` a mano (Valorant, Fortnite, etc. quedan para después).
- NO escribir BIOS/NVRAM. El módulo NVRAM queda **solo lectura / consejos**.
- NO descargar ni instalar drivers. Solo mostrar versión detectada + link oficial.
- NO sistema de licencias / anti-debug (ver ADR-003). Se decide antes de vender.
- NO tweaks marcados ❌ en la auditoría.
- NO tocar archivos de configuración de CS2 (solo lectura).
- NO "optimizaciones" que no se puedan revertir.
- NO refactors visuales de la UI más allá de lo necesario para MVVM.

## Riesgos conocidos
- Anticheat: FACEIT AC es kernel-level. Todo lo que toque mitigaciones (E01/E02) queda fuera de presets y con advertencia.
- Laptops: C-states / P0 / ASPM empeoran térmicas. Gates por chasis obligatorios.
- HKCU con elevación por otra cuenta: detectar y advertir (o escribir al hive del usuario interactivo).
