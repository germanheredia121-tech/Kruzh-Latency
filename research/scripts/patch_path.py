import re

with open('optimizacion-input-lag-definitivo.ps1', 'r', encoding='utf-8-sig') as f:
    content = f.read()

# Fix 1: default path uses PSScriptRoot
old = 'if ([string]::IsNullOrWhiteSpace($nvramPath)) { $nvramPath = "nvram.txt" }'
new = 'if ([string]::IsNullOrWhiteSpace($nvramPath)) { $nvramPath = (Join-Path $PSScriptRoot "nvram.txt") }'
content = content.replace(old, new, 1)

# Fix 2: ReadAllText also uses full path (in case relative was passed)
old2 = '    $raw = [System.IO.File]::ReadAllText($nvramPath)'
new2 = '    if (-not [System.IO.Path]::IsPathRooted($nvramPath)) { $nvramPath = Join-Path $PSScriptRoot $nvramPath }\n    $raw = [System.IO.File]::ReadAllText($nvramPath)'
content = content.replace(old2, new2, 1)

with open('optimizacion-input-lag-definitivo.ps1', 'w', encoding='utf-8-sig') as f:
    f.write(content)

print("Done. Patched PSScriptRoot fix.")
