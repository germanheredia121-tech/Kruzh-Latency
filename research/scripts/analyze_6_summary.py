import json
import sys
sys.stdout.reconfigure(encoding='utf-8')

with open('bb_deep2.json', encoding='utf-8') as f:
    data = json.load(f)

for cat, items in data.items():
    print(f"\n=== {cat} ===")
    patterns_seen = set()
    for item in items:
        if item['pattern'] not in patterns_seen:
            patterns_seen.add(item['pattern'])
            print(f" - [{item['pattern']}] Thread: {item['thread']}")
        if len(patterns_seen) >= 6:
            break
