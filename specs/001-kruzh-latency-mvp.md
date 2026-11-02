# Spec 001 — KRUZH LATENCY MVP (CS2 + VALORANT)

Estado: v2 — actualizada con las decisiones del dueño del producto (2026-11-02). Revisar antes de arrancar Fase 1.

## Objetivo
App de escritorio Windows 10/11 que exprime **la mínima latencia de sistema posible para la build concreta del usuario** (CPU, GPU, placa, NIC, periféricos), para jugar CS2 y VALORANT en FACEIT AC y Riot Vanguard. Todo seguro, reversible y aplicado en segundo plano con progreso por tweak.

Producto pensado para la comunidad y para venta (la licencia se implementa al final, ver ADR-003).

## Usuario
Jugador competitivo de CS2 / VALORANT con conocimientos medios. Quiere que la app detecte y ajuste todo por él; no quiere tocar configs del juego (eso ya lo sabe hacer).

## Límite anticheat (ADR-004, no negociable)
- La app **nunca** se inyecta en procesos, **nunca** lee ni escribe archivos de configuración de los juegos, **nunca** toca memoria de los juegos.
- Lo único por juego: perfil NVIDIA (`.nip`), prioridad CPU/IO por IFEO (`PerfOptions`), CPU sets / afinidad persistente, preferencia de GPU, flags de compatibilidad (FSO).
- Perfil **Profesional** = 100 % compatible con FACEIT AC y Vanguard (no desactiva servicios ni features que esos anticheats requieren: TPM, Secure Boot, HVCI/VBS cuando aplique, vgc/vgk, FACEIT service, mitigaciones).
- Perfil **Entusiasta** = tweak total, sin importar anticheat, con advertencia roja explícita.

## Alcance funcional (MVP)

### F1. Diagnóstico de hardware correcto
CPU (P/E-cores, CCDs, SMT, X3D), GPU discreta real, RAM (capacidad, canales, velocidad real vs nominal), monitor (Hz actual vs máximo), NIC, chasis, build de Windows.

### F2. Catálogo de tweaks + perfiles
Perfiles: **Normal**, **Competitivo**, **Profesional** (anticheat-safe), **Entusiasta** (total).
Catálogo según `docs/AUDITORIA.md §3` con las decisiones v2: **se mantienen todos los tweaks de red**; se quitan los placebo de mouse (M02, M03, M04), GPU (G03, G07) y audio (I03).

### F3. Aplicación transaccional en segundo plano
Backup exacto por operación, estado real `Applied | Partial | NotApplied | NotApplicable`, apply en background con progreso por tweak, un error no corta el lote, revert por tweak y "revertir todo", journal atómico, punto de restauración real.

### F4. Optimizador de servicios por perfil
- Al elegir un perfil se muestra la **lista exacta de servicios** que se van a desactivar/poner en manual (nombre, para qué sirve, por qué se toca, impacto si lo necesitás).
- Un click aplica; cada servicio es reversible (tipo de inicio original + estado).
- Lista de **servicios protegidos** que ningún perfil toca (críticos de Windows + los que requieren FACEIT/Vanguard + red/audio/entrada).
- El usuario puede destildar servicios individuales antes de aplicar.

### F5. Debloat de Windows por perfil
- Apps AppX preinstaladas, tareas programadas de telemetría, features opcionales y componentes en segundo plano (widgets, Copilot, sugerencias, etc.).
- Misma UX que servicios: perfil → lista → destildar → aplicar.
- AppX: remoción **por usuario** (reversible re-registrando el paquete). Desaprovisionar para todos los usuarios solo en Entusiasta y marcado como "no reversible desde la app".

### F6. Auditor de drivers
- Detecta dispositivos con error (códigos de Device Manager: 10, 28, 43, etc.), dispositivos **sin driver** y dispositivos con driver genérico de Microsoft donde existe driver de fabricante (GPU, NIC, chipset, audio).
- Marca versiones conocidas como problemáticas (catálogo local versionado).
- No descarga ni instala: muestra el problema, el dispositivo y el link oficial.

### F7. MSI Mode + prioridad automáticos por dispositivo (auto-tune)
- Enumera dispositivos PCI(e) y lee capacidades reales: soporte MSI/MSI-X y máximo de mensajes (desde el hardware, no por categoría).
- Para cada dispositivo candidato (GPU, NIC, USB xHCI, audio, NVMe, etc.) prueba combinaciones de: **modo** (línea / MSI), **MessageNumberLimit** (1, 2, 4, … hasta el máximo soportado) y **DevicePriority** (undefined / low / normal / high).
- Mide cada combinación con trazas ETW del kernel (latencia ISR + DPC por driver, promedio / p99 / máximo) bajo una carga reproducible, y deja la combinación con **menor latencia p99 estable**.
- Seguridad: si el dispositivo no arranca tras un cambio (código de error), se revierte solo. Dispositivos que no se pueden reiniciar en caliente (disco de booteo, a veces GPU/USB del mouse) se prueban con ciclo de reinicio guiado.
- Resultado guardado y reversible (vuelve a los valores originales del registro).

### F8. NVIDIA `.nip` por perfil
Perfil global + perfiles por ejecutable de CS2 / VALORANT, con export previo del perfil actual del driver para poder revertir.

### F9. BIOS / NVRAM — solo consejo
- Pantalla vacía hasta que el usuario **importa su `nvram.txt`** (SCEWIN). Nada de listas por defecto.
- La app parsea el archivo y cruza cada setting contra una **base de datos amplia de settings AMI** (nombre de la pregunta, variantes por fabricante, valor recomendado por perfil y por tipo de CPU/plataforma, motivo).
- Resultado por setting encontrado: **Bien / Cambiar a X / Sin recomendación**. Los que no están en el archivo no se muestran.
- **Sin exportar, sin parchear, sin flashear**: el usuario cambia los valores él mismo en la BIOS o con SCEWIN.

## Criterios de aceptación

### Seguridad y reversibilidad
- [ ] Toda operación guarda el valor original antes de escribir; revert restaura tipo y valor exactos (tests con fakes).
- [ ] "Revertir todo" deja el sistema igual al estado previo (snapshot → perfil → revert all → snapshot idéntico).
- [ ] Journal atómico; sobrevive a kill del proceso a mitad de apply.
- [ ] Si el punto de restauración falla, se muestra el error real y se pide confirmación.
- [ ] Perfil Profesional: test que verifica que ninguna operación toca la lista de servicios/features protegidos por anticheat.
- [ ] Ninguna operación lee/escribe dentro de las carpetas de instalación o config de CS2/VALORANT (test sobre el catálogo).

### Correctitud
- [ ] Estado de cada tweak calculado leyendo todas sus operaciones; `Partial` se completa al aplicar.
- [ ] Un error en un tweak no corta el lote; se reporta `Failed` con el motivo.
- [ ] Procesos externos: exit code + stdout + stderr; ≠ 0 = `Failed`.
- [ ] Mismo comportamiento en Windows en español y en inglés.
- [ ] Topología correcta en Intel híbrido, Ryzen 1-CCD, Ryzen 2-CCD (X3D) — tests con buffers grabados.
- [ ] Cero rutas hardcodeadas a la PC del autor.

### Auto-tune MSI
- [ ] Nunca configura un límite de mensajes mayor al soportado por el hardware.
- [ ] Si un dispositivo queda con error tras un cambio, vuelve al valor original sin intervención.
- [ ] Reporte por dispositivo: combinaciones probadas, latencia p99 de cada una, combinación elegida.
- [ ] La medición es repetible: dos corridas seguidas eligen la misma combinación o una dentro del margen de ruido documentado.

### Drivers / Servicios / Debloat / NVRAM
- [ ] Drivers: detecta un dispositivo con Code 28 y uno con Code 10 en tests con datos simulados.
- [ ] Servicios y debloat: la lista mostrada al elegir perfil == lo que se aplica (sin sorpresas).
- [ ] NVRAM: sin archivo importado no se muestra ningún setting; con archivo, solo los encontrados; test con el `nvram.txt` real de referencia.

### Rendimiento de la propia app
- [ ] Nada de registro/procesos en el hilo de UI.
- [ ] Minimizada: 0 polling (< 0,1 % CPU). No necesita quedar abierta para que los cambios persistan.

## Non-goals (MVP)
- NO otros juegos ni apps además de CS2 y VALORANT.
- NO leer ni escribir configs de juegos, NO inyección, NO overlays.
- NO asesor de configuración in-game.
- NO PresentMon / medición de FPS in-game (la medición del MSI auto-tune es ETW de sistema, sin tocar el juego).
- NO exportar / parchear / flashear NVRAM.
- NO descargar ni instalar drivers.
- NO licencias ni anti-debug (al final, spec propia).

## Riesgos
- Auto-tune MSI requiere reiniciar dispositivos (pantalla negra al reiniciar GPU, mouse se desconecta al reiniciar el xHCI) o la PC → UX guiada y modo "probar en el próximo reinicio".
- Algunas combinaciones MSI pueden dejar un dispositivo sin funcionar → rollback automático + punto de restauración obligatorio antes del auto-tune.
- Lista de servicios protegidos por anticheat puede cambiar con updates de FACEIT/Vanguard → catálogo versionado y actualizable.
