import re

with open('optimizacion-input-lag-definitivo.ps1', 'r', encoding='utf-8-sig') as f:
    content = f.read()

# Extract the $checks array definition
pattern = r'(\$checks\s*=\s*@\()([\s\S]*?)(\s*\))'
m = re.search(pattern, content)
if not m:
    print("Could not find $checks array!")
    import sys; sys.exit(1)

existing_checks = m.group(2)

new_checks = """
        @{ Q="PM L1 SS"; Rec="Disabled"; Bad=@("L1.1","L1.2","L1.1_L1.2"); Impact="CRITICO"; Reason="PCIe L1 Substates son estados de deep sleep para los buses. Desactivar = latencia minima." },
        @{ Q="Clock Power Management"; Rec="Disabled"; Bad=@("Enabled"); Impact="ALTO"; Reason="Apagar el CLKREQ# mantiene el reloj del bus PCIe siempre activo, evitando stutter de wakeup." },
        @{ Q="Legacy USB Support"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="ALTO"; Reason="El soporte Legacy emula PS/2 mediante SMM (System Management Mode). Causa stutters y CPU polling extra." },
        @{ Q="XHCI Hand-off"; Rec="Enabled"; Bad=@("Disabled"); Impact="MEDIO"; Reason="Permite al OS tomar control absoluto del USB 3.0 controller instantaneamente, sin pelear con el BIOS." },
        @{ Q="Power Supply Idle Control"; Rec="Typical Current Idle"; Bad=@("Low Current Idle","Auto"); Impact="ALTO"; Reason="Fix clasico de AMD Ryzen. Evita que la fuente/VRM caigan a estados de energia inestables." },
        @{ Q="Fast Boot"; Rec="Disabled"; Bad=@("Enabled","Ultra Fast"); Impact="BAJO"; Reason="Bypassea la inicializacion completa de hardware. Puede causar ruteos de IRQ suboptimos o DPC spikes." },
        @{ Q="CPPC Preferred Cores"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="MEDIO"; Reason="Evita que Windows mueva los hilos constantemente entre los 'mejores' cores, reduciendo latencia de cache." },
        @{ Q="SMT Control"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="EXTREMO"; Reason="Si tenes CPU de sobra, apagar Hyperthreading/SMT dedica el 100% de la cache L3 al hilo fisico y baja latencia DPC." },
        @{ Q="HD Audio Controller"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="ALTO"; Reason="El audio integrado Realtek/ALC causa masivos DPC storms. Desactivar en BIOS si usas DAC USB." },
        @{ Q="Security Device Support"; Rec="Disabled"; Bad=@("Enabled"); Impact="ALTO"; Reason="Desactiva el fTPM de AMD que es conocido por causar stutters aleatorios. Ojo: Win11 (Valorant) puede requerirlo." },
        @{ Q="CPPC"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="MEDIO"; Reason="Deshabilitar CPPC quita la administracion de energia del OS, forzando frecuencias estables dictadas por BIOS." },
        @{ Q="PSS Support"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="ALTO"; Reason="AMD Cool'n'Quiet. Desactiva el downclocking en idle, manteniendo los cores listos para el proximo tick." },
        @{ Q="Data Link Feature Exchange"; Rec="Disabled"; Bad=@("Enabled"); Impact="BAJO"; Reason="Protocolo PCIe extra. Desactivarlo puede reducir microscopica latencia de negociacion." }
"""

if "Legacy USB Support" not in existing_checks:
    # Append to existing
    updated_checks = existing_checks.rstrip() + ",\n" + new_checks.strip('\n')
    content = content[:m.start(2)] + updated_checks + content[m.end(2):]
    
    with open('optimizacion-input-lag-definitivo.ps1', 'w', encoding='utf-8-sig') as f:
        f.write(content)
    print("Added 13 new deep BIOS tweaks!")
else:
    print("Tweaks already added.")
