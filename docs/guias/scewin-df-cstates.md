# Cambiar opciones ocultas de la BIOS con SCEWIN (ejemplo: DF C-States)

KRUZH no toca la BIOS (spec 001, F9: solo consejo). Esta guía es para hacerlo vos, con cuidado.

## Qué es y cuándo sirve
- **DF C-States** (Data Fabric C-States) deja dormir al Infinity Fabric, el bus que une los núcleos con la RAM y el PCIe, cuando no hay tráfico. Es una opción real de AMD (menú AMD CBS), pero muchas placas la ocultan.
- **Evidencia:** no hay mediciones serias publicadas. En foros hay quienes dicen que apagarla da una latencia más pareja y quienes dicen que no cambia nada. Probala vos y medí antes y después: 3 corridas del mismo benchmark en cada caso.
- **SCEWIN** (AMI Setup Control Environment) exporta la configuración guardada de la BIOS (NVRAM) a un archivo de texto, lo editás y lo volvés a importar. Así se cambian opciones que el menú no muestra. Solo funciona en BIOS **AMI Aptio V**, como la de la mayoría de placas AM4 y AM5, incluida la MSI B550 Tomahawk.

## Antes de empezar (no te saltees esto)
1. **Plan de rescate:** sabé cómo hacer *Clear CMOS* en tu placa (botón, jumper o sacar la pila). Si una opción deja la PC sin arrancar, eso vuelve todo a fábrica. Las placas con *Flash BIOS Button* tienen además recuperación de BIOS.
2. **Guardá tu configuración** de BIOS (en MSI: *OC Profiles → Save*), porque un Clear CMOS la borra.
3. **Conseguí SCEWIN:** son `SCEWIN_64.exe` + `amifldrv64.sys` + `amigendrv64.sys`, todos en la misma carpeta. AMI no lo publica: viene en paquetes de herramientas de fabricantes o en foros como Win-Raid. Analizalo con el antivirus antes de usarlo.
4. **Desactivá temporalmente lo que bloquea su driver.** `amifldrv64.sys` está en la lista de drivers vulnerables de Microsoft, y además el anticheat lo bloquea:
   - Seguridad de Windows → Seguridad del dispositivo → Aislamiento del núcleo: apagá **Integridad de memoria** y **Lista de bloqueo de controladores vulnerables**.
   - **Vanguard**: en una consola como administrador, `sc config vgk start= disabled`. FACEIT AC: cerralo.
   - Reiniciá.

## Paso a paso
Abrí una consola **como administrador** dentro de la carpeta de SCEWIN.

1. **Exportar:**
   ```
   SCEWIN_64.exe /o /s nvram.txt
   ```
   Hacé una copia sin tocar: `nvram_original.txt`. Si da error de lectura, probá agregar `/h hii.db`.
2. **Buscar la opción:** abrí `nvram.txt` en el Bloc de notas y buscá `DF Cstates`. El bloque se ve así; el **asterisco** marca el valor actual:
   ```
   Setup Question = DF Cstates
   Token          = ...
   Offset         = ...
   Options        = *[0F]Auto
                     [00]Disabled
                     [01]Enabled
   ```
   Mové el asterisco a `[00]Disabled` y no cambies nada más. Si la opción aparece más de una vez, no sigas y preguntá.
3. **Importar:**
   ```
   SCEWIN_64.exe /i /s nvram.txt
   ```
   Leé la salida: tiene que terminar sin errores.
4. **Reiniciá** (reinicio completo, no "apagar y prender" con inicio rápido).
5. **Verificá:** exportá de nuevo (`/o /s nvram_check.txt`) y confirmá que el asterisco quedó en `Disabled`. El menú de la BIOS no lo va a mostrar porque la opción está oculta.
6. **Volvé a activar la seguridad:** Integridad de memoria, la lista de bloqueo, y Vanguard con `sc config vgk start= system`. Reiniciá.

## Revertir
- Importá `nvram_original.txt` con el mismo comando del paso 3, o hacé Clear CMOS.
- **Ojo:** actualizar la BIOS, hacer Clear CMOS o cargar valores por defecto vuelve la opción a `Auto`. Hay que repetir la guía.

## Cómo medir si sirvió
- Mismo benchmark de CS2, 3 corridas antes y 3 después, con los mismos programas abiertos.
- Si no ves diferencia fuera del margen entre corridas, volvé a `Auto`: es una opción de energía, y dejarla apagada solo sube el consumo en reposo.
