import json
import sys
import re
from collections import defaultdict
sys.stdout.reconfigure(encoding='utf-8')

with open('blurbusters_inputlag.json', encoding='utf-8') as f:
    data = json.load(f)

# Extraer TODOS los posts como texto plano con referencia al thread
all_posts = []
for thread in data:
    for post in thread['posts']:
        all_posts.append({
            'thread_title': thread['title'],
            'thread_url': thread['url'],
            'author': post.get('author','?'),
            'content': post.get('content','')
        })

print(f"Total posts a analizar: {len(all_posts)}")

# Busqueda exhaustiva por categorias con regex
SEARCHES = {
    "BIOS/Firmware": [
        r'(bios version|firmware version|bios update|downgrade bios)',
        r'(smi|system management interrupt|smm|system management mode)',
        r'(spi flash|spi read|nvram|scewin)',
        r'(xhci|xhci mode|xhci hand-off|usb legacy|ehci)',
        r'(c[0-9] state|c-state|c1e|package c|idle state)',
        r'(global c-state|cpu idle|halt state)',
        r'(amd kvm|kvm mouse protocol)',
        r'(pbo|precision boost|curve optimizer)',
        r'(soc voltage|vcore|vddnb|lclk)',
        r'(intel speed shift|speed optimizer|hwp)',
        r'(msi click bios|asus uefi|gigabyte bios)',
        r'(power limit|pl1|pl2|tdp)',
        r'(eist|enhanced intel speedstep)',
        r'(speedstep|core performance boost)',
        r'(memory timing|trfc|trc|tcl|trcd|trp|tras)',
        r'(expo|xmp|docp|memory oc)',
        r'(flck|uclk|mclk ratio)',
        r'(fclk|infinity fabric)',
        r'(lock interrupt routing|interrupt routing)',
        r'(above 4g decoding|resizable bar|rebar)',
        r'(iommu|virtualization|svm mode)',
        r'(secure boot|tpm|ftpm)',
        r'(spread spectrum|pcie spread)',
        r'(primary display|igpu multi-monitor|initdisplay)',
        r'(fast boot|ultra fast boot)',
        r'(dram power|power down mode|gear mode)',
        r'(gear 1|gear 2|gear 4)',
    ],
    "GPU/Display": [
        r'(dsc|display stream compression)',
        r'(vrr|variable refresh|g-sync|freesync)',
        r'(scanline sync|rtss scanline)',
        r'(large vt|large vertical total)',
        r'(flip model|present model|flip queue)',
        r'(composed flip|independent flip|iflip)',
        r'(mpo|multiplane overlay)',
        r'(dxgi|directx|dx12|dx11)',
        r'(low latency mode|ultra low latency|ullm)',
        r'(nvidia reflex|amd anti-lag)',
        r'(frame generation|dlss|fsr)',
        r'(overclock monitor|custom resolution|cru)',
        r'(strobing|blur reduction|ulmb|dyac)',
        r'(overdrive|response time|gtg)',
        r'(panel type|oled|ips|tn|va)',
        r'(display latency|display lag|gls)',
        r'(hdmi 2\.1|displayport 2\.1|dp 2\.0)',
        r'(fiber optic cable|optical cable|dp cable)',
        r'(cable quality|cable shielding|emi cable)',
        r'(pixel response|ghost|inverse ghost)',
        r'(refresh rate cap|fps cap|frame limit)',
        r'(frame pacing|frame time|1% low)',
        r'(hdr|high dynamic range)',
        r'(color depth|bit depth|chroma)',
    ],
    "Network/Desync": [
        r'(bufferbloat|cake|fq_codel|sqm)',
        r'(qos router|qos policy|dscp router)',
        r'(gaming router|asus router|netgear router)',
        r'(wired vs wireless|ethernet vs wifi)',
        r'(jitter|packet loss|ping spike)',
        r'(anti-lag server|server tickrate|tick rate)',
        r'(faceit server|valve server|dedicated server)',
        r'(interp ratio|cl_interp|lerp)',
        r'(rate setting|cl_cmdrate|cl_updaterate)',
        r'(vpn gaming|exit lag|mudfish)',
        r'(isp throttling|traffic shaping|qos isp)',
        r'(modem firmware|modem reboot)',
        r'(dns latency|dns server|1\.1\.1\.1|8\.8\.8\.8)',
        r'(tcp vs udp|protocol gaming)',
        r'(jumbo frame|mtu|jumbo packet)',
        r'(recv buffer|send buffer|socket buffer)',
        r'(rss cpu affinity|network affinity)',
        r'(fiber internet|coax|dsl latency)',
        r'(overclocked ethernet|2\.5g|10gbe)',
        r'(realtek nic|intel nic|killer nic)',
        r'(adapter offloading|checksum offload|tcp offload)',
    ],
    "Mouse/Input": [
        r'(polling rate|1000hz|2000hz|4000hz|8000hz)',
        r'(raw input|rawinput|dinput|xinput)',
        r'(dpi|cpi|sensor)',
        r'(lift off distance|lod)',
        r'(mouse accel|pointer speed|enhance pointer)',
        r'(mouse curve|windows curve|smooth mouse)',
        r'(usb 2\.0 vs 3\.0|usb port mouse)',
        r'(usb hub|usb extender|usb over ip)',
        r'(mouse firmware|firmware update mouse)',
        r'(wired mouse|wireless mouse|dongle)',
        r'(onboard memory|mouse profile)',
        r'(interrupt delay|mouse irq|mouse dpc)',
        r'(mouclass|kbdclass|hidclass)',
        r'(mouse feel|mouse tracking|mouse jitter)',
        r'(mouse drift|mouse wobble|cursor drift)',
        r'(click latency|debounce|input delay click)',
        r'(aim assist|mouse correction)',
    ],
    "CPU/System": [
        r'(cpu affinity|set affinity|process affinity)',
        r'(process priority|realtime priority|high priority)',
        r'(thread scheduling|windows scheduler)',
        r'(background process|kill process|task manager)',
        r'(antivirus|windows defender|exclusion)',
        r'(memory leak|ram usage|pagefile)',
        r'(power plan|high performance|ultimate performance)',
        r'(timer resolution|timebeginperiod|ntsettimerresolution)',
        r'(dpc latency|dpc storm|isr)',
        r'(kernel|ring 0|ring 3)',
        r'(intel hybrid|e-core|p-core)',
        r'(x3d|3d v-cache|vcache)',
        r'(dual ccd|chiplet|ccd)',
        r'(fclk|infinity fabric clock)',
        r'(memory controller|imc)',
        r'(cache miss|l3 cache|latency cache)',
        r'(cpu temp|thermal throttle|tjmax)',
        r'(turbo boost|boost clock|all core)',
        r'(secure virtual machine|hypervisor)',
        r'(windows 11|windows 10|build number)',
        r'(game mode|xbox game mode)',
        r'(fullscreen exclusive|borderless|windowed)',
        r'(presentation mode|flip queue size)',
    ],
    "Audio (DPC/IRQ)": [
        r'(realtek audio|hd audio|audio driver)',
        r'(audio enhancement|spatial sound|dolby)',
        r'(usb headset|usb audio|usb sound)',
        r'(dac|external dac|sound card)',
        r'(audio dpc|audio irq|audio interrupt)',
        r'(wasapi|asio|exclusive mode)',
        r'(sample rate|bit rate|audio latency)',
        r'(microphone|mic driver|recording)',
    ],
    "EMI/Hardware": [
        r'(emi|electromagnetic interference|shielding)',
        r'(fiber optic dp|optical displayport)',
        r'(grounding|ground loop|electrical noise)',
        r'(psu noise|psu quality|power supply)',
        r'(motherboard quality|vrm|mosfet)',
        r'(surge protector|ups|power filter)',
        r'(cpu cooler|cooler contact|thermal paste)',
        r'(ram slot|dimm slot|single channel)',
        r'(m\.2 thermal|nvme temp|ssd thermal)',
    ],
}

results = defaultdict(list)
seen = set()

for post in all_posts:
    content = post['content']
    if len(content) < 40:
        continue
    for category, patterns in SEARCHES.items():
        for pattern in patterns:
            if re.search(pattern, content, re.IGNORECASE):
                key = f"{category}|{pattern}|{post['thread_url']}"
                if key not in seen:
                    seen.add(key)
                    snippet = content[:500].replace('\n',' ').strip()
                    results[category].append({
                        'pattern': pattern,
                        'thread': post['thread_title'],
                        'url': post['thread_url'],
                        'author': post['author'],
                        'snippet': snippet
                    })
                    break

# Guardar
with open('bb_deep2.json', 'w', encoding='utf-8') as f:
    json.dump(results, f, indent=2, ensure_ascii=False)

for cat, items in results.items():
    print(f"\n[{cat}]: {len(items)} hallazgos")
