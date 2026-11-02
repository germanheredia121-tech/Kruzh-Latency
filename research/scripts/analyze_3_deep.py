import json
import sys
sys.stdout.reconfigure(encoding='utf-8')

with open('bb_filtered.json', encoding='utf-8') as f:
    data = json.load(f)

# Buscar threads con keywords muy especificas de fixes que NO tenemos
NEW_KEYWORDS = [
    # Audio
    ('realtek audio', 'Realtek Audio causing input lag'),
    ('usb headset', 'USB Headset interferencia con input'),
    ('audio driver', 'Driver de audio causando lag'),
    # BIOS
    ('bios version', 'Version de BIOS afecta desync'),
    ('smi', 'SMI (System Management Interrupt) spikes'),
    ('spi flash', 'SPI Flash / SMM interferencia'),
    ('xhci', 'xHCI USB controller modo de operacion'),
    ('amd kvm', 'AMD KVM Mouse Protocol'),
    # Red
    ('fiber optic', 'Cable DisplayPort fibra optica reduce lag'),
    ('external ssd', 'SSD externo afecta input lag'),
    ('routing', 'Herramienta de ruteo para jitter'),
    ('business internet', 'Internet empresarial reduce jitter'),
    ('usb ethernet', 'Adaptador USB Ethernet TP-Link mejor que integrada'),
    ('realtek usb', 'Driver Realtek USB LAN vs adaptador externo'),
    # CPU
    ('x3d', 'AMD X3D tip de cache'),
    ('zen 5', 'Zen 5 con iGPU fix'),
    ('am5', 'Fix especifico AM5'),
    ('dual-ccd', 'Dual CCD Ryzen tuning'),
    # GPU/Display
    ('dsc', 'DSC (Display Stream Compression) causa lag'),
    ('scanline sync', 'RTSS Scanline Sync'),
    ('large vt', 'Large Vertical Total trick'),
    ('vrr', 'VRR/G-Sync tuning correcto'),
    ('v-sync', 'VSync configuracion correcta'),
    ('fullscreen', 'True Fullscreen 2026'),
    # Input
    ('mouse drifting', 'Mouse drift fix'),
    ('mouse plotter', 'Herramienta Mouse Plotter para diagnostico'),
    ('wireless mouse', 'Mouse wireless latency test'),
    # Windows
    ('dont disable game bar', 'Game Bar NO debe desactivarse (contradiccion)'),
    ('game bar', 'Game Bar realmente ayuda con input lag'),
    ('alt-tab', 'Fix Alt-Tab lento en OLED high refresh'),
]

print("=" * 80)
print("RESUMEN DE HALLAZGOS POTENCIALMENTE NUEVOS DEL FORO BLUR BUSTERS")
print("=" * 80)

found_new = {}

for thread in data:
    title_low = thread['title'].lower()
    for (kw, label) in NEW_KEYWORDS:
        if kw.lower() in title_low:
            if label not in found_new:
                found_new[label] = []
            found_new[label].append({
                'title': thread['title'],
                'url': thread['url'],
                'snippet': thread['posts'][0]['snippet'][:300] if thread['posts'] else ''
            })

for label, threads in found_new.items():
    print(f"\n### [{label}]")
    for t in threads[:2]:
        print(f"  Thread: {t['title']}")
        print(f"  URL: {t['url']}")
        print(f"  Snippet: {t['snippet'][:200]}...")

# Ahora buscar en el CONTENIDO de los posts
print("\n" + "=" * 80)
print("MENCIONES ESPECIFICAS EN CONTENIDO DE POSTS (fixes tecnicos nuevos)")
print("=" * 80)

CONTENT_SEARCHES = [
    ('SMI', 'SMI - System Management Interrupt'),
    ('SMM', 'SMM - System Management Mode'),
    ('xHCI', 'xHCI USB controller'),
    ('BIOS version', 'BIOS version afecta desync'),
    ('Realtek audio', 'Realtek Audio DPC spikes'),
    ('USB headset', 'USB headset causa lag'),
    ('fiber optic', 'Fiber optic DP cable'),
    ('DSC', 'Display Stream Compression'),
    ('Scanline sync', 'RTSS Scanline Sync'),
    ('Large VT', 'Large Vertical Total'),
    ('TP-LINK UE300', 'Adaptador USB Ethernet TP-Link'),
    ('KVM', 'AMD KVM Mouse Protocol'),
    ('SPI flash', 'SPI Flash read latency'),
    ('X3D', 'AMD X3D cache tip'),
    ('Game Bar', 'Game Bar NO desactivar'),
    ('VRR', 'VRR configuracion'),
    ('external SSD', 'External SSD fix'),
    ('iGPU', 'iGPU Zen5 fix'),
    ('Dual-CCD', 'Dual-CCD Ryzen'),
]

content_results = {}
for thread in data:
    for post in thread['posts']:
        for (kw, label) in CONTENT_SEARCHES:
            if kw.lower() in post['snippet'].lower():
                if label not in content_results:
                    content_results[label] = []
                if len(content_results[label]) < 3:
                    content_results[label].append({
                        'thread': thread['title'],
                        'url': thread['url'],
                        'author': post['author'],
                        'is_placebo': post['is_placebo'],
                        'snippet': post['snippet'][:300]
                    })

for label, posts in content_results.items():
    print(f"\n### [{label}]")
    for p in posts[:2]:
        placebo_tag = " [PLACEBO SEGUN COMUNIDAD]" if p['is_placebo'] else ""
        print(f"  Autor: {p['author']}{placebo_tag}")
        print(f"  Thread: {p['thread']}")
        print(f"  URL: {p['url']}")
        print(f"  Snippet: {p['snippet'][:200]}...")
