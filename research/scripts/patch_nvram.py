import re

with open('optimizacion-input-lag-definitivo.ps1', 'r', encoding='utf-8-sig') as f:
    content = f.read()

NEW_FUNC = r"""# ==================== HERRAMIENTAS BLURBUSTERS (ANALISIS) ====================
function Invoke-NvramAnalyzer {
    Clear-Host; Write-Banner "ANALIZADOR BIOS/NVRAM - OPTIMIZACION COMPLETA" "Magenta"
    Write-Host "Analiza tu nvram.txt (AMI Scewin) en busca de TODOS los ajustes que" -ForegroundColor DarkGray
    Write-Host "afectan el input lag, desde C-States hasta PCIe Spread Spectrum." -ForegroundColor DarkGray
    Write-Host ""
    $nvramPath = Read-Host "Ruta al archivo nvram.txt (Enter = carpeta actual)"
    if ([string]::IsNullOrWhiteSpace($nvramPath)) { $nvramPath = "nvram.txt" }
    if (-not (Test-Path $nvramPath)) {
        Write-Host "No se encontro: $nvramPath" -ForegroundColor Red
        Write-Host "Exportar con Scewin: scewin.exe /O /S nvram.txt" -ForegroundColor Yellow
        Pause; return
    }
    Write-Host "Leyendo y parseando $nvramPath..." -ForegroundColor Cyan
    $raw = [System.IO.File]::ReadAllText($nvramPath)
    if (-not $raw) { Write-Host "Error leyendo el archivo." -ForegroundColor Red; Pause; return }

    function Get-NvramValue($raw, $questionPattern) {
        $blockRx = "Setup Question\s*=\s*[^\r\n]*$questionPattern[^\r\n]*[\r\n]+((?:.|\n)*?)(?=\r?\n\r?\n|\Z)"
        $blockM = [regex]::Match($raw, $blockRx, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if (-not $blockM.Success) { return $null }
        $block = $blockM.Value
        $starRx = "\s*\*\[[\w]+\]\s*(.+?)(?:\s*//|\r|\n|\Z)"
        $starM = [regex]::Match($block, $starRx, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($starM.Success) { return $starM.Groups[1].Value.Trim() }
        return "No encontrado"
    }

    $checks = @(
        @{ Q="AMD KVM Mouse Protocol"; Rec="Absolute"; Bad=@("Auto","Simple"); Impact="CRITICO"; Reason="Simple/Auto interpolan la posicion del mouse. Absolute = coordenadas 1:1 directas." },
        @{ Q="Global C-state Control"; Rec="Disabled"; Bad=@("Auto","Enabled"); Impact="CRITICO"; Reason="Permite C6/C7 deep sleep. Wakeup = 100-300us de latencia extra en cada frame." },
        @{ Q="Aggressive Link PM Capability"; Rec="Disabled"; Bad=@("Enabled"); Impact="CRITICO"; Reason="Manda el HBA de SATA a Partial/Slumber. DPC spikes al volver de idle." },
        @{ Q="IOMMU"; Rec="Disabled"; Bad=@("Auto","Enabled"); Impact="ALTO"; Reason="Overhead de traduccion de memoria en I/O USB y red. Desactivar = menos latencia." },
        @{ Q="Int. Clk Differential Spread"; Rec="Disabled"; Bad=@("Auto","Enabled"); Impact="ALTO"; Reason="Spread Spectrum del chipset = jitter en buses PCIe y USB." },
        @{ Q="SB Clock Spread Spectrum"; Rec="Disabled"; Bad=@("Auto","Enabled"); Impact="ALTO"; Reason="Spread del South Bridge = jitter adicional en SATA y USB." },
        @{ Q="Chipset Power Saving Features"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="ALTO"; Reason="Mantiene PCIe/USB siempre activos, sin latencia de wakeup." },
        @{ Q="XHCI D3 Support"; Rec="Disabled"; Bad=@("Auto","Enabled"); Impact="ALTO"; Reason="D3 power del xHCI (USB 3.0) = latencia de wakeup. Critico para mouse 1000Hz+." },
        @{ Q="PSPP Policy"; Rec="Disabled"; Bad=@("Performance","Balanced","Power Saving","Auto"); Impact="ALTO"; Reason="PCIe Speed Power Policy baja velocidad del link en idle, re-training con latencia." },
        @{ Q="SVM Mode"; Rec="Disabled"; Bad=@("Enabled"); Impact="MEDIO"; Reason="Virtualizacion AMD agrega hypervisor layer. Desactivar si no usas VMs." },
        @{ Q="AB Clock Gating"; Rec="Disabled"; Bad=@("Auto","Enabled"); Impact="MEDIO"; Reason="Clock gating del AMD Bus genera micro-latencias al re-activar el reloj del bus." },
        @{ Q="EHCI D3 Support"; Rec="Disabled"; Bad=@("Auto","Enabled"); Impact="MEDIO"; Reason="D3 power del EHCI = latencia de wakeup en USB legacy." },
        @{ Q="SB C1E Support"; Rec="Disabled"; Bad=@("Auto","Enabled"); Impact="MEDIO"; Reason="C1E del chipset coordina paradas de reloj con CPU, puede desincronizar el timer." },
        @{ Q="USB MSI Option"; Rec="Enabled"; Bad=@("Disabled","Auto"); Impact="MEDIO"; Reason="MSI para USB reduce latencia de interrupcion vs PIC legacy IRQs." },
        @{ Q="MsiDis in HPET"; Rec="Enabled"; Bad=@("Disabled","Auto"); Impact="MEDIO"; Reason="MSI en HPET mejora precision del temporizador de alta resolucion." },
        @{ Q="Fusion Message C State"; Rec="Disabled"; Bad=@("Enabled"); Impact="BAJO"; Reason="FCH C-state Coordination genera mensajes extra entre CPU y chipset." },
        @{ Q="EHCI Traffic Handling"; Rec="Disabled"; Bad=@("Enabled"); Impact="BAJO"; Reason="Genera IRQs para sacar al CPU del C-state por trafico USB EHCI. Innecesario." },
        @{ Q="USB Phy Power Down"; Rec="Disabled"; Bad=@("Auto","Enabled"); Impact="BAJO"; Reason="Apaga el PHY USB en suspend. Latencia al volver de S4/S5." },
        @{ Q="TimerTick Tracking"; Rec="Disabled"; Bad=@("Enabled"); Impact="BAJO"; Reason="Mensajes periodicos al CPU sobre el timer = overhead de ISR." },
        @{ Q="Clock Interrupt Tag"; Rec="Disabled"; Bad=@("Enabled"); Impact="BAJO"; Reason="Marcar interrupciones de timer = overhead innecesario." },
        @{ Q="Fusion Message C Multi-Core"; Rec="Disabled"; Bad=@("Enabled"); Impact="BAJO"; Reason="Rastrear C-state por core = mensajes adicionales al chipset." },
        @{ Q="Network Stack Driver Support"; Rec="Disabled"; Bad=@("Enabled"); Impact="BAJO"; Reason="Stack PXE/red consume recursos al boot." }
    )

    $needsChange = @(); $alreadyOk = @(); $notFound = @()
    foreach ($c in $checks) {
        $val = Get-NvramValue $raw $c.Q
        if ($null -eq $val) { $notFound += $c.Q; continue }
        $isBad = $false
        foreach ($b in $c.Bad) { if ($val -like "*$b*") { $isBad = $true; break } }
        $entry = [PSCustomObject]@{ Q=$c.Q; Val=$val; Rec=$c.Rec; Impact=$c.Impact; Reason=$c.Reason }
        if ($isBad) { $needsChange += $entry } else { $alreadyOk += $entry }
    }

    Write-Host ""; Write-Host "  Total: $($checks.Count)  Requieren cambio: $($needsChange.Count)  Ya OK: $($alreadyOk.Count)  No en BIOS: $($notFound.Count)" -ForegroundColor White; Write-Host ""

    if ($needsChange.Count -gt 0) {
        Write-Host "[ REQUIEREN CAMBIO ]" -ForegroundColor Red; Write-Host ""
        foreach ($r in $needsChange) {
            $impactColor = switch ($r.Impact) { "CRITICO" { "Red" } "ALTO" { "Yellow" } "MEDIO" { "Cyan" } default { "DarkGray" } }
            Write-Host "  [$($r.Impact)] $($r.Q)" -ForegroundColor $impactColor
            Write-Host "    Actual     : $($r.Val)" -ForegroundColor White
            Write-Host "    Recomendado: $($r.Rec)" -ForegroundColor Green
            Write-Host "    Por que    : $($r.Reason)" -ForegroundColor DarkGray
            Write-Host ""
        }
    }

    if ($alreadyOk.Count -gt 0) {
        Write-Host "[ YA BIEN CONFIGURADOS ]" -ForegroundColor Green
        foreach ($r in $alreadyOk) { Write-Host "  [OK] [$($r.Impact)] $($r.Q): $($r.Val)" -ForegroundColor DarkGray }
        Write-Host ""
    }
    Write-Host "Analisis NVRAM Finalizado." -ForegroundColor Cyan
    Pause
}
"""

# Find and replace the old Invoke-NvramAnalyzer
pattern = r'# =+ HERRAMIENTAS BLURBUSTERS.*?^function Invoke-NvramAnalyzer \{.*?^\}'
m = re.search(pattern, content, re.DOTALL | re.MULTILINE)
if m:
    content = content[:m.start()] + NEW_FUNC + content[m.end():]
    print(f"Replaced at position {m.start()}-{m.end()}")
else:
    print("ERROR: Pattern not found!")
    import sys; sys.exit(1)

# Write back with UTF-8 BOM
with open('optimizacion-input-lag-definitivo.ps1', 'w', encoding='utf-8-sig') as f:
    f.write(content)
print("Saved OK.")
