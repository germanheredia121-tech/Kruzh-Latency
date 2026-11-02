import re

with open('apply_functions.txt', 'r', encoding='utf-8') as f:
    content = f.read()

modules = re.findall(r'=== MODULE: (\w+) ===\n(.*?)(?=\n=== MODULE: |\Z)', content, re.DOTALL)

output = "function Get-State($name) {\n    switch ($name) {\n"

for mod_name, code in modules:
    # Try to find Set-ItemProperty
    reg_set = re.search(r'Set-ItemProperty\s+(?:"|)(HK[LMU].*?)(?:"|)\s+(?:"|-Name\s+)?([A-Za-z0-9_]+)(?:"|)?\s+(?:-Value\s+)?(?:")?([0-9A-Za-z_]+)(?:")?', code, re.IGNORECASE)
    reg_add = re.search(r'reg add\s+"(HK[LMU][^"]+)"\s+/v\s+([A-Za-z0-9_]+).*?/d\s+([0-9A-Za-z_]+)', code, re.IGNORECASE)
    
    if reg_set:
        path = reg_set.group(1).strip('"')
        key = reg_set.group(2).strip('"')
        val = reg_set.group(3).strip('"')
        check = f"(Get-ItemProperty -Path '{path}' -Name '{key}' -ErrorAction SilentlyContinue).'{key}' -eq '{val}'"
    elif reg_add:
        path = reg_add.group(1).replace("HKLM", "HKLM:").replace("HKCU", "HKCU:").replace("\\", "/")
        key = reg_add.group(2)
        val = reg_add.group(3)
        check = f"(Get-ItemProperty -Path '{path}' -Name '{key}' -ErrorAction SilentlyContinue).'{key}' -eq '{val}'"
    elif "Enable-NetAdapterChecksumOffload" in code:
        check = f'$false # Dificil checkear via PS simple, asume false o usa StateKey'
    elif "powercfg" in code:
        check = f'$false # Check de powercfg complejo'
    elif "Disable-NetAdapterAdvancedProperty" in code:
        check = f'$false # Check de red complejo'
    elif "bcdedit" in code:
        check = f'$false # Check de bcdedit'
    else:
        check = f"(Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1"
        
    output += f"        '{mod_name}' {{ return {check} }}\n"

output += "        default { return ((Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1) }\n"
output += "    }\n}\n"

with open('live_state.ps1', 'w', encoding='utf-8') as out:
    out.write(output)
print("Generado live_state.ps1")
