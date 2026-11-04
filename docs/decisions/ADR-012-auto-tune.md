# ADR-012 — Auto-tune medido: afinidad de GPU, MSI, afinidad de dispositivos y CPU Sets

Fecha: 2026-11-04 · Estado: ACEPTADA (decisiones del autor) · Concreta ADR-005

## Contexto
El autor pidió integrar AutoGpuAffinity (valleyofdoom, **GPL-3.0**) y CPUSetSetter (SimonvBez, **MIT**), más un auto-MSI y una
auto-afinidad de teclado, mouse y demás dispositivos. En su PC, la afinidad fija elegida "a ojo" (I02) **bajó** el 1% low
(`docs/AUDITORIA.md` §6): la única forma correcta es medir cada opción en la PC del usuario y quedarse con lo que gana.

## Decisión
1. **Nada de código GPL.** De AutoGpuAffinity se reimplementa el *método* (afinidad a un núcleo → reiniciar el driver → medir → siguiente).
   CPUSetSetter (MIT) se puede adaptar con atribución en `THIRD-PARTY-NOTICES`.
2. **Carga sintética propia** (decisión del autor), 100% automática:
   - GPU: `lava-triangle` de liblava 0.7.3 (MIT), sin límite de FPS.
   - Red: ráfaga de ICMP/UDP contra el gateway (genera interrupciones de RX/TX reales en la placa).
   - Disco: lectura secuencial y aleatoria de un archivo temporal propio.
   - USB: no hay forma de generar interrupciones USB sin el usuario → paso guiado ("mové el mouse en círculos 20 s").
3. **Medición:** PresentMon de consola (Intel, **MIT**, incluido en KRUZH) para FPS, 1% y 0,1% low; ETW del kernel
   (`DpcLatencySession`, TraceEvent ya aprobado) para ISR/DPC por driver y por CPU.
4. **"Como viene Windows" es siempre un candidato.** Se aplica una configuración solo si gana al default por encima del ruido
   medido entre corridas (≥ 3 corridas por candidato; margen = máx(2 %, 2 desvíos estándar del default)). Si nada gana, no se toca nada.
5. **Reinicios guiados con retomado** (decisión del autor): el estado de la sesión se guarda en ProgramData protegido; KRUZH reinicia
   y, al iniciar sesión, una tarea programada de un solo uso retoma la prueba. Si un dispositivo no arranca (código de problema ≠ 0)
   se revierte su cambio desde el journal y se marca como "no apto".
6. **CPU Sets** (decisión del autor): el residente opcional `KruzhLatency.TimerAgent` pasa a ser **KRUZH Agent** (timer 0,5 ms + CPU Sets
   por juego). Aplica `SetProcessDefaultCpuSets` al detectar el proceso del juego por nombre, con el permiso mínimo
   `PROCESS_SET_LIMITED_INFORMATION` (sin leer ni escribir memoria: ADR-004). Las máscaras también se eligen midiendo.
7. Todo cambio de sistema pasa por el journal (Capture → Apply → Read) como el resto de KRUZH; cancelar en cualquier punto deja
   el sistema como estaba.

## Consecuencias
- + Resultados específicos de cada PC, con números que el usuario ve, y sin repetir el error de I02.
- − Sesiones largas (afinidad de GPU: ~12 núcleos × 3 corridas × 60 s; MSI: un reinicio por dispositivo no reiniciable en caliente).
- − Se distribuyen dos binarios de terceros (PresentMon, lava-triangle) con sus licencias.
- − La pantalla parpadea al reiniciar el driver de la GPU entre corridas (igual que AutoGpuAffinity).
