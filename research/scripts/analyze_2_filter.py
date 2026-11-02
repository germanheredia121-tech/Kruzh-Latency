import json
import sys
import re
sys.stdout.reconfigure(encoding='utf-8')

KNOWN_FIXES_ALREADY_IN_SCRIPT = {
    "mouse acceleration", "enhance pointer precision", "markc", "rawaccel",
    "mousedataqueue", "keyboarddataqueue", "usb power", "selective suspend",
    "hid", "msi mode", "hdcp", "gpu p0", "nvidia reflex", "ultra low latency",
    "hags", "hardware accelerated gpu scheduling", "mpo", "multiplane overlay",
    "independent flip", "nagle", "interrupt moderation", "lso", "rss", "flow control",
    "qos", "ecn", "network throttling", "tcp autotuning", "auto-tuning", "ntp",
    "timer resolution", "game bar", "game dvr", "copilot", "windows update",
    "ultimate performance", "mmcss", "core parking", "sysmain", "superfetch",
    "visual effects", "scheduler", "cfg", "pcore", "bcd", "dynamictick",
    "platform tick", "irq", "hpet", "audio dpc", "cpu idle", "c-state",
    "cpu boost", "background hw accel", "steam", "bufferbloat",
    "win32priorityseparation", "priority separation", "csrss", "dwm compositor",
    "pcie aspm", "ndis buffer", "power throttling",
}

def is_already_known(text):
    text_low = text.lower()
    for k in KNOWN_FIXES_ALREADY_IN_SCRIPT:
        if k in text_low:
            return True
    return False

# Patrones que normalmente indican un fix o hallazgo
FIX_PATTERNS = [
    r'\bfix\b', r'\bsolved\b', r'\bsolution\b', r'\bfound\b', r'\bworkd\b',
    r'\bworked\b', r'\breduced\b', r'\bimproved\b', r'\bhelpd\b', r'\bfixed\b',
    r'\bhelped\b', r'\btry\b', r'\bregistry\b', r'\bdriver\b', r'\bbios\b',
    r'\bdisable\b', r'\benable\b', r'\bsetting\b', r'\btweak\b', r'\blatency\b',
    r'\binput lag\b', r'\bjitter\b', r'\bdesync\b', r'\bfloaty\b', r'\bstutter\b',
    r'\bping\b', r'\bbufferbloat\b', r'\bplacebo\b', r'\breal\b',
]

FIX_RE = re.compile('|'.join(FIX_PATTERNS), re.IGNORECASE)

PLACEBO_INDICATORS = [
    'placebo', 'no difference', 'no effect', 'doesnt help', "doesn't help",
    'not real', 'snake oil', 'myth', 'debunked', 'nocebo', 'no impact',
    'negligible', 'imagination'
]

with open('blurbusters_inputlag.json', encoding='utf-8') as f:
    data = json.load(f)

results = []

for thread in data:
    title = thread['title']
    tid = thread['thread_id']
    url = thread['url']
    
    thread_relevant_posts = []
    
    for post in thread['posts']:
        content = post.get('content', '')
        if not content or len(content) < 30:
            continue
        # Solo posts que parecen hablar de fixes
        if not FIX_RE.search(content):
            continue
        
        # Detectar si es placebo
        is_placebo = any(p in content.lower() for p in PLACEBO_INDICATORS)
        
        # Categorizar si ya lo tenemos o es nuevo
        already_in_script = is_already_known(content)
        
        thread_relevant_posts.append({
            'author': post.get('author', 'Unknown'),
            'is_placebo': is_placebo,
            'already_in_script': already_in_script,
            'snippet': content[:600].replace('\n', ' ').strip()
        })
    
    if thread_relevant_posts:
        results.append({
            'thread_id': tid,
            'title': title,
            'url': url,
            'posts': thread_relevant_posts
        })

# Guardar resultado filtrado
with open('bb_filtered.json', 'w', encoding='utf-8') as f:
    json.dump(results, f, indent=2, ensure_ascii=False)

print(f"Threads relevantes encontrados: {len(results)}")
total_p = sum(len(r['posts']) for r in results)
print(f"Posts relevantes encontrados: {total_p}")
new_fixes = [r for r in results if any(not p['already_in_script'] and not p['is_placebo'] for p in r['posts'])]
placebos = [r for r in results if any(p['is_placebo'] for p in r['posts'])]
print(f"Threads con fixes NUEVOS potenciales: {len(new_fixes)}")
print(f"Threads con discusion de PLACEBOS: {len(placebos)}")
