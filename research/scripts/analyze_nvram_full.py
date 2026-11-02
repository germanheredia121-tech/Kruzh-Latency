import re
import sys
sys.stdout.reconfigure(encoding='utf-8')

with open('nvram.txt', 'r', encoding='utf-8', errors='ignore') as f:
    raw = f.read()

blocks = re.split(r'\n\n(?=Setup Question\t=)', raw)

settings = []
for block in blocks:
    q = re.search(r'Setup Question\s*=\s*(.+)', block)
    h = re.search(r'Help String\s*=\s*(.+)', block)
    tok = re.search(r'Token\s*=(-?\w+)', block)
    
    # Find current value - line with *
    current_opts = re.findall(r'[ \t]*\*\[(\w+)\](.+?)(?:\s*//|$)', block, re.MULTILINE)
    all_opts = re.findall(r'[ \t]*\*?\[(\w+)\](.+?)(?:\s*//|$)', block, re.MULTILINE)
    
    if q:
        name = q.group(1).strip()
        help_str = h.group(1).strip() if h else ''
        current_val = current_opts[0][1].strip() if current_opts else 'N/A'
        all_options = [o[1].strip() for o in all_opts]
        settings.append({'name': name, 'help': help_str, 'current': current_val, 'options': list(dict.fromkeys(all_options))})

# Now do analysis against known impact list
CHECKS = [
    {
        'name': 'AMD KVM Mouse Protocol',
        'impact': 'CRITICO',
        'recommend': 'Absolute',
        'avoid': ['Auto', 'Simple'],
        'reason': 'Simple/Auto introduce interpolacion de posicion del mouse. Absolute = coordenadas directas 1:1.'
    },
    {
        'name': 'IOMMU',
        'impact': 'ALTO',
        'recommend': 'Disabled',
        'avoid': ['Auto', 'Enabled'],
        'reason': 'IOMMU agrega overhead de traduccion de direcciones en I/O (USB, red). Desactivar = menos latencia de I/O.'
    },
    {
        'name': 'SVM Mode',
        'impact': 'MEDIO',
        'recommend': 'Disabled',
        'avoid': ['Enabled'],
        'reason': 'Virtualizacion AMD agrega un hypervisor layer que puede generar SMI extras. Desactivar si no usas VMs.'
    },
    {
        'name': 'Global C-state Control',
        'impact': 'CRITICO',
        'recommend': 'Disabled',
        'avoid': ['Auto', 'Enabled'],
        'reason': 'Permite que los cores entren en C6/C7 (deep sleep). Wakeup = latencia adicional de 100-300us.'
    },
    {
        'name': 'S3 Resume/Modern Standby Support',
        'impact': 'BAJO',
        'recommend': 'Disabled',
        'avoid': ['Modern Standby Enable', 'S3 Enable'],
        'reason': 'Modern Standby (S3) puede generar interrupciones SMI periodicas para verificar estado de bateria/AC.'
    },
    {
        'name': 'Int. Clk Differential Spread',
        'impact': 'ALTO',
        'recommend': 'Disabled',
        'avoid': ['Auto', 'Enabled'],
        'reason': 'Spread Spectrum introduce modulacion de frecuencia del reloj del chipset, generando jitter en PCIe/USB.'
    },
    {
        'name': 'SB Clock Spread Spectrum',
        'impact': 'ALTO',
        'recommend': 'Disabled',
        'avoid': ['Auto', 'Enabled'],
        'reason': 'Spread del reloj del South Bridge = jitter adicional en el bus PCIe y SATA.'
    },
    {
        'name': 'PSPP Policy',
        'impact': 'ALTO',
        'recommend': 'Disabled',
        'avoid': ['Balanced-High', 'Balanced-Low', 'Power Saving', 'Auto'],
        'reason': 'PCIe Speed Power Policy permite que el link baje de velocidad en idle, causando re-training y latencia al volver.'
    },
    {
        'name': 'AB Clock Gating',
        'impact': 'MEDIO',
        'recommend': 'Disabled',
        'avoid': ['Auto', 'Enabled'],
        'reason': 'El clock gating del AMD Bus (AB) puede generar micro-latencias al re-activar el clock del bus.'
    },
    {
        'name': 'Chipset Power Saving Features',
        'impact': 'ALTO',
        'recommend': 'Disabled',
        'avoid': ['Enabled', 'Auto'],
        'reason': 'Desactivar el ahorro de energia del chipset mantiene todos los buses PCIe/USB en estado activo constante, eliminando la latencia de wakeup.'
    },
    {
        'name': 'EHCI D3 Support',
        'impact': 'MEDIO',
        'recommend': 'Disabled',
        'avoid': ['Auto', 'Enabled'],
        'reason': 'D3 power state permite que el controlador USB EHCI entre en deep power down. Implica latencia de wakeup en transferencias USB.'
    },
    {
        'name': 'XHCI D3 Support',
        'impact': 'ALTO',
        'recommend': 'Disabled',
        'avoid': ['Auto', 'Enabled'],
        'reason': 'D3 power state del xHCI (USB 3.0 controller) = latencia de wakeup. Critico para mouse de alto polling rate.'
    },
    {
        'name': 'SB C1E Support',
        'impact': 'MEDIO',
        'recommend': 'Disabled',
        'avoid': ['Auto', 'Enabled'],
        'reason': 'C1E en South Bridge coordina paradas de reloj del CPU con el chipset. Puede desincronizar el timer interno del sistema.'
    },
    {
        'name': 'Aggressive Link PM Capability',
        'impact': 'CRITICO',
        'recommend': 'Disabled',
        'avoid': ['Enabled'],
        'reason': 'Permite que el HBA de SATA solicite Partial/Slumber states en el disco. Esto puede generar DPC spikes cuando el disco vuelve de sleep.'
    },
    {
        'name': 'Fusion Message C State',
        'impact': 'MEDIO',
        'recommend': 'Disabled',
        'avoid': ['Enabled'],
        'reason': 'El FCH C-state Coordination Logic genera mensajes de sincronizacion adicionales entre CPU y chipset.'
    },
    {
        'name': 'EHCI Traffic Handling',
        'impact': 'MEDIO',
        'recommend': 'Disabled',
        'avoid': ['Enabled'],
        'reason': 'Genera interrupciones para sacar al CPU del C-state cuando hay trafico USB EHCI pendiente. Innecesario con xHCI.'
    },
    {
        'name': 'USB Phy Power Down',
        'impact': 'BAJO',
        'recommend': 'Disabled',
        'avoid': ['Auto', 'Enabled'],
        'reason': 'En S4/S5, apaga el PHY del USB. No afecta en uso normal pero puede generar latencia al volver de suspend.'
    },
    {
        'name': 'USB MSI Option',
        'impact': 'MEDIO',
        'recommend': 'Enabled',
        'avoid': ['Disabled', 'Auto'],
        'reason': 'MSI (Message Signaled Interrupts) para USB reduce la latencia de interrupcion vs PIC legacy IRQs.'
    },
    {
        'name': 'MsiDis in HPET',
        'impact': 'MEDIO',
        'recommend': 'Enabled',
        'avoid': ['Disabled', 'Auto'],
        'reason': 'Habilita MSI en el HPET timer. Mejora precision del temporizador de alta resolucion con menos latencia.'
    },
    {
        'name': 'Network Stack Driver Support',
        'impact': 'BAJO',
        'recommend': 'Disabled',
        'avoid': ['Enabled'],
        'reason': 'El stack PXE/network de UEFI consume recursos al boot. Desactivar si no arrangas por red.'
    },
    {
        'name': 'TimerTick Tracking',
        'impact': 'BAJO',
        'recommend': 'Disabled',
        'avoid': ['Enabled'],
        'reason': 'Enviar mensajes periodicos al CPU sobre el timer interval agrega overhead de ISR.'
    },
    {
        'name': 'Clock Interrupt Tag',
        'impact': 'BAJO',
        'recommend': 'Disabled',
        'avoid': ['Enabled'],
        'reason': 'Marcar las interrupciones de timer periodicas consume overhead de ISR innecesario.'
    },
    {
        'name': 'Fusion Message C Multi-Core',
        'impact': 'BAJO',
        'recommend': 'Disabled',
        'avoid': ['Enabled'],
        'reason': 'Rastrear el C-state por core genera mensajes adicionales al chipset en idle.'
    },
]

print("=" * 70)
print("ANALISIS COMPLETO NVRAM - OPTIMIZACION INPUT LAG")
print(f"Archivo: nvram.txt | Total settings encontrados: {len(settings)}")
print("=" * 70)

found_names = {s['name']: s for s in settings}

results = []
for check in CHECKS:
    for sname, sdata in found_names.items():
        if check['name'].lower() in sname.lower() or sname.lower() in check['name'].lower():
            curr = sdata['current']
            needs_change = any(a.lower() in curr.lower() for a in check['avoid'])
            results.append({
                'setting': sname,
                'current': curr,
                'recommend': check['recommend'],
                'impact': check['impact'],
                'reason': check['reason'],
                'needs_change': needs_change,
                'options': sdata['options'],
            })
            break

# Sort by impact and needs_change
priority = {'CRITICO': 0, 'ALTO': 1, 'MEDIO': 2, 'BAJO': 3}
results.sort(key=lambda x: (not x['needs_change'], priority.get(x['impact'], 9)))

print(f"\nTOTAL DE CHECKS APLICABLES: {len(results)}")
print(f"REQUIEREN CAMBIO: {sum(1 for r in results if r['needs_change'])}")
print(f"YA BIEN: {sum(1 for r in results if not r['needs_change'])}")

print("\n=== REQUIEREN CAMBIO ===")
for r in results:
    if r['needs_change']:
        print(f"\n[{r['impact']}] {r['setting']}")
        print(f"  Actual     : {r['current']}")
        print(f"  Recomendado: {r['recommend']}")
        print(f"  Opciones   : {r['options']}")
        print(f"  Por que    : {r['reason']}")

print("\n=== YA BIEN CONFIGURADOS ===")
for r in results:
    if not r['needs_change']:
        print(f"  [OK][{r['impact']}] {r['setting']}: {r['current']}")
