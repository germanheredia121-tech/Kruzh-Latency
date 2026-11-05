# Spec 003 — Auto-tune medido (GPU, MSI, dispositivos, CPU Sets)

Decisión de diseño: [ADR-012](../docs/decisions/ADR-012-auto-tune.md). Concreta ADR-005 y las tareas T70–T73.

## Objetivo
Que KRUZH elija, **midiendo en la PC del usuario**, en qué núcleo atender la GPU, qué dispositivos van en MSI, en qué núcleos
atender USB/red/NVMe y qué CPU Sets darle al juego. Sin elegir nada "a ojo" y sin empeorar nunca el default de Windows.

## Funciones
### F1. Banco de medición
- Carga: `lava-triangle` (GPU), ICMP/UDP al gateway (red), lectura de disco, paso guiado de mouse (USB).
- Métricas por corrida: FPS medio, 1% low, 0,1% low (PresentMon), y por driver/CPU: cantidad, media y máximo de ISR/DPC (ETW).
- Cada candidato se mide ≥ 3 veces; se descarta la primera corrida (calentamiento).
- Comparador: el candidato gana solo si supera al default por más que el ruido (máx(2 %, 2σ del default)).

### F2. Auto afinidad de GPU (AutoGpuAffinity)
- Candidatos: "como viene Windows" + cada CPU lógico (opcional: solo primeros hilos de cada núcleo físico).
- Por candidato: escribir afinidad (journal) → reiniciar el devnode de la GPU → verificar sin código de problema → medir.
- Resultado: tabla por CPU (FPS, 1%, 0,1%, DPC de nvlddmkm/dxgkrnl) y se aplica el ganador (o nada).

### F3. Auto MSI
- Dispositivos: los que declaran MSI/MSI-X (MsiPlanner). Candidatos por dispositivo: línea (INTx) / MSI (actual contra invertido).
- Prioridad (`DevicePriority`) y límite de mensajes (`MessageNumberLimit`): se probó medirlos (nov-2026) y en la PC del autor
  ninguno ganó al ruido en ningún dispositivo; además costaban reinicios. Se sacaron. "Revertir todo" sigue deshaciendo
  `AutoMsiPrio:` y `AutoMsiLimit:` por si quedó algo de esa versión.
- Controladoras que Windows no deja reiniciar en caliente (sin DN_DISABLEABLE: las xHCI de teclado y mouse): se detecta antes
  de medir y el diálogo dice cuántos reinicios de Windows va a pedir (hasta 2 por controladora).
- Dispositivos reiniciables en caliente: reinicio del devnode. No reiniciables (a veces la GPU): reinicio de Windows con retomado, también para la vuelta atrás (se reinicia en el momento, nunca "reiniciá más tarde": el dispositivo no puede quedar funcionando con el valor de prueba).
- MSI del NVMe: no se mide. Sin MSI el disco del sistema puede trabarse y colgar Windows sin volcado (visto en la PC del autor dos veces, nov-2026).
- Criterio: menor p99 de ISR+DPC del driver bajo su carga, sin empeorar FPS; si el dispositivo falla con MSI, queda en línea y "no apto".

### F4. Auto afinidad de dispositivos
- xHCI (mouse/teclado), placa de red (RSS), NVMe: candidatos "default" + los planes de InterruptAffinityPlanner + variantes.
- Se elige por p99 de ISR/DPC del driver y FPS sin pérdida.

### F5. CPU Sets por juego (CPUSetSetter)
- KRUZH Agent aplica CPU Sets a cs2.exe / VALORANT-Win64-Shipping.exe al abrirse.
- Candidatos: sin CPU Sets / sin el núcleo 0 / solo primeros hilos (SMT "blando") / CCD con V-Cache (X3D) / P-cores (Intel híbrido).
- La elección se mide con el benchmark de F1 (los juegos no se automatizan: ADR-004) o la elige el usuario.

### F6. Optimización completa (asistente tipo instalador)
- Botón "Empezar" en Inicio. Pantallas: perfil (Normal / Competitivo / Profesional / Entusiasta) → una por sección del catálogo
  con casillas (solo lo aplicable a la PC) → Laboratorio (ADR-011: nada marcado de entrada, el usuario elige a mano) →
  mediciones (F2, F3, F4) → resumen → "Optimizar".
- Solo suma: lo ya aplicado se muestra marcado y bloqueado. Revertir se hace desde Optimizaciones o Monitor.
- Lo manual que toca lo mismo que una medición sale del lote: "MSI en todos" (PciMsiModeV3) si se mide MSI; el reparto fijo
  de interrupciones (I02) si se mide afinidad, y si estaba aplicado se revierte para poder medir.
- Orden (`OnboardingPlan`): aplicar el lote con el flujo normal (punto de restauración + journal + aviso rojo de seguridad/
  anticheat) → reinicio si algún cambio lo pide y hay mediciones → MSI → afinidad de GPU → afinidad USB/NVMe.
- La cola vive en ProgramData (`OnboardingStore`): sobrevive a reinicios de Windows y a que KRUZH se reabra tras reiniciar el
  driver de video. Dentro del asistente las mediciones no preguntan; cada resultado se suma a un informe final, que ofrece
  reiniciar.

## Seguridad y estabilidad
- Punto de restauración antes de cada sesión. Journal por cada cambio. Cancelar = revertir todo lo de la sesión.
- Si un dispositivo no vuelve (problema ≠ 0) tras reiniciarlo → revert inmediato, dispositivo "no apto".
- Nunca se toca: lo protegido por NeverTouchPolicy / ADR-007.

## Fases
1. Núcleo puro: estadísticas de frames, parser de PresentMon, comparador con margen de ruido (Core, con tests). **← primero**
2. Infra: PresentMon y lava-triangle incluidos, ejecución de carga, ETW por CPU, reinicio de la GPU en caliente.
3. F2 (afinidad de GPU) completa con UI.
4. Sesión con reinicios y retomado (estado en ProgramData + tarea de un uso).
5. F3 (MSI) y F4 (afinidad de dispositivos).
6. F5 (KRUZH Agent con CPU Sets).
