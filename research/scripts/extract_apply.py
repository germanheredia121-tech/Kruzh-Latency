import re

with open('optimizacion-input-lag-definitivo.ps1', 'r', encoding='utf-8') as f:
    content = f.read()

# Find all function Apply-*
functions = re.findall(r'(?i)(function Apply-(\w+)\s*{.*?^})', content, re.MULTILINE | re.DOTALL)

with open('apply_functions.txt', 'w', encoding='utf-8') as out:
    for full_match, func_name in functions:
        out.write(f"=== MODULE: {func_name} ===\n")
        out.write(full_match + "\n\n")

print(f"Extracted {len(functions)} functions.")
