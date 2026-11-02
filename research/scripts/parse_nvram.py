import re
import sys
sys.stdout.reconfigure(encoding='utf-8')

with open('nvram.txt', 'r', encoding='utf-8', errors='ignore') as f:
    raw = f.read()

# Parse all settings blocks
blocks = re.split(r'\n\n(?=Setup Question\t=)', raw)

settings = []
for block in blocks:
    q = re.search(r'Setup Question\s*=\s*(.+)', block)
    h = re.search(r'Help String\s*=\s*(.+)', block)
    tok = re.search(r'Token\s*=(-?\w+)', block)
    
    # Find current value - line with *
    current_opts = re.findall(r'\*\[(\w+)\](.+?)(?:\s*//|$)', block, re.MULTILINE)
    all_opts = re.findall(r'\s*\*?\[(\w+)\](.+?)(?:\s*//|$)', block, re.MULTILINE)
    
    if q:
        name = q.group(1).strip()
        help_str = h.group(1).strip() if h else ''
        current_val = current_opts[0][1].strip() if current_opts else 'N/A'
        all_options = [(o[0], o[1].strip()) for o in all_opts]
        settings.append({
            'name': name,
            'help': help_str,
            'current': current_val,
            'options': all_options,
            'token': tok.group(1) if tok else ''
        })

print(f"Total settings parsed: {len(settings)}\n")

# Keywords that may affect input lag
keywords = [
    'c-state', 'cstate', 'c state', 'power', 'idle', 'sleep', 'halt',
    'usb', 'xhci', 'ehci', 'smi', 'spread spectrum', 'aspm', 'l0', 'l1',
    'iommu', 'svm', 'virtuali', 'fan', 'thermal', 'turbo', 'boost',
    'pcie', 'gen', 'link speed', 'above 4g', 'resizable', 'rebar',
    'secure boot', 'fast boot', 'ultra fast', 'lan', 'network', 'realtek',
    'mouse', 'kvm', 'usb legacy', 'ps/2', 'hpet', 'irq', 'dpc',
    'memory', 'dram', 'xmp', 'expo', 'gear', 'timing',
    'precision boost', 'pbo', 'fclk', 'uclk', 'clock', 'frequency',
    'audio', 'hd audio', 'onboard',
    'cpu', 'core', 'package', 'deep sleep',
    'intel speed', 'speedstep', 'eist',
    'sgx', 'tpm', 'ftpm', 'ptt',
    'bifurcation', 'sriovt', 'ari support',
]

relevant = []
for s in settings:
    text = (s['name'] + ' ' + s['help']).lower()
    for kw in keywords:
        if kw in text:
            relevant.append(s)
            break

print(f"Settings relevantes al input lag: {len(relevant)}\n")
for s in relevant:
    print(f"[{s['name']}]")
    print(f"  Help: {s['help']}")
    print(f"  Current: {s['current']}")
    print(f"  Options: {[o[1] for o in s['options']]}")
    print()
