import re

with open('nvram.txt', 'r', encoding='utf-8', errors='ignore') as f:
    raw = f.read()

blocks = re.split(r'\n\n(?=Setup Question\t=)', raw)

settings = []
for block in blocks:
    q = re.search(r'Setup Question\s*=\s*(.+)', block)
    h = re.search(r'Help String\s*=\s*(.+)', block)
    
    current_opts = re.findall(r'[ \t]*\*\[(\w+)\](.+?)(?:\s*//|$)', block, re.MULTILINE)
    all_opts = re.findall(r'[ \t]*\*?\[(\w+)\](.+?)(?:\s*//|$)', block, re.MULTILINE)
    
    if q:
        name = q.group(1).strip()
        help_str = h.group(1).strip() if h else ''
        current_val = current_opts[0][1].strip() if current_opts else 'N/A'
        all_options = [o[1].strip() for o in all_opts]
        settings.append({'name': name, 'help': help_str, 'current': current_val, 'options': list(dict.fromkeys(all_options))})

# New keywords to look for deeper optimizations
keywords = [
    'power', 'idle', 'sleep', 'halt', 'c-state', 'smi', 'aspm', 'l0', 'l1', 
    'pbo', 'precision boost', 'fclk', 'uclk', 'gear', 'timing',
    'usb', 'legacy', 'xhci', 'ehci', 'audio', 'hd audio', 'onboard', 
    'fast boot', 'secure boot', 'tpm', 'ftpm', 'ptt', 'csm',
    'bifurcation', 'sriov', 'ari', 'cppc', 'acpi', 'hpet', 'smbus',
    'sata', 'ahci', 'nvme', 'pcie', 'gen', 'link speed'
]

already_checked = [
    "AMD KVM Mouse Protocol", "Global C-state Control", "Aggressive Link PM Capability", 
    "IOMMU", "Int. Clk Differential Spread", "SB Clock Spread Spectrum", 
    "Chipset Power Saving Features", "XHCI D3 Support", "PSPP Policy", "SVM Mode", 
    "AB Clock Gating", "EHCI D3 Support", "SB C1E Support", "USB MSI Option", 
    "MsiDis in HPET", "Fusion Message C State", "EHCI Traffic Handling", 
    "USB Phy Power Down", "TimerTick Tracking", "Clock Interrupt Tag", 
    "Fusion Message C Multi-Core", "Network Stack Driver Support"
]

def is_already_checked(name):
    for c in already_checked:
        if c.lower() in name.lower() or name.lower() in c.lower():
            return True
    return False

relevant = []
for s in settings:
    if is_already_checked(s['name']):
        continue
    text = (s['name'] + ' ' + s['help']).lower()
    if any(kw in text for kw in keywords):
        relevant.append(s)

with open('nvram_extra_candidates.txt', 'w', encoding='utf-8') as out:
    for s in relevant:
        out.write(f"[{s['name']}]\n")
        out.write(f"  Help: {s['help']}\n")
        out.write(f"  Options: {s['options']}\n\n")

print(f"Found {len(relevant)} more potential settings.")
