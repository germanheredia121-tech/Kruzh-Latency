import re

with open('optimizacion-input-lag-definitivo.ps1', 'r', encoding='utf-8-sig') as f:
    content = f.read()

# Fix 1: HAGS score check — was checking the wrong registry key (HKCU DirectX preference, not the actual HAGS key)
# The score function checks HKCU:\Software\Microsoft\DirectX\UserGpuPreferences for gpupreference=2
# But HAGS is actually at HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode = 2
# We need to check BOTH: HwSchMode == 2 (system-wide HAGS) is the real check.
old_hags = '''    # HAGS (4 pts)
    $hagsVal=(Get-ItemProperty "HKCU:\\Software\\Microsoft\\DirectX\\UserGpuPreferences" -EA SilentlyContinue).DirectXUserGlobalSettings
    if ($hagsVal -match "gpupreference=2") {
        if ($script:HW.GpuGen -match "RTX[34]0|RX[67]") { $totalScore+=4; $ok+="HAGS: ON (correcto para tu GPU)" }
        else { $totalScore+=2; $warnings+="HAGS: ON (verifica si beneficia tu GPU)" }
    } else {
        if ($script:HW.GpuGen -match "GTX1[06]") { $totalScore+=2; $warnings+="HAGS: OFF (correcto para GPU legada)" }
        else { $issues+="HAGS: OFF - considera activar modulo [19] para GPU moderna" }
    }'''
new_hags = '''    # HAGS (4 pts) — check via HwSchMode (el valor real del sistema, no la preferencia DirectX por app)
    $hagsMode=(Get-ItemProperty "HKLM:\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers" -Name "HwSchMode" -EA SilentlyContinue).HwSchMode
    if ($hagsMode -eq 2) {
        if ($script:HW.GpuGen -match "RTX[34]0|RX[67]") { $totalScore+=4; $ok+="HAGS: ON (correcto para tu GPU)" }
        else { $totalScore+=2; $warnings+="HAGS: ON (verifica si beneficia tu GPU generacion $($script:HW.GpuGen))" }
    } else {
        if ($script:HW.GpuGen -match "GTX1[06]") { $totalScore+=2; $warnings+="HAGS: OFF (correcto para GPU legada)" }
        else { $issues+="HAGS: OFF - considera activar modulo [13] para GPU moderna" }
    }'''

if old_hags in content:
    content = content.replace(old_hags, new_hags, 1)
    print("Fix 1 (HAGS score check): OK")
else:
    print("Fix 1: NOT FOUND - checking for whitespace issues...")
    # Try flexible search
    m = re.search(r'# HAGS \(4 pts\).*?else \{[^}]+\}', content, re.DOTALL)
    if m:
        content = content[:m.start()] + new_hags + content[m.end():]
        print("Fix 1 (regex): OK")
    else:
        print("Fix 1: FAILED")

# Fix 2: CPU Scheduler wrong module reference in score - [22] should be [42]
old_sched = 'else { $issues+="CPU Scheduler: no optimizado (modulo [22])" }'
new_sched = 'else { $issues+="CPU Scheduler: no optimizado - activa modulo [42] en [O] Optimizaciones" }'
if old_sched in content:
    content = content.replace(old_sched, new_sched, 1)
    print("Fix 2 (scheduler module ref): OK")
else:
    print("Fix 2: NOT FOUND")

# Fix 3: Timer resolution wrong module ref [22] -> [30]
old_timer = 'else { $issues+="Timer resolution: ${timerMs}ms - MAL, activa modulo [22]" }'
new_timer = 'else { $issues+="Timer resolution: ${timerMs}ms - MAL, activa modulo [30] en [O] Optimizaciones" }'
if old_timer in content:
    content = content.replace(old_timer, new_timer, 1)
    print("Fix 3 (timer module ref): OK")
else:
    print("Fix 3: NOT FOUND")

with open('optimizacion-input-lag-definitivo.ps1', 'w', encoding='utf-8-sig') as f:
    f.write(content)
print("\nAll fixes saved.")
