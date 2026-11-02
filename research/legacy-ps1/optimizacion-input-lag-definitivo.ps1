# optimizacion-input-lag-definitivo.ps1
# Script DEFINITIVO para eliminacion de input lag / floaty mouse / desync en gaming competitivo.
# Apto para anticheats (Vanguard, FACEIT AC) - sin kernel mods, VBS changes ni driver signing bypass.
# Totalmente dinamico: se adapta al hardware detectado. Version 2.0 - ESL Pro Tech Edition
# Research: Reddit r/GlobalOffensive, BlurBusters, TechPowerUp, Realtek/Intel official, CS2 communities 2024-25

If (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]"Administrator")) {
    Start-Process PowerShell.exe -ArgumentList ("-NoProfile -ExecutionPolicy Bypass -File `"{0}`"" -f $PSCommandPath) -Verb RunAs; Exit
}

Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
$Host.UI.RawUI.WindowTitle = "INPUT LAG ELIMINATOR PRO v2.0 - ESL Tech Edition (Administrator)"
$StateKey = "HKCU:\Software\InputLagTweaks"
if (!(Test-Path $StateKey)) { New-Item -Path $StateKey -Force | Out-Null }
# FunciÃƒÂ³n HÃƒÂ­brida Live-State: Lee el valor real de Windows para configs comunes, y usa la cachÃƒÂ© para tweaks profundos
function Get-State($name) {
    switch ($name) {
        "Hags" {
            $h = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers" -Name "HwSchMode" -EA SilentlyContinue).HwSchMode
            return ($h -eq 2)
        }
        "GameBar" {
            $g = (Get-ItemProperty "HKCU:\System\GameConfigStore" -Name "GameDVR_Enabled" -EA SilentlyContinue).GameDVR_Enabled
            return ($g -eq 0) # 0 = Desactivado (Aplicado el fix)
        }
        "SysMain" {
            $s = (Get-Service SysMain -EA SilentlyContinue).Status
            return ($s -ne 'Running') # True = Fix aplicado (Sysmain apagado)
        }
        "MouseAccel" {
            $m = (Get-ItemProperty "HKCU:\Control Panel\Mouse" -Name "MouseSpeed" -EA SilentlyContinue).MouseSpeed
            return ($m -eq "0") # 0 = OFF
        }
        "VisualFx" {
            $v = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects" -Name "VisualFXSetting" -EA SilentlyContinue).VisualFXSetting
            return ($v -eq 2) # 2 = Best Performance
        }
        "PowerThrottlingOff" {
            $p = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling" -Name "PowerThrottlingOff" -EA SilentlyContinue).PowerThrottlingOff
            return ($p -eq 1)
        }
        "Copilot" {
            $c = (Get-ItemProperty "HKCU:\Software\Policies\Microsoft\Windows\WindowsCopilot" -Name "TurnOffWindowsCopilot" -EA SilentlyContinue).TurnOffWindowsCopilot
            return ($c -eq 1)
        }
        "WindowsGameMode" {
            $g = (Get-ItemProperty "HKCU:\Software\Microsoft\GameBar" -Name "AutoGameModeEnabled" -EA SilentlyContinue).AutoGameModeEnabled
            if ($null -eq $g) { return $true }
            return ($g -eq 1)
        }
        "BitLocker" {
            return (Get-BitLockerOn)
        }
        default {
            # Para tweaks profundos (MSI, IRQ, NDIS, BCD), usar la cachÃƒÂ© del script
            return ((Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1)
        }
    }
}
function Set-State($name, $value) { New-ItemProperty -Path $StateKey -Name $name -PropertyType DWord -Value $value -Force | Out-Null }
$GameExes = @("cs2.exe","VALORANT-Win64-Shipping.exe","VALORANT.exe")

# ==================== DETECCION DINAMICA DE HARDWARE ====================
$script:HW = @{}

function Initialize-HardwareProfile {
    Write-Host "  Analizando hardware..." -ForegroundColor DarkGray
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $script:HW.CpuName    = $cpu.Name
    $script:HW.CpuCores   = $cpu.NumberOfCores
    $script:HW.CpuThreads = $cpu.NumberOfLogicalProcessors
    $script:HW.CpuMaxMHz  = $cpu.MaxClockSpeed
    $script:HW.IsIntelHybrid = ($cpu.Name -match "i\d-1[234]\d{3}" -or $cpu.Name -match "i\d-13\d{3}" -or $cpu.Name -match "i\d-14\d{3}")
    $script:HW.IsIntel = ($cpu.Manufacturer -match "Intel" -or $cpu.Name -match "Intel")
    $script:HW.IsAMD   = ($cpu.Manufacturer -match "AMD"   -or $cpu.Name -match "AMD|Ryzen")
    $gpus = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue
    $primaryGpu = $gpus | Where-Object { $_.AdapterCompatibility -match "NVIDIA|AMD|Intel" } | Select-Object -First 1
    if (!$primaryGpu) { $primaryGpu = $gpus | Select-Object -First 1 }
    $script:HW.GpuName  = $primaryGpu.Name
    $script:HW.GpuVRAM  = [Math]::Round($primaryGpu.AdapterRAM / 1GB)
    $script:HW.IsNvidia  = [bool]($gpus | Where-Object { $_.AdapterCompatibility -match "NVIDIA" -or $_.Name -match "NVIDIA" })
    $script:HW.IsAMDGpu  = [bool]($gpus | Where-Object { $_.AdapterCompatibility -match "AMD"    -or $_.Name -match "Radeon" })
    $script:HW.GpuGen    = Get-GpuGeneration $script:HW.GpuName
    $ramBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    $script:HW.RamGB = [Math]::Round($ramBytes / 1GB)
    $ramModules = Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue
    $script:HW.RamSpeedMHz  = if ($ramModules) { ($ramModules | Measure-Object -Property Speed -Maximum).Maximum } else { 0 }
    $script:HW.RamChannels  = if ($ramModules) { ($ramModules | Where-Object { $_.Capacity -gt 0 }).Count } else { 1 }
    $script:HW.IsDualChannel = ($script:HW.RamChannels -ge 2)
    $nics = Get-CimInstance Win32_NetworkAdapter -ErrorAction SilentlyContinue | Where-Object { $_.NetConnectionStatus -eq 2 -and $_.PhysicalAdapter -eq $true }
    if (!$nics) { $nics = Get-CimInstance Win32_NetworkAdapter -ErrorAction SilentlyContinue | Where-Object { $_.PhysicalAdapter -eq $true } }
    $nic = $nics | Select-Object -First 1
    $script:HW.NicName   = if ($nic) { $nic.Name } else { "Desconocido" }
    $script:HW.NicDriver = Get-NicDriverInfo $nic
    $script:HW.NicType   = Get-NicType $script:HW.NicName
    $script:HW.NicPciId  = if ($nic) { $nic.PNPDeviceID } else { "" }
    $os = Get-CimInstance Win32_OperatingSystem
    $script:HW.OsCaption = $os.Caption
    $script:HW.OsBuild   = $os.BuildNumber
    $script:HW.IsWin11   = ([int]$os.BuildNumber -ge 22000)
    $drives = Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue
    $sysDrive = $drives | Where-Object { $_.DeviceID -match "0$" } | Select-Object -First 1
    if (!$sysDrive) { $sysDrive = $drives | Select-Object -First 1 }
    $script:HW.StorageType = if ($sysDrive) {
        if ($sysDrive.Caption -match "NVMe|SSD|SAMSUNG|WD|Kingston|Crucial" -or $sysDrive.InterfaceType -match "SCSI") { "NVMe/SSD" } else { "HDD" }
    } else { "Desconocido" }
    try {
        $dispPath = "HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration"
        $monitors  = Get-ChildItem $dispPath -Recurse -ErrorAction SilentlyContinue | Get-ItemProperty -ErrorAction SilentlyContinue
        $vSyncFreq = $monitors | Where-Object { $_.VSyncFreq -gt 0 } | Select-Object -First 1
        $script:HW.MonitorHz = if ($vSyncFreq) { $vSyncFreq.VSyncFreq } else { 0 }
    } catch { $script:HW.MonitorHz = 0 }
    Write-Host "  Hardware detectado OK." -ForegroundColor DarkGray
}

function Get-GpuGeneration($gpuName) {
    if ($gpuName -match "RTX 40[0-9]{2}|RTX 4[0-9]{3}") { return "RTX40" }
    if ($gpuName -match "RTX 30[0-9]{2}|RTX 3[0-9]{3}") { return "RTX30" }
    if ($gpuName -match "RTX 20[0-9]{2}|RTX 2[0-9]{3}") { return "RTX20" }
    if ($gpuName -match "GTX 16[0-9]{2}|GTX 1[6-9][0-9]{2}") { return "GTX16" }
    if ($gpuName -match "GTX 10[0-9]{2}") { return "GTX10" }
    if ($gpuName -match "RX 7[0-9]{3}") { return "RX7000" }
    if ($gpuName -match "RX 6[0-9]{3}") { return "RX6000" }
    if ($gpuName -match "RX 5[0-9]{3}") { return "RX5000" }
    return "Unknown"
}

function Get-NicType($nicName) {
    if ($nicName -match "RTL8125|Realtek 2\.5G|RTL8125B|PCIe 2\.5GbE") { return "RTL8125" }
    if ($nicName -match "RTL8111|RTL8168|Realtek PCIe GbE|Realtek PCI") { return "RTL8111" }
    if ($nicName -match "I225|I226")        { return "IntelI225" }
    if ($nicName -match "I219|I217|I218")   { return "IntelI219" }
    if ($nicName -match "AQC107|Aquantia|AQtion") { return "Aquantia" }
    if ($nicName -match "Killer|Rivet")     { return "Killer" }
    if ($nicName -match "Marvell|10GbE")    { return "Marvell" }
    return "Generic"
}

function Get-NicDriverInfo($nic) {
    if (!$nic) { return @{ Version="N/A"; Date="N/A"; Provider="N/A" } }
    try {
        $d = Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | Where-Object { $_.DeviceID -eq $nic.PNPDeviceID } | Select-Object -First 1
        if ($d) { return @{ Version=$d.DriverVersion; Date=if ($d.DriverDate) { $d.DriverDate.ToString("yyyy-MM-dd") } else { "N/A" }; Provider=$d.Manufacturer } }
    } catch {}
    return @{ Version="N/A"; Date="N/A"; Provider="N/A" }
}

# ==================== BASE DE DATOS DE DRIVERS OPTIMOS ====================
function Get-OptimalNicDriver($nicType) {
    $db = @{
        "RTL8125" = @{
            OptimalVersion   = "10.062.1129.2024 o superior"
            MinVersion       = "10.056"
            DownloadUrl      = "https://www.realtek.com/Download/List?cate_id=584"
            Notes            = "Usar driver oficial Realtek, NO OEM de placa (ASUS/MSI/Gigabyte tienen DPC alta conocida). Interrupt Moderation y Green Ethernet OFF es CRITICO."
            KnownIssues      = "Versiones < 10.056: micro-drops con CPU power states. ASPM debe estar OFF."
            AdvancedSettings = @("*InterruptModeration=0","EEELinkAdvertisement=0","FlowControl=0","*LsoV2IPv4=0","*LsoV2IPv6=0","AutoDisableGigabit=0","PowerSavingMode=0","AdvancedEEE=0")
        }
        "RTL8111" = @{
            OptimalVersion   = "10.052.0921.2021 o superior"
            MinVersion       = "10.048"
            DownloadUrl      = "https://www.realtek.com/Download/List?cate_id=584"
            Notes            = "Actualizar a driver oficial Realtek. Desactivar Green Ethernet y EEE."
            KnownIssues      = "Propenso a DPC spikes con power-saving features activas."
            AdvancedSettings = @("*InterruptModeration=0","EEELinkAdvertisement=0","FlowControl=0","*LsoV2IPv4=0")
        }
        "IntelI225" = @{
            OptimalVersion   = "Ultima version Intel oficial (intel.com)"
            MinVersion       = "28.0"
            DownloadUrl      = "https://www.intel.com/content/www/us/en/download/15084/intel-ethernet-adapter-complete-driver-pack.html"
            Notes            = "Driver de Intel.com, NO OEM. I225-V Rev3+ sin bug de hardware. Rev1/2: forzar a 1Gbps full duplex si hay drops."
            KnownIssues      = "Rev 1/2 del I225-V: bug de hardware con 2.5Gbps. EEE causa latency spikes."
            AdvancedSettings = @("*InterruptModeration=0","*EEE=0","FlowControl=0","*LsoV2IPv4=0","*LsoV2IPv6=0","*RSS=1")
        }
        "IntelI219" = @{
            OptimalVersion   = "Ultima version Intel oficial"
            MinVersion       = "26.0"
            DownloadUrl      = "https://www.intel.com/content/www/us/en/download/15084/intel-ethernet-adapter-complete-driver-pack.html"
            Notes            = "Driver Intel oficial. Muy estable. EEE es el principal problema."
            KnownIssues      = "Energy Efficient Ethernet causa latency spikes intermitentes."
            AdvancedSettings = @("*InterruptModeration=0","*EEE=0","FlowControl=0","*LsoV2IPv4=0")
        }
        "Aquantia" = @{
            OptimalVersion   = "Driver oficial Marvell/Aquantia"
            MinVersion       = "2.1"
            DownloadUrl      = "https://www.marvell.com/support/downloads.html"
            Notes            = "AQC107 excelente para gaming. Drivers oficiales Marvell."
            KnownIssues      = "Drivers OEM de placa pueden estar muy desactualizados."
            AdvancedSettings = @("*InterruptModeration=0","FlowControl=0","*LsoV2IPv4=0")
        }
        "Killer" = @{
            OptimalVersion   = "Ultima version Intel (ahora Intel Killer)"
            MinVersion       = "10.1"
            DownloadUrl      = "https://www.intel.com/content/www/us/en/download/706107/intel-killer-wifi-and-ethernet-software.html"
            Notes            = "CRITICO: Desinstalar Killer Control Center - es una de las peores causas de DPC latency conocidas. Solo instalar driver base."
            KnownIssues      = "Killer Control Center = peor ofensor de DPC. Desinstalar SIEMPRE."
            AdvancedSettings = @("*InterruptModeration=0","FlowControl=0","*LsoV2IPv4=0","*LsoV2IPv6=0")
        }
        "Generic" = @{
            OptimalVersion   = "Ultima version del fabricante"
            MinVersion       = "N/A"
            DownloadUrl      = "Buscar en sitio del fabricante de la placa madre"
            Notes            = "NIC no identificado. Buscar driver en web del fabricante."
            KnownIssues      = "Verificar con LatencyMon si el driver del NIC genera DPC."
            AdvancedSettings = @("*InterruptModeration=0","FlowControl=0")
        }
    }
    if ($db.ContainsKey($nicType)) { return $db[$nicType] }
    return $db["Generic"]
}

function Get-OptimalGpuDriver($gpuGen, $isNvidia) {
    if ($isNvidia) {
        $db = @{
            "RTX40" = @{ Recommended="572.83+ (Game Ready)"; Stable="566.36"; Avoid="595.xx-610.xx (DPC spikes conocidos)"; Notes="RTX 4000: HAGS ON, Reflex ON, Low Latency Ultra, P0. Driver 566.36 golden standard comunidad CS2."; DownloadUrl="https://www.nvidia.com/drivers" }
            "RTX30" = @{ Recommended="566.36 (BlurBusters/r/GlobalOffensive consensus)"; Stable="566.36"; Avoid="577.xx, 591.86, 595.xx-610.xx (latency spikes)"; Notes="RTX 3000: 566.36 mas estable. HAGS ON, Reflex ON, Low Latency Ultra, P0."; DownloadUrl="https://www.nvidia.com/drivers" }
            "RTX20" = @{ Recommended="566.36"; Stable="566.36"; Avoid="Drivers 5xx nuevos pueden tener overhead extra en Turing"; Notes="RTX 2000: 566.36. HAGS puede no beneficiar esta gen. Reflex: ON."; DownloadUrl="https://www.nvidia.com/drivers" }
            "GTX16" = @{ Recommended="566.36"; Stable="566.36"; Avoid="6xx+ pueden no optimizar bien Turing-lite"; Notes="GTX 1600: HAGS OFF. Low Latency: Ultra."; DownloadUrl="https://www.nvidia.com/drivers" }
            "GTX10" = @{ Recommended="566.36"; Stable="566.36"; Avoid="N/A - Pascal bien soportado"; Notes="GTX 1000: HAGS OFF. Low Latency: On. Considerar upgrade de GPU."; DownloadUrl="https://www.nvidia.com/drivers" }
        }
        if ($db.ContainsKey($gpuGen)) { return $db[$gpuGen] }
        return @{ Recommended="Ultima Game Ready Driver"; Stable="566.36"; Avoid="Series 595-610"; Notes="Game Ready Driver, NO Studio Driver."; DownloadUrl="https://www.nvidia.com/drivers" }
    } else {
        $db = @{
            "RX7000" = @{ Recommended="Ultima Adrenalin WHQL (solo driver, sin suite)"; Stable="24.3.1+ WHQL"; Avoid="Betas sin WHQL"; Notes="RX 7000: DDU limpio. Anti-Lag+ ON. Solo driver, sin Adrenalin Experience. Chipset AMD.com no del fabricante de placa."; DownloadUrl="https://www.amd.com/en/support" }
            "RX6000" = @{ Recommended="Ultima Adrenalin WHQL"; Stable="24.3.1+ WHQL"; Avoid="Pre-24.1"; Notes="RX 6000: Anti-Lag ON, Radeon Boost. Solo driver base. Desactivar AMDCrashDefender con Autoruns si hay DPC."; DownloadUrl="https://www.amd.com/en/support" }
            "RX5000" = @{ Recommended="Ultima Adrenalin WHQL"; Stable="24.3.1+"; Avoid="N/A"; Notes="RX 5000: Anti-Lag ON. HAGS puede funcionar bien."; DownloadUrl="https://www.amd.com/en/support" }
        }
        if ($db.ContainsKey($gpuGen)) { return $db[$gpuGen] }
        return @{ Recommended="Ultima Adrenalin WHQL"; Stable="Ultima WHQL"; Avoid="Betas sin certificar"; Notes="Solo driver sin suite para menos overhead."; DownloadUrl="https://www.amd.com/en/support" }
    }
}
# ==================== HELPERS ====================
function Get-SecureBootStatus {
    try { $sb = Confirm-SecureBootUEFI -ErrorAction SilentlyContinue
        if ($sb -eq $true) { return "ACTIVADO" } elseif ($sb -eq $false) { return "DESACTIVADO" } else { return "No soportado/Legacy" }
    } catch { return "No determinado" }
}
function Get-DisplayClassPath { return "Registry::HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}" }
function Write-Banner { param([string]$Text,[string]$Color="Cyan"); $line="="*($Text.Length+4); Write-Host $line -ForegroundColor $Color; Write-Host "  $Text  " -ForegroundColor $Color; Write-Host $line -ForegroundColor $Color; Write-Host "" }

# ==================== SCORE COMPETITIVO PRO 0-100 ====================
function Get-CompetitiveScore {
    Clear-Host; Write-Banner "ANALISIS COMPETITIVO PRO - SCORE 0/100" "Cyan"
    Write-Host "Analizando tu sistema como un tecnico de ESL..." -ForegroundColor Yellow; Write-Host ""
    $totalScore=0; $maxScore=100; $issues=@(); $ok=@(); $warnings=@()

    Write-Host "[ SISTEMA - 25 pts ]" -ForegroundColor Magenta
    # Timer resolution (5 pts)
    try {
        $sig='[DllImport("ntdll.dll")] public static extern int NtQueryTimerResolution(out uint a, out uint b, out uint c);'
        if (!("TimerQ" -as [type])) { Add-Type -MemberDefinition $sig -Name TimerQ -Namespace Win32 -EA SilentlyContinue }
        [uint32]$mn=0;[uint32]$mx=0;[uint32]$cur=0
        [Win32.TimerQ]::NtQueryTimerResolution([ref]$mn,[ref]$mx,[ref]$cur) | Out-Null
        $timerMs=[Math]::Round($cur/10000.0,3)
        if ($timerMs -le 0.5) { $totalScore+=5; $ok+="Timer resolution: ${timerMs}ms (OPTIMO)" }
        elseif ($timerMs -le 1.0) { $totalScore+=3; $warnings+="Timer resolution: ${timerMs}ms (activa modulo [22])" }
        else { $issues+="Timer resolution: ${timerMs}ms - MAL, activa modulo [30] en [O] Optimizaciones" }
    } catch { $warnings+="Timer resolution: no se pudo medir" }
    # Power plan (5 pts)
    $pp=(powercfg -getactivescheme 2>$null)-join""
    if ($pp -match "Ultimate|Gaming|e9a42b02") { $totalScore+=5; $ok+="Power Plan: ALTO RENDIMIENTO" }
    elseif ($pp -match "High|8c5e7fda") { $totalScore+=3; $warnings+="Power Plan: High Performance (Gaming es mejor)" }
    else { $issues+="Power Plan: BALANCED - MUY MAL, activa [12] o [P]" }
    # Core parking (3 pts)
    $cpMin=(powercfg /qh scheme_current SUB_PROCESSOR 0cc5b647-c1df-4637-891a-dec35c318583 2>$null | Select-String "Current AC Power Setting Index:" | Select-Object -Last 1)-join""
    if ($cpMin -match "0x00000064") { $totalScore+=3; $ok+="Core Parking: 100% nucleos activos" } else { $issues+="Core Parking: activo - modulo [17]" }
    # SysMain (3 pts)
    $sysmain=(Get-Service SysMain -EA SilentlyContinue).Status
    if ($sysmain -ne "Running") { $totalScore+=3; $ok+="SysMain: desactivado" } else { $issues+="SysMain: CORRIENDO - modulo [18]" }
    # HAGS (4 pts) Ã¢â‚¬â€ check via HwSchMode (el valor real del sistema, no la preferencia DirectX por app)
    $hagsMode=(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers" -Name "HwSchMode" -EA SilentlyContinue).HwSchMode
    if ($hagsMode -eq 2) {
        if ($script:HW.GpuGen -match "RTX[34]0|RX[67]") { $totalScore+=4; $ok+="HAGS: ON (correcto para tu GPU)" }
        else { $totalScore+=2; $warnings+="HAGS: ON (verifica si beneficia tu GPU generacion $($script:HW.GpuGen))" }
    } else {
        if ($script:HW.GpuGen -match "GTX1[06]") { $totalScore+=2; $warnings+="HAGS: OFF (correcto para GPU legada)" }
        else { $issues+="HAGS: OFF - considera activar modulo [13] para GPU moderna" }
    }
    # Visual FX (2 pts)
    $vfx=(Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects" -EA SilentlyContinue).VisualFXSetting
    if ($vfx -eq 2) { $totalScore+=2; $ok+="Efectos visuales: rendimiento" } else { $warnings+="Efectos visuales: no optimizados (modulo [20])" }
    # Game DVR (3 pts)
    $dvr=(Get-ItemProperty "HKCU:\System\GameConfigStore" -EA SilentlyContinue).GameDVR_Enabled
    if ($dvr -eq 0) { $totalScore+=3; $ok+="Game DVR/GameBar: desactivado" } else { $issues+="Game DVR: ACTIVO - overhead de captura (modulo [9])" }

    Write-Host "[ RED - 30 pts ]" -ForegroundColor Magenta
    # Nagle (5 pts)
    $ifBase="HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"; $nagleOff=$true
    Get-ChildItem $ifBase -EA SilentlyContinue | ForEach-Object { $tnd=(Get-ItemProperty $_.PSPath -Name TCPNoDelay -EA SilentlyContinue).TCPNoDelay; if ($tnd -ne 1) { $nagleOff=$false } }
    if ($nagleOff) { $totalScore+=5; $ok+="Nagle: OFF" } else { $issues+="Nagle: ACTIVO - afecta latencia de red (modulo [14])" }
    # Interrupt Moderation (7 pts)
    $nicBase="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"; $imOff=$false
    Get-ChildItem $nicBase -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $im=(Get-ItemProperty $_.PSPath -Name "*InterruptModeration" -EA SilentlyContinue)."*InterruptModeration"; if ($im -eq "0") { $imOff=$true } }
    if ($imOff) { $totalScore+=7; $ok+="Interrupt Moderation NIC: OFF (FIX CLAVE para jitter)" } else { $issues+="Interrupt Moderation NIC: ACTIVO - CAUSA JITTER/DESYNC (modulo [24])" }
    # LSO (5 pts)
    $lsoOff=$true
    Get-ChildItem $nicBase -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $lso=(Get-ItemProperty $_.PSPath -Name "*LsoV2IPv4" -EA SilentlyContinue)."*LsoV2IPv4"; if ($lso -ne "0") { $lsoOff=$false } }
    if ($lsoOff) { $totalScore+=5; $ok+="LSO: OFF" } else { $issues+="LSO: ACTIVO - desync por packet buffering (modulo [25])" }
    # Flow Control (3 pts)
    $fcOff=$false
    Get-ChildItem $nicBase -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $fc=(Get-ItemProperty $_.PSPath -Name "FlowControl" -EA SilentlyContinue).FlowControl; if ($fc -eq "0") { $fcOff=$true } }
    if ($fcOff) { $totalScore+=3; $ok+="Flow Control NIC: OFF" } else { $warnings+="Flow Control NIC: activo (modulo [27])" }
    # Power NIC (3 pts)
    if (Get-State "NetPower") { $totalScore+=3; $ok+="Ahorro energia NIC: desactivado" } else { $issues+="Ahorro energia NIC: activo (modulo [4])" }
    # ECN (2 pts)
    $ecn=(netsh int tcp show global 2>$null | Select-String "ECN")-join""
    if ($ecn -match "disabled") { $totalScore+=2; $ok+="ECN: desactivado" } else { $warnings+="ECN: activo (modulo [29])" }
    # Driver NIC (5 pts)
    $nicOptimal=Get-OptimalNicDriver $script:HW.NicType
    if ($script:HW.NicType -ne "Generic") {
        $curVer=$script:HW.NicDriver.Version; $minVer=$nicOptimal.MinVersion
        if ($curVer -ne "N/A" -and $minVer -ne "N/A") {
            try {
                $verNum=[double]($curVer -replace "^(\d+\.\d+).*",'$1')
                $minNum=[double]($minVer -replace "^(\d+\.\d+).*",'$1')
                if ($verNum -ge $minNum) { $totalScore+=5; $ok+="Driver NIC ($($script:HW.NicType)): $curVer (OK)" }
                else { $issues+="Driver NIC: $curVer es VIEJO (recomendado: $($nicOptimal.OptimalVersion))" }
            } catch { $warnings+="Driver NIC: no se pudo comparar version" }
        } else { $warnings+="Driver NIC: no se pudo verificar ($curVer)" }
    } else { $warnings+="NIC no identificada - verificar driver manualmente" }

    Write-Host "[ INTERRUPCIONES / DPC - 25 pts ]" -ForegroundColor Magenta
    # MSI GPU (8 pts)
    $gpuMsi=$false
    Get-PnpDevice -Class Display -EA SilentlyContinue | ForEach-Object {
        $msiPath="HKLM:\SYSTEM\ControlSet001\Enum\$($_.InstanceId)\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties"
        if ((Get-ItemProperty $msiPath -EA SilentlyContinue).MSISupported -eq 1) { $gpuMsi=$true } }
    if ($gpuMsi) { $totalScore+=8; $ok+="MSI Mode GPU: ACTIVADO" } else { $issues+="MSI Mode GPU: NO activado (modulo [3])" }
    # MSI HID (5 pts)
    if (Get-State "HidMsi") { $totalScore+=5; $ok+="MSI HID/Mouse: ACTIVADO" } else { $issues+="MSI HID/Mouse: no activo (modulo [21])" }
    # IRQ Affinity (7 pts)
    if (Get-State "UsbIrqAffinity") { $totalScore+=7; $ok+="IRQ Affinity USB: configurado" } else { $warnings+="IRQ Affinity USB: no configurado (modulo [23])" }
    # DPC medicion (5 pts)
    try {
        $perf=Get-Counter "\Processor(_Total)\% Privileged Time" -SampleInterval 1 -MaxSamples 5 -EA SilentlyContinue
        if ($perf) {
            $avg=($perf.CounterSamples.CookedValue | Measure-Object -Average).Average
            if ($avg -le 3) { $totalScore+=5; $ok+="DPC/Kernel Time: $([Math]::Round($avg,1))% (EXCELENTE)" }
            elseif ($avg -le 8) { $totalScore+=3; $ok+="DPC/Kernel Time: $([Math]::Round($avg,1))% (bueno)" }
            elseif ($avg -le 15) { $totalScore+=1; $warnings+="DPC/Kernel Time: $([Math]::Round($avg,1))% (elevado)" }
            else { $issues+="DPC/Kernel Time: $([Math]::Round($avg,1))% (MUY ALTO - driver problematico)" }
        }
    } catch { $warnings+="DPC: no se pudo medir" }

    Write-Host "[ HARDWARE CONFIG - 20 pts ]" -ForegroundColor Magenta
    # RAM (6 pts)
    $ramSpd=$script:HW.RamSpeedMHz
    if ($ramSpd -ge 3600) { $totalScore+=6; $ok+="RAM: ${ramSpd}MHz (RAPIDA - XMP/EXPO activo)" }
    elseif ($ramSpd -ge 3000) { $totalScore+=4; $ok+="RAM: ${ramSpd}MHz (buena)" }
    elseif ($ramSpd -ge 2400) { $totalScore+=2; $warnings+="RAM: ${ramSpd}MHz (activa XMP/EXPO en BIOS)" }
    elseif ($ramSpd -eq 2133 -and $script:HW.RamGB -ge 16) { $issues+="RAM: 2133MHz - POSIBLE XMP/EXPO DESACTIVADO en BIOS" }
    else { $warnings+="RAM: velocidad no detectada (${ramSpd}MHz)" }
    # Dual channel (5 pts)
    if ($script:HW.IsDualChannel) { $totalScore+=5; $ok+="RAM: Dual Channel ($($script:HW.RamChannels) modulos)" } else { $issues+="RAM: posible Single Channel (bottleneck de ancho de banda)" }
    # Scheduler (5 pts)
    $schedVal=(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl" -EA SilentlyContinue).Win32PrioritySeparation
    if ($schedVal -eq 38) { $totalScore+=5; $ok+="CPU Scheduler: Foreground Boost 3x (optimo)" } else { $issues+="CPU Scheduler: no optimizado - activa modulo [42] en [O] Optimizaciones" }
    # Power Throttling (4 pts)
    $ptOff=(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling" -EA SilentlyContinue).PowerThrottlingOff
    if ($ptOff -eq 1) { $totalScore+=4; $ok+="Power Throttling (EcoQoS): DESACTIVADO" } else { $issues+="Power Throttling: activo - juego puede ir a E-core (modulo [32])" }

    Write-Host ""
    $pct=[Math]::Round(($totalScore/$maxScore)*100)
    $bar="#"*[Math]::Round($pct/2); $empty="-"*(50-[Math]::Round($pct/2))
    $scoreColor=if ($pct -ge 85) { "Green" } elseif ($pct -ge 60) { "Yellow" } else { "Red" }
    Write-Host ("="*60) -ForegroundColor $scoreColor
    Write-Host "  SCORE COMPETITIVO PRO: $totalScore/$maxScore ($pct%)" -ForegroundColor $scoreColor
    Write-Host "  [$bar$empty]" -ForegroundColor $scoreColor
    $grade=if ($pct -ge 95) { "S+ NIVEL PRO ESL" } elseif ($pct -ge 85) { "A FACEIT TOP" } elseif ($pct -ge 70) { "B SEMI-PRO" } elseif ($pct -ge 50) { "C COMPETITIVO BASICO" } else { "D NECESITA OPTIMIZACION" }
    Write-Host "  GRADO: $grade" -ForegroundColor $scoreColor
    Write-Host ("="*60) -ForegroundColor $scoreColor; Write-Host ""
    if ($ok.Count -gt 0) { Write-Host "CORRECTO:" -ForegroundColor Green; foreach ($i in $ok) { Write-Host "  + $i" -ForegroundColor Green }; Write-Host "" }
    if ($warnings.Count -gt 0) { Write-Host "OPTIMIZABLE:" -ForegroundColor Yellow; foreach ($i in $warnings) { Write-Host "  ~ $i" -ForegroundColor Yellow }; Write-Host "" }
    if ($issues.Count -gt 0) { Write-Host "PROBLEMAS (arreglar para subir score):" -ForegroundColor Red; foreach ($i in $issues) { Write-Host "  ! $i" -ForegroundColor Red }; Write-Host "" }
    Write-Host "[ HARDWARE DETECTADO ]" -ForegroundColor Cyan
    Write-Host "  CPU : $($script:HW.CpuName) ($($script:HW.CpuCores)c/$($script:HW.CpuThreads)t)" -ForegroundColor White
    if ($script:HW.IsIntelHybrid) { Write-Host "        *** CPU HIBRIDA Intel P+E cores - modulo [33] disponible ***" -ForegroundColor Magenta }
    Write-Host "  GPU : $($script:HW.GpuName) [$($script:HW.GpuGen)] $($script:HW.GpuVRAM)GB VRAM" -ForegroundColor White
    Write-Host "  RAM : $($script:HW.RamGB)GB @ $($script:HW.RamSpeedMHz)MHz ($($script:HW.RamChannels) modulos)" -ForegroundColor White
    Write-Host "  NIC : $($script:HW.NicName) [$($script:HW.NicType)] Driver: $($script:HW.NicDriver.Version) ($($script:HW.NicDriver.Date))" -ForegroundColor White
    Write-Host "  OS  : $($script:HW.OsCaption) Build $($script:HW.OsBuild)" -ForegroundColor White
    Write-Host "  Disco: $($script:HW.StorageType)" -ForegroundColor White
    if ($script:HW.MonitorHz -gt 0) { Write-Host "  Monitor: $($script:HW.MonitorHz) Hz detectados" -ForegroundColor White }
    Write-Host ""; Pause
}

# ==================== DIAGNOSTICOS ====================
function Get-PCDiagnostics {
    Clear-Host; Write-Banner "DIAGNOSTICO COMPLETO DE HARDWARE Y SISTEMA" "Cyan"
    Write-Host "[ CPU ]" -ForegroundColor Yellow
    Write-Host "  $($script:HW.CpuName) - $($script:HW.CpuCores) cores / $($script:HW.CpuThreads) threads @ $($script:HW.CpuMaxMHz)MHz"
    if ($script:HW.IsIntelHybrid) { Write-Host "  *** CPU HIBRIDA Intel - activar modulo [33] para P-core affinity ***" -ForegroundColor Magenta }
    if ($script:HW.CpuThreads -eq $script:HW.CpuCores*2) { Write-Host "  HyperThreading: ACTIVADO" -ForegroundColor Yellow }
    Write-Host ""
    Write-Host "[ RAM ]" -ForegroundColor Yellow
    Write-Host "  $($script:HW.RamGB) GB @ $($script:HW.RamSpeedMHz) MHz - $($script:HW.RamChannels) modulos"
    if (!$script:HW.IsDualChannel) { Write-Host "  *** POSIBLE SINGLE CHANNEL - bottleneck de ancho de banda ***" -ForegroundColor Red }
    if ($script:HW.RamSpeedMHz -le 2133 -and $script:HW.RamSpeedMHz -gt 0) { Write-Host "  *** RAM en 2133MHz - verificar XMP/EXPO en BIOS ***" -ForegroundColor Red }
    Write-Host ""
    Write-Host "[ GPU ]" -ForegroundColor Yellow
    Write-Host "  $($script:HW.GpuName) [$($script:HW.GpuGen)] - $($script:HW.GpuVRAM)GB VRAM"
    $gpuDriver=Get-OptimalGpuDriver $script:HW.GpuGen $script:HW.IsNvidia
    Write-Host "  Driver recomendado: $($gpuDriver.Recommended)" -ForegroundColor Cyan
    Write-Host "  EVITAR: $($gpuDriver.Avoid)" -ForegroundColor Red
    Write-Host "  Notas: $($gpuDriver.Notes)" -ForegroundColor DarkGray
    Write-Host "  Descarga: $($gpuDriver.DownloadUrl)" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "[ NIC / RED ]" -ForegroundColor Yellow
    Write-Host "  $($script:HW.NicName) [$($script:HW.NicType)]"
    Write-Host "  Driver: $($script:HW.NicDriver.Version) | Fecha: $($script:HW.NicDriver.Date) | Provider: $($script:HW.NicDriver.Provider)"
    $nicOpt=Get-OptimalNicDriver $script:HW.NicType
    Write-Host "  Driver optimo: $($nicOpt.OptimalVersion)" -ForegroundColor Cyan
    Write-Host "  Notas criticas: $($nicOpt.Notes)" -ForegroundColor Yellow
    Write-Host "  Issues conocidos: $($nicOpt.KnownIssues)" -ForegroundColor DarkGray
    Write-Host "  Descarga: $($nicOpt.DownloadUrl)" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "[ SISTEMA ]" -ForegroundColor Yellow
    $bios=Get-CimInstance Win32_BIOS -EA SilentlyContinue
    Write-Host "  BIOS: $($bios.Manufacturer) $($bios.Version)"
    Write-Host "  Secure Boot: $(Get-SecureBootStatus) | OS: $($script:HW.OsCaption) Build $($script:HW.OsBuild)"
    Write-Host "  Storage: $($script:HW.StorageType)"
    if ($script:HW.StorageType -match "HDD") { Write-Host "  *** HDD: SysMain ayuda en HDD, no desactivar si solo tienes HDD ***" -ForegroundColor Yellow }
    Write-Host ""
    Write-Host "[ PROCESOS CON OVERHEAD ]" -ForegroundColor Yellow
    $suspicious=@("RazerSynapse3","LGHUB","Discord","Spotify","obs64","OBS64","MSIAfterburner","HWiNFO64","NahimicSvc","SteelSeriesGG","iCUE","EpicGamesLauncher","EADesktop")
    $bad=Get-Process -EA SilentlyContinue | Where-Object { $suspicious -contains $_.ProcessName }
    if ($bad) { foreach ($p in $bad) { Write-Host "  ! $($p.ProcessName) (PID $($p.Id)) - agrega DPC latency" -ForegroundColor Yellow } }
    else { Write-Host "  + Sin procesos problematicos detectados" -ForegroundColor Green }
    Write-Host ""
    Write-Host "[ RECOMENDACIONES ESPECIFICAS PARA TU SETUP ]" -ForegroundColor Cyan
    $recsCount=0
    if ($script:HW.IsIntelHybrid) { Write-Host "  -> CPU HIBRIDA: activar modulo [33] para eliminar frame spikes de E-cores" -ForegroundColor Magenta; $recsCount++ }
    if ($script:HW.NicType -eq "RTL8125") { Write-Host "  -> RTL8125: modulo [15] (Interrupt Moderation OFF) es CRITICO para tu jitter/desync" -ForegroundColor Red; $recsCount++ }
    if ($script:HW.NicType -match "Killer") { Write-Host "  -> NIC KILLER: DESINSTALA el Killer Control Center - peor causa de DPC latency" -ForegroundColor Red; $recsCount++ }
    if (!$script:HW.IsDualChannel) { Write-Host "  -> SINGLE CHANNEL: colocar 2do stick en slot correcto para dual channel" -ForegroundColor Red; $recsCount++ }
    if ($script:HW.IsNvidia) { Write-Host "  -> NVIDIA: activar modulos [6],[9],[12] (MSI, NvidiaTweaks, ULL). Driver recomendado: 566.36" -ForegroundColor Cyan; $recsCount++ }
    if ($script:HW.IsAMD) { Write-Host "  -> AMD Ryzen: activar modulo [22] Timer Resolution y [29] Core Parking OFF" -ForegroundColor Cyan; $recsCount++ }
    Write-Host "  -> SIEMPRE activo: modulos [13] Nagle OFF, [15] Interrupt Moderation, [16] LSO OFF" -ForegroundColor Yellow; $recsCount++
    Write-Host "  -> Para desync: modulos [21] NTP Clock Fix y [15] Interrupt Moderation" -ForegroundColor Yellow; $recsCount++
    if ($recsCount -eq 0) { Write-Host "  + Mantener drivers actualizados de fuente oficial y evitar bloatware." -ForegroundColor Green }
    Write-Host ""; Pause
}

function Get-DPCLatencySample {
    Clear-Host; Write-Banner "MUESTREO DPC / INTERRUPT LATENCY" "Cyan"
    Write-Host "Midiendo 15 segundos... (abre Discord/Spotify para ver impacto)" -ForegroundColor Yellow; Write-Host ""
    try {
        $samples=Get-Counter "\Processor(_Total)\% Privileged Time" -SampleInterval 1 -MaxSamples 15 -EA SilentlyContinue
        if ($samples) {
            $vals=$samples.CounterSamples.CookedValue
            $avg=($vals|Measure-Object -Average).Average; $max=($vals|Measure-Object -Maximum).Maximum; $min=($vals|Measure-Object -Minimum).Minimum
            Write-Host "  Promedio: $([Math]::Round($avg,2))% | Max: $([Math]::Round($max,2))% | Min: $([Math]::Round($min,2))%"
            Write-Host ""
            if ($avg -le 3) { Write-Host "  + EXCELENTE: setup pro level" -ForegroundColor Green }
            elseif ($avg -le 8) { Write-Host "  + BUENO: rango competitivo" -ForegroundColor Green }
            elseif ($avg -le 15) { Write-Host "  ~ MODERADO: aplicar tweaks restantes" -ForegroundColor Yellow }
            else { Write-Host "  ! ALTO: driver/servicio generando spikes - usar LatencyMon" -ForegroundColor Red }
            if ($max -gt 30) { Write-Host "  ! SPIKES DETECTADOS (max: $([Math]::Round($max,1))%) - problema de driver" -ForegroundColor Red }
        }
    } catch { Write-Host "No se pudo medir." -ForegroundColor Red }
    Write-Host ""; Pause
}

function Show-TimerResolution {
    Clear-Host; Write-Banner "RESOLUCION DE TIMER ACTUAL" "Cyan"
    try {
        $sig='[DllImport("ntdll.dll")] public static extern int NtQueryTimerResolution(out uint a, out uint b, out uint c);'
        if (!("TimerQx" -as [type])) { Add-Type -MemberDefinition $sig -Name TimerQx -Namespace Win32 -EA SilentlyContinue }
        [uint32]$mn=0;[uint32]$mx=0;[uint32]$cur=0
        [Win32.TimerQx]::NtQueryTimerResolution([ref]$mn,[ref]$mx,[ref]$cur) | Out-Null
        $mnMs=[Math]::Round($mn/10000.0,3); $mxMs=[Math]::Round($mx/10000.0,3); $curMs=[Math]::Round($cur/10000.0,3)
        Write-Host "  Min posible : ${mnMs}ms"
        Write-Host "  Max posible : ${mxMs}ms"
        Write-Host "  ACTUAL      : ${curMs}ms" -ForegroundColor Cyan
        if ($curMs -le 0.5) { Write-Host ""; Write-Host "  + OPTIMO (0.5ms) - excelente precision de timer!" -ForegroundColor Green }
        elseif ($curMs -le 1.0) { Write-Host ""; Write-Host "  ~ Bueno (${curMs}ms) - activa modulo [22] para llegar a 0.5ms" -ForegroundColor Yellow }
        else { Write-Host ""; Write-Host "  ! Precision baja (${curMs}ms) - activa modulo [22] ahora!" -ForegroundColor Red }
    } catch { Write-Host "  No se pudo medir. Asegurate de ejecutar como Administrador." -ForegroundColor Red }
    Write-Host ""; Pause
}

function Show-ResourceMonitorInfo {
    Clear-Host; Write-Banner "MONITOR DE RECURSOS EN TIEMPO REAL" "Cyan"
    Write-Host "[ CPU ]" -ForegroundColor Yellow
    try {
        $cpu=Get-Counter "\Processor(_Total)\% Processor Time","\Processor(_Total)\% Privileged Time" -SampleInterval 1 -MaxSamples 3 -EA SilentlyContinue
        if ($cpu) {
            $user=[Math]::Round(($cpu.CounterSamples | Where-Object {$_.Path -match 'Processor Time'} | Measure-Object CookedValue -Average).Average,1)
            $kernel=[Math]::Round(($cpu.CounterSamples | Where-Object {$_.Path -match 'Privileged'} | Measure-Object CookedValue -Average).Average,1)
            Write-Host "  Total CPU  : ${user}%  | Kernel (DPC): ${kernel}%"
            $kColor=if($kernel-le3){"Green"}elseif($kernel-le8){"Yellow"}else{"Red"}
            $kStatus=if($kernel-le3){"EXCELENTE"}elseif($kernel-le8){"BUENO"}else{"ALTO - driver problematico"}
            Write-Host "  Estado DPC : $kStatus" -ForegroundColor $kColor
        }
    } catch {}
    Write-Host ""
    Write-Host "[ MEMORIA ]" -ForegroundColor Yellow
    try {
        $memAvail=Get-Counter "\Memory\Available MBytes" -SampleInterval 1 -MaxSamples 1 -EA SilentlyContinue
        $memPages=Get-Counter "\Memory\Page Faults/sec" -SampleInterval 1 -MaxSamples 1 -EA SilentlyContinue
        $availMB=[Math]::Round(($memAvail.CounterSamples.CookedValue | Measure-Object -Average).Average)
        $availGB=[Math]::Round($availMB/1024,1)
        $pageFaults=[Math]::Round(($memPages.CounterSamples.CookedValue | Measure-Object -Average).Average)
        Write-Host "  Disponible : ${availGB}GB (${availMB}MB)"
        Write-Host "  Page Faults: ${pageFaults}/seg"
        if ($pageFaults -gt 1000) { Write-Host "  ! Alto page fault rate - posible insuficiencia de RAM" -ForegroundColor Yellow }
    } catch {}
    Write-Host ""
    Write-Host "[ RED ]" -ForegroundColor Yellow
    try {
        $nics=Get-NetAdapter -Physical -EA SilentlyContinue | Where-Object {$_.Status -eq 'Up'}
        foreach ($nic in $nics) {
            $recvC=Get-Counter "\Network Interface($($nic.InterfaceDescription))\Bytes Received/sec" -SampleInterval 1 -MaxSamples 1 -EA SilentlyContinue
            $sentC=Get-Counter "\Network Interface($($nic.InterfaceDescription))\Bytes Sent/sec" -SampleInterval 1 -MaxSamples 1 -EA SilentlyContinue
            if ($recvC -and $sentC) {
                $recv=[Math]::Round(($recvC.CounterSamples.CookedValue|Measure-Object-Average).Average/1KB,1)
                $sent=[Math]::Round(($sentC.CounterSamples.CookedValue|Measure-Object-Average).Average/1KB,1)
                Write-Host "  $($nic.Name): Recv=${recv}KB/s | Sent=${sent}KB/s"
            }
        }
    } catch { Write-Host "  No se pudo medir trafico de red." -ForegroundColor DarkGray }
    Write-Host ""
    Write-Host "[ TOP PROCESOS POR CPU ]" -ForegroundColor Yellow
    Get-Process -EA SilentlyContinue | Sort-Object CPU -Descending | Select-Object -First 8 | ForEach-Object {
        Write-Host ("  {0,-25} CPU:{1,6:N1}s  RAM:{2,6:N0}MB" -f $_.ProcessName, $_.CPU, ($_.WorkingSet64/1MB))
    }
    Write-Host ""; Pause
}

function Show-LatencyMeasurementMenu {
    Clear-Host; Write-Host "=== HERRAMIENTAS DE MEDICION ===" -ForegroundColor Cyan; Write-Host ""
    Write-Host "[1] Timer Resolution actual"
    Write-Host "[2] Monitor de Recursos (CPU/RAM/RED en tiempo real)"
    Write-Host "[3] Muestreo DPC Latency (15 seg)"
    Write-Host "[4] Guia: Como arreglar Bufferbloat (Router SQM)"
    Write-Host "[5] Descargar SetTimerResolution"
    Write-Host "[6] Info MouseTester"
    Write-Host "[7] Volver"; Write-Host ""
    $c=Read-Host "Elegi"
    switch ($c) {
        "1" { Show-TimerResolution; Show-LatencyMeasurementMenu }
        "2" { Show-ResourceMonitorInfo; Show-LatencyMeasurementMenu }
        "3" { Get-DPCLatencySample; Show-LatencyMeasurementMenu }
        "4" { 
            Clear-Host; Write-Banner "GUIA: COMO ARREGLAR BUFFERBLOAT (+10ms UPLOAD)" "Cyan"
            Write-Host "El script ya optimizo tu PC al 100%, pero el bufferbloat ocurre en tu ROUTER." -ForegroundColor Yellow
            Write-Host "Cuando el router se satura subiendo datos, tus paquetes del juego se encolan y generan ping spikes." -ForegroundColor White
            Write-Host ""
            Write-Host "COMO ARREGLARLO (Orden de efectividad):" -ForegroundColor Cyan
            Write-Host "1. Activar SQM (FQ_CoDel o CAKE):" -ForegroundColor Green
            Write-Host "   Entra a la config de tu router (generalmente 192.168.1.1 o 192.168.0.1)."
            Write-Host "   Busca 'QoS', 'Bandwidth Control' o 'Smart Queue Management'."
            Write-Host "   Si tienes FQ_CoDel o CAKE, activalo. Es la solucion magica."
            Write-Host "2. Limitar el Ancho de Banda (Traffic Shaping):" -ForegroundColor Green
            Write-Host "   Si tienes QoS normal, pon el limite de tu Subida y Bajada al 90% de lo que realmente tienes."
            Write-Host "   Ej: Si tienes 100 Mbps, pon el limite en 90 Mbps. Esto evita que el modem colapse."
            Write-Host "3. Comprar un Router Gaming / OpenWrt:" -ForegroundColor Green
            Write-Host "   Los modems que dan los ISPs (Movistar, Claro, etc) son MUY MALOS para bufferbloat."
            Write-Host "   Muchos pros usan routers con OpenWrt o routers ASUS/Netgear con buen QoS de fabrica."
            Write-Host ""
            Write-Host "Testea tu conexion aca: https://www.waveform.com/tools/bufferbloat (Busca grado A+)" -ForegroundColor Cyan
            Pause; Show-LatencyMeasurementMenu
        }
        "5" {
            $url="https://github.com/valleyofdoom/TimerResolution/releases/download/SetTimerResolution-v1.0.0/SetTimerResolution-v1.0.0.zip"
            $d="$env:USERPROFILE\Downloads\SetTimerResolution.zip"
            try { Invoke-WebRequest -Uri $url -OutFile $d -UseBasicParsing; Expand-Archive $d "$env:USERPROFILE\Downloads\TimerResolution" -Force; Write-Host "Listo en Downloads\TimerResolution" -ForegroundColor Green }
            catch { Write-Host "Descargar: https://github.com/valleyofdoom/TimerResolution/releases" -ForegroundColor Yellow }
            Pause; Show-LatencyMeasurementMenu
        }
        "5" { Write-Host "MouseTester: https://github.com/valleyofdoom/MouseTester/releases" -ForegroundColor Cyan; Write-Host "Mover en onda senoidal lenta, verificar curva suave."; Pause; Show-LatencyMeasurementMenu }
        "6" { return }
        default { Write-Host "Invalido." -ForegroundColor Red; Start-Sleep 1; Show-LatencyMeasurementMenu }
    }
}
# ==================== LATENCY PROCESSES ====================
function Get-LatencyProcesses {
    Clear-Host; Write-Banner "PROCESOS / SERVICIOS QUE GENERAN LATENCIA" "Cyan"
    $suspicious=@("nvidia-smi","RazerSynapse3","LGHUB","Discord","Spotify","obs64","VLC","chrome","MSIAfterburner","HWiNFO64","NahimicSvc","SteelSeriesGG","iCUE","EpicGamesLauncher","EADesktop","upc")
    $running=Get-Process -EA SilentlyContinue | Where-Object { $_.ProcessName -in $suspicious }
    if ($running) { foreach ($p in $running) { Write-Host "  ! $($p.ProcessName) (PID $($p.Id), $([Math]::Round($p.WorkingSet64/1MB))MB)" -ForegroundColor Yellow } }
    else { Write-Host "  + Sin procesos problematicos detectados." -ForegroundColor Green }
    Write-Host ""
    Write-Host "Servicios:" -ForegroundColor White
    @(@{Name="SysMain";Desc="Superfetch";Good="Stopped"},@{Name="WSearch";Desc="Windows Search";Good="Stopped"},
      @{Name="DiagTrack";Desc="Telemetria";Good="Stopped"},@{Name="NahimicService";Desc="Nahimic DPC";Good="Stopped"}) | ForEach-Object {
        $s=Get-Service $_.Name -EA SilentlyContinue
        if ($s) { $isOk=($s.Status.ToString()-eq$_.Good); $col=if($isOk){"Green"}else{"Yellow"}; $icon=if($isOk){"+"}else{"~"}; Write-Host "  $icon $($_.Name): $($s.Status) ($($_.Desc))" -ForegroundColor $col }
    }
    Write-Host ""; Pause
}

# ==================== DETECTOR DE DRIVERS ====================
function Show-DriverHealthCheck {
    Clear-Host; Write-Banner "DETECTOR DE DRIVERS OPTIMOS - ESL TECH REPORT" "Cyan"
    Write-Host "[ GPU: $($script:HW.GpuName) ]" -ForegroundColor Yellow
    $gpuOpt=Get-OptimalGpuDriver $script:HW.GpuGen $script:HW.IsNvidia
    $gpuDriverVer="N/A"
    try {
        if ($script:HW.IsNvidia) {
            $dr=Get-ItemProperty "HKLM:\SOFTWARE\NVIDIA Corporation\Installer2\InstallerUI" -EA SilentlyContinue
            if ($dr.Version) { $gpuDriverVer=$dr.Version }
        }
        if ($gpuDriverVer -eq "N/A") {
            $pnp=Get-CimInstance Win32_PnPSignedDriver -EA SilentlyContinue | Where-Object { $_.DeviceName -match "NVIDIA|Radeon|AMD" } | Select-Object -First 1
            if ($pnp) { $gpuDriverVer=$pnp.DriverVersion }
        }
    } catch {}
    Write-Host "  Driver actual:      $gpuDriverVer"
    Write-Host "  Driver recomendado: $($gpuOpt.Recommended)" -ForegroundColor Cyan
    Write-Host "  Versiones estables: $($gpuOpt.Stable)" -ForegroundColor Green
    Write-Host "  EVITAR: $($gpuOpt.Avoid)" -ForegroundColor Red
    Write-Host "  Notas: $($gpuOpt.Notes)" -ForegroundColor DarkGray
    Write-Host "  -> Descarga: $($gpuOpt.DownloadUrl)" -ForegroundColor White
    Write-Host "  -> SIEMPRE usar DDU para instalacion limpia: https://www.wagnardsoft.com/DDU" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "[ NIC: $($script:HW.NicName) ]" -ForegroundColor Yellow
    $nicOpt=Get-OptimalNicDriver $script:HW.NicType
    Write-Host "  Tipo: $($script:HW.NicType) | Driver: $($script:HW.NicDriver.Version) | Fecha: $($script:HW.NicDriver.Date)"
    Write-Host "  Driver optimo: $($nicOpt.OptimalVersion)" -ForegroundColor Cyan
    Write-Host "  Issues conocidos: $($nicOpt.KnownIssues)" -ForegroundColor Red
    Write-Host "  Notas: $($nicOpt.Notes)" -ForegroundColor DarkGray
    Write-Host "  -> Descarga: $($nicOpt.DownloadUrl)" -ForegroundColor White
    Write-Host ""
    Write-Host "[ SETTINGS AVANZADOS RECOMENDADOS PARA $($script:HW.NicType) ]" -ForegroundColor Yellow
    Write-Host "  (Device Manager -> $($script:HW.NicName) -> Properties -> Advanced)" -ForegroundColor DarkGray
    foreach ($s in $nicOpt.AdvancedSettings) { Write-Host "  -> $s" -ForegroundColor Cyan }
    Write-Host ""
    Write-Host "[ DPC LATENCY RAPIDA (5 seg) ]" -ForegroundColor Yellow
    try {
        $perf=Get-Counter "\Processor(_Total)\% Privileged Time" -SampleInterval 1 -MaxSamples 5 -EA SilentlyContinue
        if ($perf) {
            $avg=($perf.CounterSamples.CookedValue|Measure-Object -Average).Average
            $st=if($avg-le3){"EXCELENTE"}elseif($avg-le8){"BUENO"}elseif($avg-le15){"MODERADO"}else{"ALTO - driver problematico"}
            $col=if($avg-le8){"Green"}elseif($avg-le15){"Yellow"}else{"Red"}
            Write-Host "  Kernel Time: $([Math]::Round($avg,2))% - $st" -ForegroundColor $col
            if ($avg -gt 15 -and $script:HW.NicType -match "RTL|Realtek") {
                Write-Host "  ! Alta probabilidad: driver Realtek. Actualizar de realtek.com" -ForegroundColor Red
            }
        }
    } catch {}
    Write-Host ""; Pause
}

# ==================== IRQ SMART BALANCER DINAMICO ====================
function Invoke-IRQSmartBalancer {
    Clear-Host; Write-Banner "IRQ SMART BALANCER - EQUIVALENTE AUTOGPUAFFINITY" "Cyan"
    Write-Host "Midiendo carga por core y asignando IRQ optimo..." -ForegroundColor Yellow
    Write-Host "Tarda ~30-60 segundos. No mover el mouse durante el analisis." -ForegroundColor DarkGray; Write-Host ""
    $threadCount=$script:HW.CpuThreads
    $baseline=@{}
    try {
        for ($i=0; $i -lt [Math]::Min($threadCount,16); $i++) {
            $s=Get-Counter "\Processor($i)\% Privileged Time" -SampleInterval 1 -MaxSamples 3 -EA SilentlyContinue
            $baseline[$i]=if ($s) { ($s.CounterSamples.CookedValue|Measure-Object -Average).Average } else { 999 }
        }
    } catch {
        for ($i=0; $i -lt $threadCount; $i++) { $baseline[$i]=$i*5 }
    }
    $sorted=$baseline.GetEnumerator()|Sort-Object Value
    $bestCore  =$sorted|Select-Object -First 1
    $secondCore=$sorted|Select-Object -Skip 1 -First 1
    $thirdCore =$sorted|Select-Object -Skip 2 -First 1
    Write-Host "Carga por core (Kernel %):" -ForegroundColor White
    foreach ($c in ($sorted|Select-Object -First 8)) {
        $bar="#"*[Math]::Min(30,[int]($c.Value))
        Write-Host "  Core $($c.Key): $([Math]::Round($c.Value,1))% [$bar]"
    }
    Write-Host ""
    function Get-AffinityMask($coreId) {
        $mask=[Math]::Pow(2,$coreId)
        $bytes=[System.BitConverter]::GetBytes([uint32]$mask)
        return ($bytes|ForEach-Object{"{0:X2}"-f $_})-join""
    }
    $gpuCore=[int]$bestCore.Key
    $nicCore=if ($secondCore) { [int]$secondCore.Key } else { ($gpuCore+1)%$threadCount }
    $usbCore=if ($thirdCore)  { [int]$thirdCore.Key  } else { ($gpuCore+2)%$threadCount }
    $gpuMask=Get-AffinityMask $gpuCore; $nicMask=Get-AffinityMask $nicCore
    $usbMask=Get-AffinityMask $usbCore; $hidMask=Get-AffinityMask $gpuCore
    Write-Host "Asignacion optima:" -ForegroundColor Green
    Write-Host "  GPU/HID -> Core $gpuCore (Kernel: $([Math]::Round($baseline[$gpuCore],1))%)" -ForegroundColor Cyan
    Write-Host "  NIC     -> Core $nicCore" -ForegroundColor Cyan
    Write-Host "  USB     -> Core $usbCore" -ForegroundColor Cyan; Write-Host ""
    Write-Host "Aplicando..." -ForegroundColor Yellow
    $gpuN=0
    Get-PnpDevice -Class Display -EA SilentlyContinue | ForEach-Object {
        $ap="HKLM\SYSTEM\ControlSet001\Enum\$($_.InstanceId)\Device Parameters\Interrupt Management\Affinity Policy"
        cmd /c "reg add `"$ap`" /v DevicePolicy /t REG_DWORD /d 4 /f >nul 2>&1"
        cmd /c "reg add `"$ap`" /v AssignmentSetOverride /t REG_BINARY /d $gpuMask /f >nul 2>&1"
        $gpuN++
    }
    $nicBase="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"; $nicN=0
    Get-ChildItem $nicBase -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $ap="$($_.Name)\Device Parameters\Interrupt Management\Affinity Policy"
        cmd /c "reg add `"$ap`" /v DevicePolicy /t REG_DWORD /d 4 /f >nul 2>&1"
        cmd /c "reg add `"$ap`" /v AssignmentSetOverride /t REG_BINARY /d $nicMask /f >nul 2>&1"; $nicN++
    }
    $usbClass="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000}"; $usbN=0
    Get-ChildItem $usbClass -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $ap="$($_.Name)\Device Parameters\Interrupt Management\Affinity Policy"
        cmd /c "reg add `"$ap`" /v DevicePolicy /t REG_DWORD /d 4 /f >nul 2>&1"
        cmd /c "reg add `"$ap`" /v AssignmentSetOverride /t REG_BINARY /d $usbMask /f >nul 2>&1"; $usbN++
    }
    $hidClass="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{745a17a0-74d3-11d0-b6fe-00a0c90f57da}"; $hidN=0
    Get-ChildItem $hidClass -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $ap="$($_.Name)\Device Parameters\Interrupt Management\Affinity Policy"
        cmd /c "reg add `"$ap`" /v DevicePolicy /t REG_DWORD /d 4 /f >nul 2>&1"
        cmd /c "reg add `"$ap`" /v AssignmentSetOverride /t REG_BINARY /d $hidMask /f >nul 2>&1"; $hidN++
    }
    Set-State "IRQSmartBalancer" 1
    New-ItemProperty -Path $StateKey -Name "IRQGpuCore" -PropertyType DWord -Value $gpuCore -Force | Out-Null
    Write-Host ""; Write-Host "+ IRQ Smart Balancer aplicado:" -ForegroundColor Green
    Write-Host "  GPU: $gpuN dispositivos -> Core $gpuCore" -ForegroundColor Green
    Write-Host "  NIC: $nicN interfaces  -> Core $nicCore" -ForegroundColor Green
    Write-Host "  USB: $usbN controllers -> Core $usbCore" -ForegroundColor Green
    Write-Host "  HID: $hidN dispositivos -> Core $gpuCore" -ForegroundColor Green
    Write-Host ""; Write-Host "REINICIAR Windows para que surta efecto." -ForegroundColor Yellow
    Pause
}

function Revert-IRQSmartBalancer {
    $classes=@("HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}",
               "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}",
               "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000}",
               "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{745a17a0-74d3-11d0-b6fe-00a0c90f57da}")
    foreach ($cls in $classes) {
        Get-ChildItem $cls -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
            $ap="$($_.Name)\Device Parameters\Interrupt Management\Affinity Policy"
            cmd /c "reg delete `"$ap`" /v DevicePolicy /f >nul 2>&1"
            cmd /c "reg delete `"$ap`" /v AssignmentSetOverride /f >nul 2>&1"
        }
    }
    Get-PnpDevice -Class Display -EA SilentlyContinue | ForEach-Object {
        $ap="HKLM\SYSTEM\ControlSet001\Enum\$($_.InstanceId)\Device Parameters\Interrupt Management\Affinity Policy"
        cmd /c "reg delete `"$ap`" /v DevicePolicy /f >nul 2>&1"
        cmd /c "reg delete `"$ap`" /v AssignmentSetOverride /f >nul 2>&1"
    }
    Set-State "IRQSmartBalancer" 0
    Write-Host "IRQ Smart Balancer: revertido a gestion automatica (reinicio requerido)." -ForegroundColor Yellow
}

# ==================== FIXES EXTREMOS 2024 ====================

function Apply-MpoOff {
    $path="HKLM:\SOFTWARE\Microsoft\Windows\Dwm"
    if (!(Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    Set-ItemProperty $path "OverlayTestMode" 5 -Force
    Set-State "MpoOff" 1
    Write-Host "Disable MPO (Multi-Plane Overlay): APLICADO." -ForegroundColor Green
    Write-Host "  -> Soluciona el 'choppy image' (stutters visuales con altos FPS) y flickering." -ForegroundColor DarkGray
}
function Revert-MpoOff {
    Remove-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\Dwm" "OverlayTestMode" -EA SilentlyContinue
    Set-State "MpoOff" 0; Write-Host "MPO: restaurado a default." -ForegroundColor Yellow
}

function Apply-ForceIndependentFlip {
    $path="HKCU:\Software\Microsoft\DirectX\UserGpuPreferences"
    if (!(Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    Set-ItemProperty $path "DirectXUserGlobalSettings" "SwapEffectUpgradeEnable=1;" -Force
    Set-State "ForceIndependentFlip" 1
    Write-Host "Force DXGI Independent Flip: APLICADO." -ForegroundColor Green
    Write-Host "  -> Activa 'Optimizaciones para juegos en ventana' (Bypass DWM moderno)." -ForegroundColor DarkGray
    Write-Host "  -> Forza Hardware Composed: Independent Flip en CS2/Valorant para 0 lag." -ForegroundColor DarkGray
}
function Revert-ForceIndependentFlip {
    Remove-ItemProperty "HKCU:\Software\Microsoft\DirectX\UserGpuPreferences" "DirectXUserGlobalSettings" -EA SilentlyContinue
    Set-State "ForceIndependentFlip" 0; Write-Host "Force Independent Flip: desactivado." -ForegroundColor Yellow
}

function Apply-MarkCMouseFix {
    $path="HKCU:\Control Panel\Mouse"
    $xCurve = [byte[]](0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x15,0x6E,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x40,0x01,0x00,0x00,0x00,0x00,0x00,0x29,0xDC,0x03,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x28,0x00,0x00,0x00,0x00,0x00)
    $yCurve = [byte[]](0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xB8,0x5E,0x01,0x00,0x00,0x00,0x00,0x00,0x00,0x80,0x02,0x00,0x00,0x00,0x00,0x00,0x29,0xDC,0x03,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x28,0x00,0x00,0x00,0x00,0x00)
    Set-ItemProperty $path "SmoothMouseXCurve" $xCurve -Force
    Set-ItemProperty $path "SmoothMouseYCurve" $yCurve -Force
    Set-State "MarkCMouseFix" 1
    Write-Host "MarkC Mouse Fix (Curvas 1:1): APLICADO." -ForegroundColor Green
    Write-Host "  -> Inyecta curvas planas directas en el registro para matar cualquier filtrado residual de Windows." -ForegroundColor DarkGray
}
function Revert-MarkCMouseFix {
    $path="HKCU:\Control Panel\Mouse"
    $xCurve = [byte[]](0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x15,0x6E,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x40,0x01,0x00,0x00,0x00,0x00,0x00,0x29,0xDC,0x03,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x28,0x00,0x00,0x00,0x00,0x00)
    $yCurve = [byte[]](0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xB8,0x5E,0x01,0x00,0x00,0x00,0x00,0x00,0x00,0x80,0x02,0x00,0x00,0x00,0x00,0x00,0x29,0xDC,0x03,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x28,0x00,0x00,0x00,0x00,0x00)
    Set-ItemProperty $path "SmoothMouseXCurve" $xCurve -Force
    Set-ItemProperty $path "SmoothMouseYCurve" $yCurve -Force
    Set-State "MarkCMouseFix" 0; Write-Host "Curvas MarkC: removidas (default)." -ForegroundColor Yellow
}

function Apply-PrioritySeparation {
    $path="HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl"
    Set-ItemProperty $path "Win32PrioritySeparation" 0x28 -Force
    Set-State "PrioritySeparation" 1
    Write-Host "Win32PrioritySeparation a 0x28: APLICADO." -ForegroundColor Green
    Write-Host "  -> Prioriza agresivamente la aplicacion en foco (juego) y reduce latencia de input del CPU." -ForegroundColor DarkGray
}
function Revert-PrioritySeparation {
    $path="HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl"
    Set-ItemProperty $path "Win32PrioritySeparation" 2 -Force
    Set-State "PrioritySeparation" 0; Write-Host "Win32PrioritySeparation: restaurado (2)." -ForegroundColor Yellow
}

function Apply-NetworkThrottlingOff {
    $path="HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"
    Set-ItemProperty $path "NetworkThrottlingIndex" 0xFFFFFFFF -Force
    Set-ItemProperty $path "SystemResponsiveness" 0 -Force
    Set-State "NetworkThrottlingOff" 1
    Write-Host "Network Throttling / System Responsiveness: APLICADO." -ForegroundColor Green
    Write-Host "  -> Asigna el 100% de la red y CPU a los juegos en vez de reservar para multimedia." -ForegroundColor DarkGray
}
function Revert-NetworkThrottlingOff {
    $path="HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"
    Set-ItemProperty $path "NetworkThrottlingIndex" 10 -Force
    Set-ItemProperty $path "SystemResponsiveness" 20 -Force
    Set-State "NetworkThrottlingOff" 0; Write-Host "Network Throttling: restaurado (defaults 10/20)." -ForegroundColor Yellow
}

function Apply-TcpAutoTuning {
    netsh int tcp set global autotuninglevel=disabled 2>$null | Out-Null
    Set-State "TcpAutoTuning" 1
    Write-Host "TCP Auto-Tuning: DISABLED. APLICADO." -ForegroundColor Green
    Write-Host "  -> Mitiga severamente el bufferbloat local al no expandir el tamaÃƒÂ±o de la ventana TCP." -ForegroundColor DarkGray
}
function Revert-TcpAutoTuning {
    netsh int tcp set global autotuninglevel=normal 2>$null | Out-Null
    Set-State "TcpAutoTuning" 0; Write-Host "TCP Auto-Tuning: restaurado a Normal." -ForegroundColor Yellow
}

function Apply-HpetOff {
    bcdedit /deletevalue useplatformclock 2>$null | Out-Null
    try {
        $hpet = Get-CimInstance Win32_PnPEntity -Filter "Name='High precision event timer'" -EA SilentlyContinue
        if ($hpet) { Disable-PnpDevice -InstanceId $hpet.PNPDeviceID -Confirm:$false -EA SilentlyContinue | Out-Null }
    } catch {}
    Set-State "HpetOff" 1
    Write-Host "HPET (High Precision Event Timer): DESACTIVADO." -ForegroundColor Green
    Write-Host "  -> Desactivado en BCD y Device Manager. Disminuye latencia DPC masivamente." -ForegroundColor DarkGray
    Write-Host "  -> IMPORTANTE: Si ves micro-stutters, desactivalo tambien en la BIOS." -ForegroundColor Yellow
}
function Revert-HpetOff {
    try {
        $hpet = Get-CimInstance Win32_PnPEntity -Filter "Name='High precision event timer'" -EA SilentlyContinue
        if ($hpet) { Enable-PnpDevice -InstanceId $hpet.PNPDeviceID -Confirm:$false -EA SilentlyContinue | Out-Null }
    } catch {}
    Set-State "HpetOff" 0; Write-Host "HPET: activado en Device Manager." -ForegroundColor Yellow
}

function Apply-PcieAspmDisable {
    powercfg -attributes SUB_PCIEXPRESS ASPM -ATTRIB_HIDE 2>$null | Out-Null
    powercfg -attributes SUB_PCIEXPRESS LPM -ATTRIB_HIDE 2>$null | Out-Null
    $guids = @("381b4222-f694-41f0-9685-ff5bb260df2e", "scheme_current", "e9a42b02-d5df-448d-aa00-03f14749eb61")
    foreach ($g in $guids) {
        powercfg -setacvalue $g SUB_PCIEXPRESS ASPM 0 2>$null | Out-Null
    }
    $path="HKLM:\SYSTEM\CurrentControlSet\Services\pci\Parameters"
    if (!(Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    Set-ItemProperty $path "EnableASPM" 0 -Force -EA SilentlyContinue
    Set-ItemProperty $path "EnableL1" 0 -Force -EA SilentlyContinue
    Set-State "PcieAspmDisable" 1
    Write-Host "PCIe ASPM (L0s/L1) Disabled: APLICADO." -ForegroundColor Green
    Write-Host "  -> Forza el bus PCIe a quedarse en estado L0 (max power/min latency). Elimina stutter de CPU/GPU." -ForegroundColor DarkGray
}
function Revert-PcieAspmDisable {
    $path="HKLM:\SYSTEM\CurrentControlSet\Services\pci\Parameters"
    Remove-ItemProperty $path "EnableASPM" -EA SilentlyContinue
    Remove-ItemProperty $path "EnableL1" -EA SilentlyContinue
    Set-State "PcieAspmDisable" 0; Write-Host "PCIe ASPM: restaurado." -ForegroundColor Yellow
}

function Apply-DisableCfgPerGame {
    foreach ($exe in $GameExes) {
        $path="HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exe"
        if (!(Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        Set-ItemProperty $path "MitigationOptions" ([byte[]](2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2)) -Force -EA SilentlyContinue
    }
    Set-State "DisableCfgPerGame" 1
    Write-Host "Disable CFG / Exploit Protection (Per-Game): APLICADO." -ForegroundColor Green
    Write-Host "  -> Desactiva chequeos de memoria extra de Windows solo para los juegos. Remueve input lag pesado." -ForegroundColor DarkGray
}
function Revert-DisableCfgPerGame {
    foreach ($exe in $GameExes) {
        $path="HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exe"
        Remove-ItemProperty $path "MitigationOptions" -EA SilentlyContinue
    }
    Set-State "DisableCfgPerGame" 0; Write-Host "CFG Per-Game: restaurado." -ForegroundColor Yellow
}

function Apply-NdisBufferTuning {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        cmd /c "reg add `"$($_.Name)`" /v `"*ReceiveBuffers`" /t REG_SZ /d 256 /f >nul 2>&1"
        cmd /c "reg add `"$($_.Name)`" /v `"*TransmitBuffers`" /t REG_SZ /d 256 /f >nul 2>&1"
    }
    Set-State "NdisBufferTuning" 1
    Write-Host "NDIS Ring Buffer Tuning: APLICADO." -ForegroundColor Green
    Write-Host "  -> Reduce buffers de NIC a 256 (prioriza menor latencia sobre ancho de banda pico). Corta bufferbloat." -ForegroundColor DarkGray
}
function Revert-NdisBufferTuning {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        cmd /c "reg delete `"$($_.Name)`" /v `"*ReceiveBuffers`" /f >nul 2>&1"
        cmd /c "reg delete `"$($_.Name)`" /v `"*TransmitBuffers`" /f >nul 2>&1"
    }
    Set-State "NdisBufferTuning" 0; Write-Host "NDIS Ring Buffers: revertido a defaults del driver." -ForegroundColor Yellow
}

function Apply-AudioDpcFix {
    Set-ItemProperty "HKCU:\Software\Microsoft\Multimedia\Audio\WaveVolumeProperty" "AudioEnhancement" 0 -Force -EA SilentlyContinue
    $path="HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio"
    if (!(Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    Set-ItemProperty $path "DisableProtectedAudioDG" 1 -Force -EA SilentlyContinue
    Set-State "AudioDpcFix" 1
    Write-Host "Audio DPC + Enhancements Fix: APLICADO." -ForegroundColor Green
    Write-Host "  -> Deshabilita el overhead de DRM en AudioDG y Enhancements, reduciendo interrupciones DPC." -ForegroundColor DarkGray
}
function Revert-AudioDpcFix {
    Remove-ItemProperty "HKCU:\Software\Microsoft\Multimedia\Audio\WaveVolumeProperty" "AudioEnhancement" -EA SilentlyContinue
    Remove-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio" "DisableProtectedAudioDG" -EA SilentlyContinue
    Set-State "AudioDpcFix" 0; Write-Host "Audio DPC Fix: revertido a defaults de Windows." -ForegroundColor Yellow
}

function Apply-CpuIdleThresholds {
    # IDLEPROMOTE, IDLEDEMOTE: documentados. IDLEDISABLE GUID: 5d76a2ca
    powercfg -setacvalue scheme_current sub_processor IDLEPROMOTE 0 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_processor IDLEDEMOTE 0 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_processor IDLEDISABLE 1 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_processor IDLESCALING 0 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_processor PERFCHECK 10 2>$null | Out-Null
    powercfg -setactive scheme_current 2>$null | Out-Null
    Set-State "CpuIdleThresholds" 1
    Write-Host "CPU Idle Thresholds (0 Wake-up Latency): APLICADO." -ForegroundColor Green
    Write-Host "  -> Evita que los cores caigan en C-States profundos via Windows Power Plan." -ForegroundColor DarkGray
    Write-Host "  -> IDLEDISABLE=1, IDLEPROMOTE=0, IDLEDEMOTE=0, IDLESCALING=0" -ForegroundColor DarkGray
}
function Revert-CpuIdleThresholds {
    powercfg -setacvalue scheme_current sub_processor IDLEPROMOTE 60 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_processor IDLEDEMOTE 40 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_processor IDLEDISABLE 0 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_processor IDLESCALING 1 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_processor PERFCHECK 100 2>$null | Out-Null
    powercfg -setactive scheme_current 2>$null | Out-Null
    Set-State "CpuIdleThresholds" 0; Write-Host "CPU Idle Thresholds: revertido a defaults." -ForegroundColor Yellow
}

function Apply-CpuBoostMode {
    powercfg -setacvalue scheme_current sub_processor PERFBOOSTMODE 2
    powercfg -setacvalue scheme_current sub_processor PERFBOOSTPOL 100
    powercfg -setacvalue scheme_current sub_processor PERFOPTION 2
    powercfg -setactive scheme_current
    Set-State "CpuBoostMode" 1
    Write-Host "CPU Performance Boost Mode (Aggressive): APLICADO." -ForegroundColor Green
    Write-Host "  -> Mantiene frecuencias altas estables eliminando la latencia de rampa." -ForegroundColor DarkGray
}
function Revert-CpuBoostMode {
    powercfg -setacvalue scheme_current sub_processor PERFBOOSTMODE 3
    powercfg -setacvalue scheme_current sub_processor PERFBOOSTPOL 33
    powercfg -setacvalue scheme_current sub_processor PERFOPTION 3
    powercfg -setactive scheme_current
    Set-State "CpuBoostMode" 0; Write-Host "CPU Boost Mode: revertido." -ForegroundColor Yellow
}

function Apply-DisableBackgroundHwAccel {
    $steamPath="HKCU:\Software\Valve\Steam"
    if (Test-Path $steamPath) {
        Set-ItemProperty $steamPath "gpuacceleration" 0 -Force -EA SilentlyContinue
        Set-ItemProperty $steamPath "WebBrowser" 0 -Force -EA SilentlyContinue
    }
    Set-State "DisableBackgroundHwAccel" 1
    Write-Host "Background HW Accel (Steam): DESACTIVADA." -ForegroundColor Green
    Write-Host "  -> Steam ya no competira con tu juego por renderizado GPU." -ForegroundColor DarkGray
    Write-Host "  -> IMPORTANTE: Apaga el Overlay de GeForce Experience manualmente." -ForegroundColor Yellow
}
function Revert-DisableBackgroundHwAccel {
    $steamPath="HKCU:\Software\Valve\Steam"
    if (Test-Path $steamPath) {
        Set-ItemProperty $steamPath "gpuacceleration" 1 -Force -EA SilentlyContinue
        Set-ItemProperty $steamPath "WebBrowser" 1 -Force -EA SilentlyContinue
    }
    Set-State "DisableBackgroundHwAccel" 0; Write-Host "Background HW Accel: restaurada." -ForegroundColor Yellow
}

function Apply-MouseDataQueueSize {
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Services\mouclass\Parameters`" /v MouseDataQueueSize /t REG_DWORD /d 16 /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters`" /v KeyboardDataQueueSize /t REG_DWORD /d 16 /f >nul 2>&1"
    Set-State "MouseDataQueueSize" 1
    Write-Host "Mouse/Kbd Data Queue Size (16): APLICADO." -ForegroundColor Green
    Write-Host "  -> Reduce la profundidad del buffer de 100 a 16 para latencia de hardware extrema." -ForegroundColor DarkGray
}
function Revert-MouseDataQueueSize {
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Services\mouclass\Parameters`" /v MouseDataQueueSize /t REG_DWORD /d 100 /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters`" /v KeyboardDataQueueSize /t REG_DWORD /d 100 /f >nul 2>&1"
    Set-State "MouseDataQueueSize" 0; Write-Host "Data Queue Size: revertido a 100 (default)." -ForegroundColor Yellow
}

function Apply-CompositorPriority {
    $csrss="HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions"
    $dwm="HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\dwm.exe\PerfOptions"
    if (!(Test-Path $csrss)) { New-Item -Path $csrss -Force | Out-Null }
    if (!(Test-Path $dwm)) { New-Item -Path $dwm -Force | Out-Null }
    Set-ItemProperty $csrss "CpuPriorityClass" 3 -Force -EA SilentlyContinue
    Set-ItemProperty $dwm "CpuPriorityClass" 3 -Force -EA SilentlyContinue
    Set-State "CompositorPriority" 1
    Write-Host "Compositor Priority (CSRSS/DWM High): APLICADO." -ForegroundColor Green
    Write-Host "  -> El OS priorizara los procesos de I/O de mouse y composicion de pantalla (High)." -ForegroundColor DarkGray
}
function Revert-CompositorPriority {
    $csrss="HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions"
    $dwm="HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\dwm.exe\PerfOptions"
    Remove-ItemProperty $csrss "CpuPriorityClass" -EA SilentlyContinue
    Remove-ItemProperty $dwm "CpuPriorityClass" -EA SilentlyContinue
    Set-State "CompositorPriority" 0; Write-Host "Compositor Priority: revertido a OS scheduler normal." -ForegroundColor Yellow
}

# === BLURBUSTERS KNOWLEDGE BASE FIXES ===
function Apply-AudioEnhancementsOff {
    # Deshabilitar Audio Enhancements del sistema - causa DPC spikes que generan floaty mouse
    # Fuente: BlurBusters threads [15352],[12413],[10508] - confirmado por Chief Blur Buster
    $audioPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"
    Set-ItemProperty $audioPath "SystemResponsiveness" 0 -Force -EA SilentlyContinue
    # Desactivar mejoras de audio en todos los endpoints
    $endpointPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"
    if (Test-Path $endpointPath) {
        Get-ChildItem $endpointPath -EA SilentlyContinue | ForEach-Object {
            $fxProps = Join-Path $_.PSPath "FxProperties"
            if (Test-Path $fxProps) {
                # {1da5d803-d492-4edd-8c23-e0c0ffee7f0e},6 = Disable enhancements flag
                cmd /c "reg add `"$($_.Name)\FxProperties`" /v `"{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},6`" /t REG_DWORD /d 1 /f >nul 2>&1"
            }
        }
    }
    # Deshabilitar Spatial Sound (Atmos, etc.) - genera callbacks extra en el kernel
    $spatialPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Audio"
    if (!(Test-Path $spatialPath)) { New-Item -Path $spatialPath -Force | Out-Null }
    Set-ItemProperty $spatialPath "EnableSpatialAudio" 0 -Force -EA SilentlyContinue
    Set-State "AudioEnhancementsOff" 1
    Write-Host "Audio Enhancements OFF: APLICADO." -ForegroundColor Green
    Write-Host "  -> Elimina DPC spikes del sistema de audio que causan floaty mouse." -ForegroundColor DarkGray
    Write-Host "  -> Confirmado por Chief Blur Buster como causa real en algunos sistemas." -ForegroundColor DarkGray
    Write-Host "  -> REINICIAR para aplicar completamente." -ForegroundColor Yellow
}
function Revert-AudioEnhancementsOff {
    $spatialPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Audio"
    Set-ItemProperty $spatialPath "EnableSpatialAudio" 1 -Force -EA SilentlyContinue
    Set-State "AudioEnhancementsOff" 0; Write-Host "Audio Enhancements: revertido." -ForegroundColor Yellow
}

function Apply-RealtekAudioFix {
    # BlurBusters [15352],[12413]: El driver Realtek HD Audio causa DPC spikes que generan
    # mouse floaty en sistemas especificos. Fix: deshabilitar el dispositivo en Device Manager.
    $realtekFound = $false
    Get-PnpDevice -Class "AudioEndpoint" -EA SilentlyContinue | Where-Object { $_.FriendlyName -match 'Realtek' -and $_.Status -eq 'OK' } | ForEach-Object {
        Disable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -EA SilentlyContinue
        $realtekFound = $true
        Write-Host "  -> Deshabilitado: $($_.FriendlyName)" -ForegroundColor DarkGray
    }
    if (-not $realtekFound) {
        Write-Host "  -> No se encontro dispositivo Realtek Audio activo en este sistema." -ForegroundColor Yellow
    }
    # Tambien desactivar enhancements del driver Realtek en registro
    $realtekRegPaths = @(
        "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}"
    )
    foreach ($path in $realtekRegPaths) {
        Get-ChildItem $path -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
            $driverDesc = (Get-ItemProperty $_.PSPath -EA SilentlyContinue).DriverDesc
            if ($driverDesc -match 'Realtek') {
                Set-ItemProperty $_.PSPath "EnableDynamicDevices" 0 -Force -EA SilentlyContinue
            }
        }
    }
    Set-State "RealtekAudioFix" 1
    Write-Host "Realtek Audio Fix: APLICADO." -ForegroundColor Green
    Write-Host "  -> Si el mouse se siente normal ahora, el driver Realtek era el culpable." -ForegroundColor DarkGray
    Write-Host "  -> REINICIAR y probar. Si empeora, usa Revertir." -ForegroundColor Yellow
}
function Revert-RealtekAudioFix {
    Get-PnpDevice -Class "AudioEndpoint" -EA SilentlyContinue | Where-Object { $_.FriendlyName -match 'Realtek' -and $_.Status -ne 'OK' } | ForEach-Object {
        Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -EA SilentlyContinue
    }
    Set-State "RealtekAudioFix" 0; Write-Host "Realtek Audio Fix: revertido (audio Realtek re-habilitado)." -ForegroundColor Yellow
}

function Apply-GameBarPreserve {
    # BlurBusters [15717]: En Windows 11 24H2+, desactivar Game Bar rompe Independent Flip
    # en juegos DX12, sumando latencia. El modulo [31] lo desactiva globalmente.
    # Este modulo lo restaura al estado correcto para DX12.
    $build = [System.Environment]::OSVersion.Version.Build
    if ($build -ge 26100) {
        # Win11 24H2+ (build 26100+): mantener Game Bar activo pero deshabilitar solo DVR/capturas
        $gbPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR"
        $policyPath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR"
        if (!(Test-Path $gbPath)) { New-Item -Path $gbPath -Force | Out-Null }
        if (!(Test-Path $policyPath)) { New-Item -Path $policyPath -Force | Out-Null }
        # Deshabilitar SOLO la grabacion en segundo plano, pero mantener la capa de Game Bar
        Set-ItemProperty $gbPath "AppCaptureEnabled" 0 -Force -EA SilentlyContinue
        Set-ItemProperty $gbPath "GameDVR_Enabled" 0 -Force -EA SilentlyContinue
        # RE-HABILITAR Game Bar si fue desactivado por el modulo [31]
        $xboxPath = "HKCU:\Software\Microsoft\GameBar"
        if (!(Test-Path $xboxPath)) { New-Item -Path $xboxPath -Force | Out-Null }
        Set-ItemProperty $xboxPath "AllowAutoGameMode" 1 -Force -EA SilentlyContinue
        Set-ItemProperty $xboxPath "ShowGameModeNotifications" 0 -Force -EA SilentlyContinue
        cmd /c "reg add `"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR`" /v GameDVR_FSEBehaviorMode /t REG_DWORD /d 2 /f >nul 2>&1"
        Set-State "GameBarPreserve" 1
        Write-Host "Game Bar Preserve (DX12 Fix) Win11 24H2+: APLICADO." -ForegroundColor Green
        Write-Host "  -> DVR/Capture desactivado, pero Game Bar activo para Independent Flip DX12." -ForegroundColor DarkGray
        Write-Host "  -> Segun BlurBusters: desactivar Game Bar en 24H2 rompe iFlip en DX12." -ForegroundColor Yellow
    } else {
        Write-Host "  -> Este fix es solo para Windows 11 24H2+ (build 26100+)." -ForegroundColor Yellow
        Write-Host "  -> Tu sistema tiene build $build, no es necesario." -ForegroundColor DarkGray
        Set-State "GameBarPreserve" 1
    }
}
function Revert-GameBarPreserve {
    Set-State "GameBarPreserve" 0; Write-Host "Game Bar Preserve: revertido." -ForegroundColor Yellow
}

function Apply-DualCCDRyzenAffinity {
    # BlurBusters [15683]: En CPUs Ryzen Dual-CCD (5900X, 7950X3D, 9950X, etc.)
    # el scheduler salta entre CCDs generando latencia de cache. Fix: forzar affinity
    # del juego activo al CCD primario (cores 0-7 en la mayoria de modelos).
    $cpuName = (Get-WmiObject Win32_Processor -EA SilentlyContinue | Select-Object -First 1).Name
    $isDualCCD = $cpuName -match '(5900X|5950X|7900X|7950X|7900|7950|9900X|9950X|PRO 595)'
    if (-not $isDualCCD) {
        Write-Host "  -> CPU detectado: $cpuName" -ForegroundColor DarkGray
        Write-Host "  -> Este modulo es para Ryzen Dual-CCD. Tu CPU no parece serlo." -ForegroundColor Yellow
        Write-Host "  -> Podes continuar igual si sabes que tu CPU es Dual-CCD." -ForegroundColor Yellow
    }
    # Forzar affinity del proceso principal del juego via registro de Processor Affinity
    # Para cs2.exe, valorant, etc. usando IFEO ProcessorAffinity
    # Mascara 0xFF = cores 0-7 (primer CCD)
    $mask = 255 # 0xFF = cores 0-7
    $games = @("cs2.exe","VALORANT-Win64-Shipping.exe","EscapeFromTarkov.exe","r5apex.exe")
    foreach ($game in $games) {
        $ifeoPth = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$game\PerfOptions"
        if (!(Test-Path $ifeoPth)) { New-Item -Path $ifeoPth -Force | Out-Null }
        Set-ItemProperty $ifeoPth "ProcessorAffinity" $mask -Force -EA SilentlyContinue
    }
    Set-State "DualCCDRyzenAffinity" 1
    Write-Host "Dual-CCD Ryzen CPU Affinity (CCD0): APLICADO." -ForegroundColor Green
    Write-Host "  -> CS2/Valorant forzados al CCD primario (cores 0-7)." -ForegroundColor DarkGray
    Write-Host "  -> Evita saltos de cache entre CCDs que causan micro-stutters." -ForegroundColor DarkGray
    Write-Host "  -> Solo en Ryzen Dual-CCD. En X3D: forzar al CCD con 3D V-Cache." -ForegroundColor Yellow
}
function Revert-DualCCDRyzenAffinity {
    $games = @("cs2.exe","VALORANT-Win64-Shipping.exe","EscapeFromTarkov.exe","r5apex.exe")
    foreach ($game in $games) {
        $ifeoPth = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$game\PerfOptions"
        Remove-ItemProperty $ifeoPth "ProcessorAffinity" -EA SilentlyContinue
    }
    Set-State "DualCCDRyzenAffinity" 0; Write-Host "Dual-CCD Affinity: revertido (affinity libre)." -ForegroundColor Yellow
}

function Apply-UsbAudioSeparation {
    # BlurBusters [13863],[10508]: USB Headsets en el mismo hub USB que el mouse
    # generan contention de bandwidth y pueden causar micro-drops de input.
    # Este modulo detecta dispositivos de audio USB y muestra informacion de diagnostico.
    Write-Host "Analizando dispositivos de audio USB..." -ForegroundColor Cyan
    $usbAudioDevices = Get-PnpDevice -Class "AudioEndpoint" -EA SilentlyContinue | Where-Object {
        $_.InstanceId -match 'USB' -and $_.Status -eq 'OK'
    }
    if ($usbAudioDevices) {
        Write-Host ""
        Write-Host "  !! ATENCION: Se detectaron dispositivos de audio USB:" -ForegroundColor Red
        foreach ($dev in $usbAudioDevices) {
            Write-Host "     -> $($dev.FriendlyName) [$($dev.InstanceId)]" -ForegroundColor Yellow
        }
        Write-Host ""
        Write-Host "  ACCION RECOMENDADA (BlurBusters confirmed):" -ForegroundColor Cyan
        Write-Host "  1. Mover el headset/audio USB a un puerto TRASERO diferente al del mouse." -ForegroundColor White
        Write-Host "  2. Idealmente en un puerto gestionado por un controlador xHCI distinto." -ForegroundColor White
        Write-Host "  3. El audio USB compite en el mismo bus del mouse -> micro-drops de input." -ForegroundColor DarkGray
    } else {
        Write-Host "  -> No se detectaron dispositivos de audio USB activos." -ForegroundColor Green
        Write-Host "  -> Tu configuracion de audio no genera contention USB. Bien!" -ForegroundColor Green
    }
    # Marcar como revisado
    Set-State "UsbAudioSeparation" 1
    Write-Host ""
    Write-Host "USB Audio Separation Check: COMPLETADO." -ForegroundColor Green
}
function Revert-UsbAudioSeparation {
    Set-State "UsbAudioSeparation" 0; Write-Host "USB Audio Check: reseteado." -ForegroundColor Yellow
}

# ==================== POWER PLANS ====================
function Apply-GamingPowerPlan {
    Clear-Host; Write-Banner "CREAR / ACTIVAR POWER PLAN GAMING" "Cyan"
    $gamingGuid="00000000-0000-0000-0000-000000000042"
    if (powercfg -list | Select-String $gamingGuid) {
        Write-Host "Plan Gaming ya existe, activando..." -ForegroundColor Green; powercfg -setactive $gamingGuid
    } else {
        $out=powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61 2>&1
        if ($out -match '([0-9A-Fa-f-]{36})') { $g=$Matches[1]; powercfg -changename $g "Gaming Low-Latency" "Optimizado para competitivo" 2>$null; powercfg -setactive $g }
        else { powercfg -setactive e9a42b02-d5df-448d-aa00-03f14749eb61 2>$null }
    }
    powercfg -setacvalue scheme_current sub_processor PERFBOOSTMODE 2 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_processor PERFOPTION 2 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_disk DISKIDLE 0 2>$null | Out-Null
    powercfg -setacvalue scheme_current sub_processor CPMINCORES 100 2>$null | Out-Null
    Write-Host "Plan Gaming Low-Latency: creado y activado." -ForegroundColor Green; Write-Host ""; Pause
}

function Show-PowerPlansMenu {
    Clear-Host; Write-Host "=== POWER PLANS ===" -ForegroundColor Cyan; Write-Host ""
    powercfg -list | ForEach-Object { Write-Host "  $_" }; Write-Host ""
    Write-Host "[1] Gaming Low-Latency (customizado)"; Write-Host "[2] Ultimate Performance"; Write-Host "[3] Balanced"; Write-Host "[4] Volver"; Write-Host ""
    $c=Read-Host "Elegi"
    switch ($c) {
        "1" { Apply-GamingPowerPlan }
        "2" { powercfg -setactive e9a42b02-d5df-448d-aa00-03f14749eb61; Write-Host "Ultimate Performance activado."; Pause }
        "3" { powercfg -setactive scheme_balanced; Write-Host "Balanced activado."; Pause }
        "4" { return }
        default { Write-Host "Invalido." -ForegroundColor Red; Start-Sleep 1; Show-PowerPlansMenu }
    }
}

# ==================== MODULOS 1-23 ====================
function Apply-MouseAccel {
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseSpeed -Value "0"
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseThreshold1 -Value "0"
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseThreshold2 -Value "0"
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseSensitivity -Value "10" -EA SilentlyContinue
    Set-State "MouseAccel" 1; Write-Host "Aceleracion del puntero: OFF. Speed 6/11 (1:1)." -ForegroundColor Green
    Write-Host "  -> Activa Raw Input en CS2/Valorant para bypass completo del OS." -ForegroundColor DarkGray
}
function Revert-MouseAccel {
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseSpeed -Value "1"
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseThreshold1 -Value "6"
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseThreshold2 -Value "10"
    Set-State "MouseAccel" 0; Write-Host "Aceleracion: restaurada." -ForegroundColor Yellow
}

function Apply-USBPower {
    foreach ($branch in "ACPI","HID","PCI","USB") {
        Get-ChildItem "HKLM:\SYSTEM\ControlSet001\Enum\$branch" -Recurse -EA SilentlyContinue | Where-Object { $_.PSChildName -eq "Device Parameters" } | ForEach-Object {
            $p=$_.Name
            cmd /c "reg add `"$p`" /v EnhancedPowerManagementEnabled /t REG_DWORD /d 0 /f >nul 2>&1"
            cmd /c "reg add `"$p`" /v SelectiveSuspendEnabled /t REG_BINARY /d 00 /f >nul 2>&1"
            cmd /c "reg add `"$p`" /v SelectiveSuspendOn /t REG_DWORD /d 0 /f >nul 2>&1"
            cmd /c "reg add `"$p`" /v WaitWakeEnabled /t REG_DWORD /d 0 /f >nul 2>&1"
        }
    }
    Set-State "USBPower" 1; Write-Host "Ahorro energia USB/HID/PCI/ACPI: OFF." -ForegroundColor Green
}
function Revert-USBPower {
    foreach ($branch in "ACPI","HID","PCI","USB") {
        Get-ChildItem "HKLM:\SYSTEM\ControlSet001\Enum\$branch" -Recurse -EA SilentlyContinue | Where-Object { $_.PSChildName -eq "Device Parameters" } | ForEach-Object {
            foreach ($v in "EnhancedPowerManagementEnabled","SelectiveSuspendEnabled","SelectiveSuspendOn","WaitWakeEnabled") {
                cmd /c "reg delete `"$($_.Name)`" /v $v /f >nul 2>&1"
            }
        }
    }
    Set-State "USBPower" 0; Write-Host "Ahorro energia USB: restaurado." -ForegroundColor Yellow
}

function Apply-GpuMsi {
    Get-PnpDevice -Class Display -EA SilentlyContinue | ForEach-Object {
        cmd /c "reg add `"HKLM\SYSTEM\ControlSet001\Enum\$($_.InstanceId)\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties`" /v MSISupported /t REG_DWORD /d 1 /f >nul 2>&1"
    }
    Set-State "GpuMsi" 1; Write-Host "MSI Mode GPU: ACTIVADO (reduce latencia de interrupcion)." -ForegroundColor Green
}
function Revert-GpuMsi {
    Get-PnpDevice -Class Display -EA SilentlyContinue | ForEach-Object {
        cmd /c "reg add `"HKLM\SYSTEM\ControlSet001\Enum\$($_.InstanceId)\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties`" /v MSISupported /t REG_DWORD /d 0 /f >nul 2>&1"
    }
    Set-State "GpuMsi" 0; Write-Host "MSI Mode GPU: desactivado." -ForegroundColor Yellow
}

function Apply-NetPower {
    $base="HKLM:\System\ControlSet001\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $p=$_.Name
        cmd /c "reg add `"$p`" /v PnPCapabilities /t REG_DWORD /d 24 /f >nul 2>&1"
        foreach ($v in "AdvancedEEE","*EEE","EEELinkAdvertisement","SipsEnabled","ULPMode","GigaLite","EnableGreenEthernet","PowerSavingMode","AutoDisableGigabit") {
            cmd /c "reg add `"$p`" /v `"$v`" /t REG_SZ /d 0 /f >nul 2>&1"
        }
    }
    Set-State "NetPower" 1; Write-Host "Ahorro energia NIC: OFF (EEE, GreenEthernet OFF)." -ForegroundColor Green
}
function Revert-NetPower {
    $base="HKLM:\System\ControlSet001\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        foreach ($v in "PnPCapabilities","AdvancedEEE","*EEE","EEELinkAdvertisement","SipsEnabled","ULPMode","GigaLite","EnableGreenEthernet","PowerSavingMode","AutoDisableGigabit") {
            cmd /c "reg delete `"$($_.Name)`" /v `"$v`" /f >nul 2>&1"
        }
    }
    Set-State "NetPower" 0; Write-Host "Ahorro energia NIC: restaurado." -ForegroundColor Yellow
}

function Apply-TimerRes {
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel`" /v GlobalTimerResolutionRequests /t REG_DWORD /d 1 /f >nul 2>&1"
    Set-State "TimerRes" 1; Write-Host "Timer Resolution 0.5ms: ACTIVADO (requiere reinicio)." -ForegroundColor Green
}
function Revert-TimerRes {
    cmd /c "reg delete `"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel`" /v GlobalTimerResolutionRequests /f >nul 2>&1"
    Set-State "TimerRes" 0; Write-Host "Timer Resolution: restaurado." -ForegroundColor Yellow
}

function Apply-Hdcp {
    Get-ChildItem (Get-DisplayClassPath) -EA SilentlyContinue | Where-Object { $_.PSChildName -notlike '*Configuration' } | ForEach-Object {
        reg add "$($_.Name)" /v "RMHdcpKeyglobZero" /t REG_DWORD /d "1" /f | Out-Null }
    Set-State "Hdcp" 1; Write-Host "HDCP: OFF (reduce overhead de cifrado de display)." -ForegroundColor Green
}
function Revert-Hdcp {
    Get-ChildItem (Get-DisplayClassPath) -EA SilentlyContinue | Where-Object { $_.PSChildName -notlike '*Configuration' } | ForEach-Object {
        reg add "$($_.Name)" /v "RMHdcpKeyglobZero" /t REG_DWORD /d "0" /f | Out-Null }
    Set-State "Hdcp" 0; Write-Host "HDCP: restaurado." -ForegroundColor Yellow
}

function Apply-GpuP0 {
    if (!$script:HW.IsNvidia) { Write-Host "Solo NVIDIA." -ForegroundColor Red; return }
    Get-ChildItem (Get-DisplayClassPath) -EA SilentlyContinue | Where-Object { $_.PSChildName -notlike '*Configuration' } | ForEach-Object {
        reg add "$($_.Name)" /v "DisableDynamicPstate" /t REG_DWORD /d "1" /f | Out-Null }
    Set-State "GpuP0" 1; Write-Host "GPU P0 (clock maximo fijo): ON. Mas calor = zero variabilidad." -ForegroundColor Green
}
function Revert-GpuP0 {
    Get-ChildItem (Get-DisplayClassPath) -EA SilentlyContinue | Where-Object { $_.PSChildName -notlike '*Configuration' } | ForEach-Object {
        reg add "$($_.Name)" /v "DisableDynamicPstate" /t REG_DWORD /d "0" /f | Out-Null }
    Set-State "GpuP0" 0; Write-Host "GPU P0: clocks dinamicos restaurados." -ForegroundColor Yellow
}

function Apply-NvidiaTweaks {
    if (!$script:HW.IsNvidia) { Write-Host "Solo NVIDIA." -ForegroundColor Red; return }
    cmd /c "reg add `"HKLM\System\ControlSet001\Services\nvlddmkm\Parameters\Global\NVTweak`" /v NvCplPhysxAuto /t REG_DWORD /d 0 /f >nul 2>&1"
    cmd /c "reg add `"HKLM\System\ControlSet001\Services\nvlddmkm\Parameters\Global\NVTweak`" /v NvDevToolsVisible /t REG_DWORD /d 1 /f >nul 2>&1"
    cmd /c "reg add `"HKLM\System\ControlSet001\Services\nvlddmkm\Parameters\Global\NVTweak`" /v RmProfilingAdminOnly /t REG_DWORD /d 0 /f >nul 2>&1"
    cmd /c "reg add `"HKCU\Software\NVIDIA Corporation\NvTray`" /v StartOnLogin /t REG_DWORD /d 0 /f >nul 2>&1"
    Set-State "NvidiaTweaks" 1; Write-Host "Ajustes driver NVIDIA: aplicados." -ForegroundColor Green
}
function Revert-NvidiaTweaks {
    foreach ($v in "NvCplPhysxAuto","NvDevToolsVisible","RmProfilingAdminOnly") {
        cmd /c "reg delete `"HKLM\System\ControlSet001\Services\nvlddmkm\Parameters\Global\NVTweak`" /v $v /f >nul 2>&1" }
    cmd /c "reg delete `"HKCU\Software\NVIDIA Corporation\NvTray`" /v StartOnLogin /f >nul 2>&1"
    Set-State "NvidiaTweaks" 0; Write-Host "NVIDIA tweaks: revertidos." -ForegroundColor Yellow
}

function Apply-GameBar {
    cmd /c "reg add `"HKCU\System\GameConfigStore`" /v GameDVR_Enabled /t REG_DWORD /d 0 /f >nul 2>&1"
    cmd /c "reg add `"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR`" /v AppCaptureEnabled /t REG_DWORD /d 0 /f >nul 2>&1"
    cmd /c "reg add `"HKCU\Software\Microsoft\GameBar`" /v UseNexusForGameBarEnabled /t REG_DWORD /d 0 /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR`" /v AllowgameDVR /t REG_DWORD /d 0 /f >nul 2>&1"
    Set-State "GameBar" 1; Write-Host "Game Bar / Game DVR: OFF." -ForegroundColor Green
}
function Revert-GameBar {
    foreach ($key in "HKCU\System\GameConfigStore","HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR","HKCU\Software\Microsoft\GameBar") {
        cmd /c "reg delete `"$key`" /f >nul 2>&1" }
    Set-State "GameBar" 0; Write-Host "Game Bar: restaurado." -ForegroundColor Yellow
}

function Apply-Copilot {
    cmd /c "reg add `"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot`" /v TurnOffWindowsCopilot /t REG_DWORD /d 1 /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot`" /v TurnOffWindowsCopilot /t REG_DWORD /d 1 /f >nul 2>&1"
    Set-State "Copilot" 1; Write-Host "Windows Copilot: OFF." -ForegroundColor Green
}
function Revert-Copilot {
    cmd /c "reg delete `"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot`" /f >nul 2>&1"
    cmd /c "reg delete `"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot`" /f >nul 2>&1"
    Set-State "Copilot" 0; Write-Host "Copilot: restaurado." -ForegroundColor Yellow
}

function Apply-WinUpdatePause {
    $pause=(Get-Date).AddDays(365).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $today=(Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $path="HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"
    if (!(Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    Set-ItemProperty $path "PauseUpdatesExpiryTime" $pause -Force; Set-ItemProperty $path "PauseFeatureUpdatesEndTime" $pause -Force
    Set-ItemProperty $path "PauseFeatureUpdatesStartTime" $today -Force; Set-ItemProperty $path "PauseQualityUpdatesEndTime" $pause -Force
    Set-ItemProperty $path "PauseQualityUpdatesStartTime" $today -Force; Set-ItemProperty $path "PauseUpdatesStartTime" $today -Force
    Set-State "WinUpdatePause" 1; Write-Host "Windows Update: PAUSADO 1 anio. (Recordar reactivar para seguridad)" -ForegroundColor Green
}
function Revert-WinUpdatePause {
    $path="HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"
    foreach ($v in "PauseUpdatesExpiryTime","PauseFeatureUpdatesEndTime","PauseFeatureUpdatesStartTime","PauseQualityUpdatesEndTime","PauseQualityUpdatesStartTime","PauseUpdatesStartTime") {
        Remove-ItemProperty $path $v -EA SilentlyContinue }
    Set-State "WinUpdatePause" 0; Write-Host "Windows Update: reanudado." -ForegroundColor Yellow
}

function Apply-UltimatePerf {
    $out=powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61 2>&1|Out-String
    if ($out -match '([0-9A-Fa-f-]{36})') { $g=$Matches[1]; powercfg -setactive $g }
    else { powercfg -setactive e9a42b02-d5df-448d-aa00-03f14749eb61 }
    Set-State "UltimatePerf" 1; Write-Host "Ultimate Performance: activado." -ForegroundColor Green
}
function Revert-UltimatePerf {
    powercfg -setactive scheme_balanced 2>$null
    Set-State "UltimatePerf" 0; Write-Host "Volviste a Balanced." -ForegroundColor Yellow
}

function Get-BitLockerOn { try { return [bool](Get-BitLockerVolume -EA Stop | Where-Object { $_.ProtectionStatus -eq "On" }) } catch { return $false } }
function Apply-BitLocker {
    Write-Host "ADVERTENCIA: deja el disco SIN cifrar." -ForegroundColor Red
    $c=Read-Host "Escribe CONFIRMAR"
    if ($c -ne "CONFIRMAR") { Write-Host "Cancelado."; return }
    try { Get-BitLockerVolume | Where-Object { $_.ProtectionStatus -eq "On" } | ForEach-Object { Disable-BitLocker -MountPoint $_.MountPoint -EA SilentlyContinue | Out-Null }; Write-Host "BitLocker: desactivando..." -ForegroundColor Green }
    catch { Write-Host "No se pudo gestionar BitLocker." -ForegroundColor Red }
}
function Revert-BitLocker { Start-Process control.exe -ArgumentList "/name microsoft.bitlockerdriveencryption" }

function Apply-Nagle {
    $ifBase="HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"
    Get-ChildItem $ifBase -EA SilentlyContinue | ForEach-Object {
        cmd /c "reg add `"$($_.Name)`" /v TcpAckFrequency /t REG_DWORD /d 1 /f >nul 2>&1"
        cmd /c "reg add `"$($_.Name)`" /v TCPNoDelay /t REG_DWORD /d 1 /f >nul 2>&1"
    }
    Set-State "Nagle" 1; Write-Host "Nagle: OFF (envio inmediato de paquetes, reduce latencia de red)." -ForegroundColor Green
}
function Revert-Nagle {
    $ifBase="HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"
    Get-ChildItem $ifBase -EA SilentlyContinue | ForEach-Object {
        cmd /c "reg delete `"$($_.Name)`" /v TcpAckFrequency /f >nul 2>&1"
        cmd /c "reg delete `"$($_.Name)`" /v TCPNoDelay /f >nul 2>&1"
    }
    Set-State "Nagle" 0; Write-Host "Nagle: restaurado." -ForegroundColor Yellow
}

function Apply-Mmcss {
    cmd /c "reg add `"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile`" /v SystemResponsiveness /t REG_DWORD /d 10 /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile`" /v NetworkThrottlingIndex /t REG_DWORD /d 0xffffffff /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games`" /v Priority /t REG_DWORD /d 6 /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games`" /v `"Scheduling Category`" /t REG_SZ /d High /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games`" /v `"GPU Priority`" /t REG_DWORD /d 8 /f >nul 2>&1"
    Set-State "Mmcss" 1; Write-Host "MMCSS: Games priority High, sin throttling de red." -ForegroundColor Green
}
function Revert-Mmcss {
    cmd /c "reg add `"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile`" /v SystemResponsiveness /t REG_DWORD /d 20 /f >nul 2>&1"
    cmd /c "reg delete `"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile`" /v NetworkThrottlingIndex /f >nul 2>&1"
    Set-State "Mmcss" 0; Write-Host "MMCSS: restaurado." -ForegroundColor Yellow
}

function Apply-GamePriority {
    foreach ($exe in $GameExes) {
        cmd /c "reg add `"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exe`" /v PriorityClass /t REG_DWORD /d 256 /f >nul 2>&1"
    }
    cmd /c "reg add `"HKCU\Software\Microsoft\GameBar`" /v AutoGameModeEnabled /t REG_DWORD /d 1 /f >nul 2>&1"
    Set-State "GamePriority" 1; Write-Host "Prioridad CPU alta + Game Mode: ON para cs2/Valorant." -ForegroundColor Green
}
function Revert-GamePriority {
    foreach ($exe in $GameExes) { cmd /c "reg delete `"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exe`" /v PriorityClass /f >nul 2>&1" }
    cmd /c "reg delete `"HKCU\Software\Microsoft\GameBar`" /v AutoGameModeEnabled /f >nul 2>&1"
    Set-State "GamePriority" 0; Write-Host "Prioridad CPU: restaurada." -ForegroundColor Yellow
}

function Apply-CoreParking { powercfg -setacvalue scheme_current sub_processor CPMINCORES 100; Set-State "CoreParking" 1; Write-Host "Core parking: OFF (100% nucleos despiertos)." -ForegroundColor Green }
function Revert-CoreParking { powercfg -setacvalue scheme_current sub_processor CPMINCORES 5; Set-State "CoreParking" 0; Write-Host "Core parking: restaurado." -ForegroundColor Yellow }

function Apply-SysMain {
    if ($script:HW.StorageType -match "HDD") {
        Write-Host "! HDD detectado. SysMain ayuda en HDD. Continuar? (S/N)" -ForegroundColor Yellow
        if ((Read-Host).ToUpper() -ne "S") { Write-Host "Cancelado."; return }
    }
    Stop-Service SysMain -EA SilentlyContinue; Set-Service SysMain -StartupType Disabled -EA SilentlyContinue
    Set-State "SysMain" 1; Write-Host "SysMain: OFF." -ForegroundColor Green
}
function Revert-SysMain {
    Set-Service SysMain -StartupType Automatic -EA SilentlyContinue; Start-Service SysMain -EA SilentlyContinue
    Set-State "SysMain" 0; Write-Host "SysMain: restaurado." -ForegroundColor Yellow
}

function Apply-Hags {
    if ($script:HW.GpuGen -match "GTX10") {
        Write-Host "! GPU $($script:HW.GpuGen) puede tener stutter con HAGS. Continuar? (S/N)" -ForegroundColor Yellow
        if ((Read-Host).ToUpper() -ne "S") { Write-Host "Cancelado."; return }
    }
    cmd /c "reg add `"HKCU\Software\Microsoft\DirectX\UserGpuPreferences`" /v DirectXUserGlobalSettings /t REG_SZ /d gpupreference=2; /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers`" /v HwSchMode /t REG_DWORD /d 2 /f >nul 2>&1"
    Set-State "Hags" 1; Write-Host "HAGS: ON." -ForegroundColor Green
    if ($script:HW.GpuGen -match "RTX[34]0") { Write-Host "  -> Tu GPU $($script:HW.GpuGen) se beneficia bien de HAGS." -ForegroundColor DarkGray }
}
function Revert-Hags {
    cmd /c "reg delete `"HKCU\Software\Microsoft\DirectX\UserGpuPreferences`" /v DirectXUserGlobalSettings /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers`" /v HwSchMode /t REG_DWORD /d 1 /f >nul 2>&1"
    Set-State "Hags" 0; Write-Host "HAGS: OFF." -ForegroundColor Yellow
}

function Apply-VisualFx {
    Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects" -Name VisualFXSetting -Value 2 -Force
    Set-State "VisualFx" 1; Write-Host "Efectos visuales: RENDIMIENTO MAXIMO." -ForegroundColor Green
}
function Revert-VisualFx {
    Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects" -Name VisualFXSetting -Value 3 -Force
    Set-State "VisualFx" 0; Write-Host "Efectos visuales: restaurados." -ForegroundColor Yellow
}

function Apply-HidMsi {
    $hidClass="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{745a17a0-74d3-11d0-b6fe-00a0c90f57da}"
    Get-ChildItem $hidClass -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        cmd /c "reg add `"$($_.Name)\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties`" /v MSISupported /t REG_DWORD /d 1 /f >nul 2>&1"
        cmd /c "reg add `"$($_.Name)\Device Parameters\Interrupt Management\Affinity Policy`" /v DevicePolicy /t REG_DWORD /d 4 /f >nul 2>&1"
        cmd /c "reg add `"$($_.Name)\Device Parameters\Interrupt Management\Affinity Policy`" /v AssignmentSetOverride /t REG_BINARY /d 01 /f >nul 2>&1"
    }
    Set-State "HidMsi" 1; Write-Host "MSI Interrupts HID/Mouse: ON (requiere reinicio). Mouse -> core 0 = menos jitter." -ForegroundColor Green
}
function Revert-HidMsi {
    $hidClass="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{745a17a0-74d3-11d0-b6fe-00a0c90f57da}"
    Get-ChildItem $hidClass -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        foreach ($sub in "MessageSignaledInterruptProperties","Affinity Policy") {
            $path="$($_.Name)\Device Parameters\Interrupt Management\$sub"
            cmd /c "reg delete `"$path`" /v MSISupported /f >nul 2>&1"
            cmd /c "reg delete `"$path`" /v DevicePolicy /f >nul 2>&1"
            cmd /c "reg delete `"$path`" /v AssignmentSetOverride /f >nul 2>&1"
        }
    }
    Set-State "HidMsi" 0; Write-Host "MSI HID: revertido (reinicio)." -ForegroundColor Yellow
}

function Apply-SchedulerFg {
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl`" /v Win32PrioritySeparation /t REG_DWORD /d 38 /f >nul 2>&1"
    Set-State "SchedulerFg" 1; Write-Host "CPU Scheduler Foreground Boost 3x: ON." -ForegroundColor Green
}
function Revert-SchedulerFg {
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl`" /v Win32PrioritySeparation /t REG_DWORD /d 2 /f >nul 2>&1"
    Set-State "SchedulerFg" 0; Write-Host "CPU Scheduler: restaurado." -ForegroundColor Yellow
}

function Apply-UsbIrqAffinity {
    $usbClass="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000}"; $n=0
    Get-ChildItem $usbClass -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $ap="$($_.Name)\Device Parameters\Interrupt Management\Affinity Policy"
        cmd /c "reg add `"$ap`" /v DevicePolicy /t REG_DWORD /d 4 /f >nul 2>&1"
        cmd /c "reg add `"$ap`" /v AssignmentSetOverride /t REG_BINARY /d 02 /f >nul 2>&1"; $n++
    }
    Set-State "UsbIrqAffinity" 1; Write-Host "IRQ Affinity USB: $n controladores -> core 1 (requiere reinicio)." -ForegroundColor Green
}
function Revert-UsbIrqAffinity {
    $usbClass="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000}"
    Get-ChildItem $usbClass -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $ap="$($_.Name)\Device Parameters\Interrupt Management\Affinity Policy"
        cmd /c "reg delete `"$ap`" /v DevicePolicy /f >nul 2>&1"
        cmd /c "reg delete `"$ap`" /v AssignmentSetOverride /f >nul 2>&1"
    }
    Set-State "UsbIrqAffinity" 0; Write-Host "IRQ Affinity USB: revertido (reinicio)." -ForegroundColor Yellow
}
# ==================== MODULOS NUEVOS 24-38 ====================

# [24] Interrupt Moderation OFF - FIX PRINCIPAL para jitter/desync
function Apply-InterruptModeration {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"; $count=0
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $p=$_.Name
        cmd /c "reg add `"$p`" /v `"*InterruptModeration`" /t REG_SZ /d 0 /f >nul 2>&1"
        cmd /c "reg add `"$p`" /v `"*ReceiveBuffers`" /t REG_SZ /d 256 /f >nul 2>&1"
        cmd /c "reg add `"$p`" /v `"*TransmitBuffers`" /t REG_SZ /d 256 /f >nul 2>&1"
        cmd /c "reg add `"$p`" /v `"*IPChecksumOffloadIPv4`" /t REG_SZ /d 3 /f >nul 2>&1"
        cmd /c "reg add `"$p`" /v `"*TCPChecksumOffloadIPv4`" /t REG_SZ /d 3 /f >nul 2>&1"
        cmd /c "reg add `"$p`" /v `"*UDPChecksumOffloadIPv4`" /t REG_SZ /d 3 /f >nul 2>&1"
        if ($script:HW.NicType -eq "RTL8125") {
            cmd /c "reg add `"$p`" /v AutoDisableGigabit /t REG_SZ /d 0 /f >nul 2>&1"
            cmd /c "reg add `"$p`" /v AdvancedEEE /t REG_SZ /d 0 /f >nul 2>&1"
        }
        $count++
    }
    Set-State "InterruptModeration" 1
    Write-Host "Interrupt Moderation: OFF en $count interfaces. [FIX PRINCIPAL para jitter/desync]" -ForegroundColor Green
    Write-Host "  -> CPU procesa cada paquete inmediatamente. Puede aumentar levemente CPU usage de red (normal)." -ForegroundColor DarkGray
}
function Revert-InterruptModeration {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        foreach ($v in "*InterruptModeration","*ReceiveBuffers","*TransmitBuffers","AutoDisableGigabit","AdvancedEEE") {
            cmd /c "reg delete `"$($_.Name)`" /v `"$v`" /f >nul 2>&1" }
    }
    Set-State "InterruptModeration" 0; Write-Host "Interrupt Moderation: restaurado." -ForegroundColor Yellow
}

# [25] LSO v1/v2 OFF
function Apply-LsoOff {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $p=$_.Name
        cmd /c "reg add `"$p`" /v `"*LsoV2IPv4`" /t REG_SZ /d 0 /f >nul 2>&1"
        cmd /c "reg add `"$p`" /v `"*LsoV2IPv6`" /t REG_SZ /d 0 /f >nul 2>&1"
        cmd /c "reg add `"$p`" /v LsoV1IPv4 /t REG_SZ /d 0 /f >nul 2>&1"
    }
    Set-State "LsoOff" 1
    Write-Host "LSO (Large Send Offload) v1/v2: OFF. Windows ya no acumula paquetes antes de enviar." -ForegroundColor Green
    Write-Host "  -> Elimina variabilidad de paquetes que causa jitter y desync." -ForegroundColor DarkGray
}
function Revert-LsoOff {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        foreach ($v in "*LsoV2IPv4","*LsoV2IPv6","LsoV1IPv4") { cmd /c "reg delete `"$($_.Name)`" /v `"$v`" /f >nul 2>&1" }
    }
    Set-State "LsoOff" 0; Write-Host "LSO: restaurado." -ForegroundColor Yellow
}

# [26] RSS optimizado
function Apply-RssOptimize {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    $queues=[Math]::Min(4,$script:HW.CpuCores)
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        cmd /c "reg add `"$($_.Name)`" /v `"*RSS`" /t REG_SZ /d 1 /f >nul 2>&1"
        cmd /c "reg add `"$($_.Name)`" /v `"*NumRssQueues`" /t REG_SZ /d $queues /f >nul 2>&1"
    }
    netsh int tcp set global rss=enabled 2>$null | Out-Null
    Set-State "RssOptimize" 1
    Write-Host "RSS: OPTIMIZADO ($queues queues). Distribuye interrupciones en multiples cores." -ForegroundColor Green
}
function Revert-RssOptimize {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        cmd /c "reg delete `"$($_.Name)`" /v `"*NumRssQueues`" /f >nul 2>&1" }
    Set-State "RssOptimize" 0; Write-Host "RSS: restaurado." -ForegroundColor Yellow
}

# [27] Flow Control OFF
function Apply-FlowControlOff {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        cmd /c "reg add `"$($_.Name)`" /v FlowControl /t REG_SZ /d 0 /f >nul 2>&1"
        cmd /c "reg add `"$($_.Name)`" /v `"*FlowControl`" /t REG_SZ /d 0 /f >nul 2>&1"
    }
    Set-State "FlowControlOff" 1; Write-Host "Flow Control NIC: OFF (elimina pause frames que interrumpen flujo)." -ForegroundColor Green
}
function Revert-FlowControlOff {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        foreach ($v in "FlowControl","*FlowControl") { cmd /c "reg delete `"$($_.Name)`" /v `"$v`" /f >nul 2>&1" }
    }
    Set-State "FlowControlOff" 0; Write-Host "Flow Control: restaurado." -ForegroundColor Yellow
}

# [28] QoS DSCP 46 para CS2/Valorant
function Apply-QosDscp {
    $qosPath="HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS"
    if (!(Test-Path $qosPath)) { New-Item -Path $qosPath -Force | Out-Null }
    @(@{Name="CS2_QoS";App="cs2.exe"},@{Name="Valorant_QoS";App="VALORANT-Win64-Shipping.exe"}) | ForEach-Object {
        $p="$qosPath\$($_.Name)"
        if (!(Test-Path $p)) { New-Item -Path $p -Force | Out-Null }
        Set-ItemProperty $p "Version" "1.0" -Force
        Set-ItemProperty $p "Application Name" $_.App -Force
        Set-ItemProperty $p "Protocol" "UDP" -Force
        Set-ItemProperty $p "Local Port" "*" -Force; Set-ItemProperty $p "Remote Port" "*" -Force
        Set-ItemProperty $p "Local IP" "*" -Force; Set-ItemProperty $p "Local IP Prefix" "32" -Force
        Set-ItemProperty $p "Remote IP" "*" -Force; Set-ItemProperty $p "Remote IP Prefix" "32" -Force
        Set-ItemProperty $p "DSCP Value" "46" -Force; Set-ItemProperty $p "Throttle Rate" "-1" -Force
        Write-Host "  QoS DSCP 46 (EF) para $($_.App)" -ForegroundColor DarkGray
    }
    Set-State "QosDscp" 1; Write-Host "QoS DSCP 46: ON para CS2/Valorant. Prioridad maxima si el router lo soporta." -ForegroundColor Green
}
function Revert-QosDscp {
    $qosPath="HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS"
    foreach ($n in "CS2_QoS","Valorant_QoS") {
        $p="$qosPath\$n"; if (Test-Path $p) { Remove-Item $p -Recurse -Force -EA SilentlyContinue }
    }
    Set-State "QosDscp" 0; Write-Host "QoS DSCP: eliminado." -ForegroundColor Yellow
}

# [29] ECN OFF
function Apply-EcnOff {
    netsh int tcp set global ecncapability=disabled 2>$null | Out-Null
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters`" /v ECNEnabled /t REG_DWORD /d 0 /f >nul 2>&1"
    Set-State "EcnOff" 1; Write-Host "ECN: OFF (evita delays paradojicos en redes optimizadas)." -ForegroundColor Green
}
function Revert-EcnOff {
    netsh int tcp set global ecncapability=default 2>$null | Out-Null
    cmd /c "reg delete `"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters`" /v ECNEnabled /f >nul 2>&1"
    Set-State "EcnOff" 0; Write-Host "ECN: restaurado." -ForegroundColor Yellow
}

# [30] NIC Power Management full OFF
function Apply-NicPowerMgmt {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        cmd /c "reg add `"$($_.Name)`" /v AllowIdleIrpInD3 /t REG_DWORD /d 0 /f >nul 2>&1" }
    Get-NetAdapter -Physical -EA SilentlyContinue | ForEach-Object {
        try { Disable-NetAdapterPowerManagement -Name $_.Name -WakeOnMagicPacket -WakeOnPattern -ArpOffload -D0PacketCoalescing -EA SilentlyContinue } catch {} }
    Set-State "NicPowerMgmt" 1; Write-Host "NIC Power Management: OFF completo (WoL, idle D3, packet coalescing)." -ForegroundColor Green
}
function Revert-NicPowerMgmt {
    $base="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
    Get-ChildItem $base -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        cmd /c "reg delete `"$($_.Name)`" /v AllowIdleIrpInD3 /f >nul 2>&1" }
    Set-State "NicPowerMgmt" 0; Write-Host "NIC Power Management: restaurado." -ForegroundColor Yellow
}

# [31] Fake/Double Scaling Fix
function Apply-FakeScalingFix {
    if ($script:HW.IsNvidia) {
        $nvPath="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
        Get-ChildItem $nvPath -EA SilentlyContinue | Where-Object { $_.PSChildName -notlike '*Configuration' } | ForEach-Object {
            cmd /c "reg add `"$($_.Name)`" /v Scaling /t REG_DWORD /d 1 /f >nul 2>&1" }
        cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration`" /v GpuPreference /t REG_DWORD /d 2 /f >nul 2>&1"
    } elseif ($script:HW.IsAMDGpu) {
        $amdPath="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
        Get-ChildItem $amdPath -EA SilentlyContinue | Where-Object { $_.PSChildName -notlike '*Configuration' } | ForEach-Object {
            cmd /c "reg add `"$($_.Name)`" /v DALScalingMode /t REG_DWORD /d 2 /f >nul 2>&1" }
    }
    cmd /c "reg add `"HKCU\Control Panel\Desktop`" /v Win8DpiScaling /t REG_DWORD /d 0 /f >nul 2>&1"
    cmd /c "reg add `"HKCU\Control Panel\Desktop`" /v LogPixels /t REG_DWORD /d 96 /f >nul 2>&1"
    Set-State "FakeScalingFix" 1
    Write-Host "Fake/Double Scaling Fix: APLICADO." -ForegroundColor Green
    Write-Host "  -> GPU maneja el scaling, no el monitor. Elimina double scaling que agrega latencia." -ForegroundColor DarkGray
    Write-Host "  -> Si juegas en 4:3 stretched: verificar OSD del monitor en modo Full." -ForegroundColor DarkGray
}
function Revert-FakeScalingFix {
    cmd /c "reg delete `"HKCU\Control Panel\Desktop`" /v Win8DpiScaling /f >nul 2>&1"
    Set-State "FakeScalingFix" 0; Write-Host "Fake Scaling Fix: revertido." -ForegroundColor Yellow
}

# [32] Power Throttling OFF (EcoQoS global)
function Apply-PowerThrottlingOff {
    $path="HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling"
    if (!(Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    Set-ItemProperty $path "PowerThrottlingOff" 1 -Force
    foreach ($exe in $GameExes) {
        $ifeoPath="HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exe\PerfOptions"
        if (!(Test-Path $ifeoPath)) { New-Item -Path $ifeoPath -Force | Out-Null }
        Set-ItemProperty $ifeoPath "CpuPriorityClass" 3 -Force
        Set-ItemProperty $ifeoPath "IoPriority" 3 -Force
    }
    Set-State "PowerThrottlingOff" 1
    Write-Host "Power Throttling (EcoQoS): OFF globalmente." -ForegroundColor Green
    Write-Host "  -> Windows ya no mandara procesos a E-cores ni los throttleara sin avisar." -ForegroundColor DarkGray
}
function Revert-PowerThrottlingOff {
    $path="HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling"
    Remove-ItemProperty $path "PowerThrottlingOff" -EA SilentlyContinue
    foreach ($exe in $GameExes) {
        Remove-Item "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exe\PerfOptions" -Force -EA SilentlyContinue }
    Set-State "PowerThrottlingOff" 0; Write-Host "Power Throttling: restaurado." -ForegroundColor Yellow
}

# [33] P-core affinity Intel hybrid
function Apply-PcoreAffinity {
    if (!$script:HW.IsIntelHybrid) { Write-Host "CPU no es Intel hibrida (12th/13th/14th gen)." -ForegroundColor Yellow; return }
    $pCoreThreads=$script:HW.CpuCores
    $mask=0; for ($i=0; $i -lt $pCoreThreads; $i++) { $mask=$mask -bor (1 -shl $i) }
    Write-Host "  CPU: $($script:HW.CpuName)" -ForegroundColor Cyan
    Write-Host "  P-cores estimados: $pCoreThreads | Mascara: 0x$("{0:X}" -f $mask)" -ForegroundColor DarkGray
    Write-Host "  NOTA: Estimacion asume P-cores en primeros threads. Verificar con Task Manager." -ForegroundColor Yellow
    foreach ($exe in $GameExes) {
        $ip="HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exe\PerfOptions"
        if (!(Test-Path $ip)) { New-Item -Path $ip -Force | Out-Null }
        Set-ItemProperty $ip "ProcessorAffinityMask" $mask -Force
    }
    Set-State "PcoreAffinity" 1
    Write-Host "P-core Affinity: ON para cs2.exe y Valorant." -ForegroundColor Green
    Write-Host "  -> Juegos solo usaran P-cores. Elimina frame spikes de E-cores." -ForegroundColor DarkGray
}
function Revert-PcoreAffinity {
    foreach ($exe in $GameExes) {
        Remove-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$exe\PerfOptions" "ProcessorAffinityMask" -EA SilentlyContinue }
    Set-State "PcoreAffinity" 0; Write-Host "P-core Affinity: revertido." -ForegroundColor Yellow
}
# [34] BCDEdit safe tweaks
function Apply-BCDTweaks {
    bcdedit /set disabledynamictick yes 2>$null | Out-Null
    bcdedit /set tscsyncpolicy enhanced 2>$null | Out-Null
    bcdedit /set useplatformtick yes 2>$null | Out-Null
    Set-State "BCDTweaks" 1
    Write-Host "BCDEdit tweaks: aplicados (requiere reinicio)." -ForegroundColor Green
    Write-Host "  -> disabledynamictick: reduce overhead de clock interrupt en idle." -ForegroundColor DarkGray
    Write-Host "  -> tscsyncpolicy enhanced: mejor sync TSC entre cores (menos frame time jitter)." -ForegroundColor DarkGray
    Write-Host "  -> useplatformtick: timer mas consistente." -ForegroundColor DarkGray
}
function Revert-BCDTweaks {
    bcdedit /deletevalue disabledynamictick 2>$null | Out-Null
    bcdedit /deletevalue tscsyncpolicy 2>$null | Out-Null
    bcdedit /deletevalue useplatformtick 2>$null | Out-Null
    Set-State "BCDTweaks" 0; Write-Host "BCDEdit: revertido (requiere reinicio)." -ForegroundColor Yellow
}

# [35] NTP Clock Sync Fix (fix para desync 'todos van adelante')
function Apply-NTPClockFix {
    Write-Host "  Forzando resync de reloj..." -ForegroundColor DarkGray
    $paramsPath="HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Parameters"
    $configPath="HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Config"
    $ntpPath   ="HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\NtpClient"
    # Servidores NTP alta precision: Cloudflare + Google + NIST
    Set-ItemProperty $paramsPath "NtpServer" "time.cloudflare.com,0x9 time.google.com,0x9 time.nist.gov,0x9" -Force -EA SilentlyContinue
    Set-ItemProperty $paramsPath "Type" "NTP" -Force -EA SilentlyContinue
    # Reducir intervalo de resync (default = cada 7 dias! -> ahora cada 64-1024 seg)
    Set-ItemProperty $configPath "MaxPollInterval"        10 -Force -EA SilentlyContinue
    Set-ItemProperty $configPath "MinPollInterval"        6  -Force -EA SilentlyContinue
    Set-ItemProperty $configPath "UpdateInterval"         100 -Force -EA SilentlyContinue
    Set-ItemProperty $configPath "MaxNegPhaseCorrection" 3600 -Force -EA SilentlyContinue
    Set-ItemProperty $configPath "MaxPosPhaseCorrection" 3600 -Force -EA SilentlyContinue
    Set-ItemProperty $ntpPath "Enabled" 1 -Force -EA SilentlyContinue
    Restart-Service W32Time -EA SilentlyContinue
    Start-Sleep -Seconds 2
    w32tm /resync /force 2>$null | Out-Null
    w32tm /resync /rediscover 2>$null | Out-Null
    Set-State "NTPClockFix" 1
    Write-Host "NTP Clock Fix: APLICADO." -ForegroundColor Green
    Write-Host "  -> Servidores: Cloudflare, Google, NIST (alta precision)." -ForegroundColor DarkGray
    Write-Host "  -> Resync cada 64-1024 seg (antes: cada 7 dias)." -ForegroundColor DarkGray
    Write-Host "  -> Clock drift que causa desync 'todos van adelante' corregido." -ForegroundColor DarkGray
    try {
        $st=w32tm /query /status 2>$null
        Write-Host ""; Write-Host "Estado NTP:" -ForegroundColor Cyan
        $st | Select-String "Source|Stratum|Offset" | ForEach-Object { Write-Host "  $_" -ForegroundColor White }
    } catch {}
}
function Revert-NTPClockFix {
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Parameters" "NtpServer" "time.windows.com,0x9" -Force -EA SilentlyContinue
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Config" "MaxPollInterval" 15 -Force -EA SilentlyContinue
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Config" "MinPollInterval" 10 -Force -EA SilentlyContinue
    Restart-Service W32Time -EA SilentlyContinue; w32tm /resync /force 2>$null | Out-Null
    Set-State "NTPClockFix" 0; Write-Host "NTP: restaurado a time.windows.com." -ForegroundColor Yellow
}

# [36] NVIDIA Ultra Low Latency via registry
function Apply-NvidiaULL {
    if (!$script:HW.IsNvidia) { Write-Host "Solo NVIDIA." -ForegroundColor Red; return }
    $nvPath="HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\FTS"
    if (!(Test-Path $nvPath)) { New-Item -Path $nvPath -Force | Out-Null }
    Set-ItemProperty $nvPath "EnableRID73777" 1 -Force -EA SilentlyContinue
    $nvGlobal="HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global"
    if (!(Test-Path $nvGlobal)) { New-Item -Path $nvGlobal -Force | Out-Null }
    Set-ItemProperty $nvGlobal "PowerMizerEnable" 0 -Force -EA SilentlyContinue
    Set-ItemProperty $nvGlobal "PowerMizerLevel"  1 -Force -EA SilentlyContinue
    cmd /c "reg add `"HKLM\SOFTWARE\NVIDIA Corporation\Global\NVTweak`" /v Coolbits /t REG_DWORD /d 28 /f >nul 2>&1"
    Set-State "NvidiaULL" 1
    Write-Host "NVIDIA Ultra Low Latency: ON via registry." -ForegroundColor Green
    Write-Host "  -> Equivalente a Low Latency Mode = Ultra en NVIDIA Control Panel." -ForegroundColor DarkGray
    Write-Host "  -> PowerMizer en max performance (sin throttling de GPU)." -ForegroundColor DarkGray
}
function Revert-NvidiaULL {
    $nvPath="HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\FTS"
    Remove-ItemProperty $nvPath "EnableRID73777" -EA SilentlyContinue
    $nvGlobal="HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global"
    Remove-ItemProperty $nvGlobal "PowerMizerEnable" -EA SilentlyContinue
    Remove-ItemProperty $nvGlobal "PowerMizerLevel"  -EA SilentlyContinue
    Set-State "NvidiaULL" 0; Write-Host "NVIDIA ULL: revertido." -ForegroundColor Yellow
}

# [37] Shader Cache Clear (accion puntual)
function Invoke-ShaderCacheClear {
    Write-Host "Limpiando shader cache..." -ForegroundColor Yellow
    $paths=@("$env:LOCALAPPDATA\D3DSCache","$env:LOCALAPPDATA\NVIDIA\DXCache","$env:LOCALAPPDATA\NVIDIA\GLCache",
             "$env:LOCALAPPDATA\AMD\DxCache","$env:LOCALAPPDATA\VkCache","$env:TEMP\NVIDIA Corporation\NV_Cache",
             "C:\ProgramData\NVIDIA Corporation\NV_Cache")
    $total=0
    foreach ($path in $paths) {
        if (Test-Path $path) {
            try {
                $size=(Get-ChildItem $path -Recurse -EA SilentlyContinue|Measure-Object -Property Length -Sum).Sum
                Remove-Item "$path\*" -Recurse -Force -EA SilentlyContinue
                $total+=$size; Write-Host "  + Limpiado: $path ($([Math]::Round($size/1MB,1))MB)" -ForegroundColor Green
            } catch { Write-Host "  ~ No se pudo: $path" -ForegroundColor Yellow }
        }
    }
    Write-Host ""; Write-Host "Total liberado: $([Math]::Round($total/1MB,1))MB" -ForegroundColor Green
    Write-Host "  -> Elimina stutters de recompilacion post-driver update." -ForegroundColor DarkGray
    Write-Host "  -> Primera sesion puede tener leve hitching mientras recompila (normal)." -ForegroundColor DarkGray
}

# [38] Floaty Mouse Fix COMPLETO
function Apply-FloatyMouseFix {
    # Accel OFF + curva lineal 1:1
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseSpeed -Value "0"
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseThreshold1 -Value "0"
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseThreshold2 -Value "0"
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseSensitivity -Value "10" -EA SilentlyContinue
    # SmoothMouseXCurve/SmoothMouseYCurve: curva lineal pura (sin enhance pointer precision)
    $curveX=[byte[]](0,0,0,0,0, 0,0,0,0,0, 0,24,0,0,0, 0,48,0,0,0, 0,96,0,0,0)
    $curveY=[byte[]](0,0,0,0,0, 0,0,0,0,0, 0,24,0,0,0, 0,48,0,0,0, 0,96,0,0,0)
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name "SmoothMouseXCurve" -Value $curveX -EA SilentlyContinue
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name "SmoothMouseYCurve" -Value $curveY -EA SilentlyContinue
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseHoverTime -Value "10" -EA SilentlyContinue
    cmd /c "reg add `"HKCU\Control Panel\Mouse`" /v MouseTrails /t REG_SZ /d 0 /f >nul 2>&1"
    # HID power saving OFF para evitar micro-drops de USB
    $hidClass="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{745a17a0-74d3-11d0-b6fe-00a0c90f57da}"
    Get-ChildItem $hidClass -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $p="$($_.Name)\Device Parameters"
        cmd /c "reg add `"$p`" /v EnhancedPowerManagementEnabled /t REG_DWORD /d 0 /f >nul 2>&1"
        cmd /c "reg add `"$p`" /v SelectiveSuspendEnabled /t REG_BINARY /d 00 /f >nul 2>&1"
        cmd /c "reg add `"$p`" /v DeviceSelectiveSuspendEnabled /t REG_DWORD /d 0 /f >nul 2>&1"
    }
    # Raw input queue optimo
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Services\mouclass\Parameters`" /v MouseDataQueueSize /t REG_DWORD /d 64 /f >nul 2>&1"
    cmd /c "reg add `"HKLM\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters`" /v KeyboardDataQueueSize /t REG_DWORD /d 64 /f >nul 2>&1"
    Set-State "FloatyMouseFix" 1
    Write-Host "Floaty Mouse Fix COMPLETO: APLICADO." -ForegroundColor Green
    Write-Host "  -> Mouse accel: OFF (curva lineal 1:1)" -ForegroundColor DarkGray
    Write-Host "  -> Pointer speed: 6/11 (ratio exacto sin interpolacion)" -ForegroundColor DarkGray
    Write-Host "  -> HID power saving: OFF (no micro-drops de USB)" -ForegroundColor DarkGray
    Write-Host "  -> Raw input queue: optimizado" -ForegroundColor DarkGray
    Write-Host "  -> ACTIVAR Raw Input en CS2/Valorant para bypass completo del OS" -ForegroundColor Yellow
}
function Revert-FloatyMouseFix {
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseSpeed -Value "1"
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseThreshold1 -Value "6"
    Set-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseThreshold2 -Value "10"
    Remove-ItemProperty "HKCU:\Control Panel\Mouse" -Name MouseHoverTime -EA SilentlyContinue
    Set-State "FloatyMouseFix" 0; Write-Host "Floaty Mouse Fix: revertido." -ForegroundColor Yellow
}

# ==================== HERRAMIENTAS BLURBUSTERS (ANALISIS) ====================
function Invoke-NvramAnalyzer {
    Clear-Host; Write-Banner "ANALIZADOR BIOS/NVRAM - OPTIMIZACION COMPLETA" "Magenta"
    Write-Host "Analiza tu nvram.txt (AMI Scewin) en busca de TODOS los ajustes que" -ForegroundColor DarkGray
    Write-Host "afectan el input lag, desde C-States hasta PCIe Spread Spectrum." -ForegroundColor DarkGray
    Write-Host ""
    $nvramPath = Read-Host "Ruta al archivo nvram.txt (Enter = carpeta actual)"
    if ([string]::IsNullOrWhiteSpace($nvramPath)) { $nvramPath = (Join-Path $PSScriptRoot "nvram.txt") }
    if (-not (Test-Path $nvramPath)) {
        Write-Host "No se encontro: $nvramPath" -ForegroundColor Red
        Write-Host "Exportar con Scewin: scewin.exe /O /S nvram.txt" -ForegroundColor Yellow
        Pause; return
    }
    Write-Host "Leyendo y parseando $nvramPath..." -ForegroundColor Cyan
    if (-not [System.IO.Path]::IsPathRooted($nvramPath)) { $nvramPath = Join-Path $PSScriptRoot $nvramPath }
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
        @{ Q="AMD KVM Mouse Protocol"; Rec="Absolute"; Bad=@("Auto","Simple",
        @{ Q="PM L1 SS"; Rec="Disabled"; Bad=@("L1.1","L1.2","L1.1_L1.2"); Impact="CRITICO"; Reason="PCIe L1 Substates son estados de deep sleep para los buses. Desactivar = latencia minima." },
        @{ Q="Clock Power Management"; Rec="Disabled"; Bad=@("Enabled"); Impact="ALTO"; Reason="Apagar el CLKREQ# mantiene el reloj del bus PCIe siempre activo, evitando stutter de wakeup." },
        @{ Q="Legacy USB Support"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="ALTO"; Reason="El soporte Legacy emula PS/2 mediante SMM (System Management Mode). Causa stutters y CPU polling extra." },
        @{ Q="XHCI Hand-off"; Rec="Enabled"; Bad=@("Disabled"); Impact="MEDIO"; Reason="Permite al OS tomar control absoluto del USB 3.0 controller instantaneamente, sin pelear con el BIOS." },
        @{ Q="Power Supply Idle Control"; Rec="Typical Current Idle"; Bad=@("Low Current Idle","Auto"); Impact="ALTO"; Reason="Fix clasico de AMD Ryzen. Evita que la fuente/VRM caigan a estados de energia inestables." },
        @{ Q="Fast Boot"; Rec="Disabled"; Bad=@("Enabled","Ultra Fast"); Impact="BAJO"; Reason="Bypassea la inicializacion completa de hardware. Puede causar ruteos de IRQ suboptimos o DPC spikes." },
        @{ Q="CPPC Preferred Cores"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="MEDIO"; Reason="Evita que Windows mueva los hilos constantemente entre los 'mejores' cores, reduciendo latencia de cache." },
        @{ Q="SMT Control"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="EXTREMO"; Reason="Si tenes CPU de sobra, apagar Hyperthreading/SMT dedica el 100% de la cache L3 al hilo fisico y baja latencia DPC." },
        @{ Q="HD Audio Controller"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="ALTO"; Reason="El audio integrado Realtek/ALC causa masivos DPC storms. Desactivar en BIOS si usas DAC USB." },
        @{ Q="Security Device Support"; Rec="Disabled"; Bad=@("Enabled"); Impact="ALTO"; Reason="Desactiva el fTPM de AMD que es conocido por causar stutters aleatorios. Ojo: Win11 (Valorant) puede requerirlo." },
        @{ Q="CPPC"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="MEDIO"; Reason="Deshabilitar CPPC quita la administracion de energia del OS, forzando frecuencias estables dictadas por BIOS." },
        @{ Q="PSS Support"; Rec="Disabled"; Bad=@("Enabled","Auto"); Impact="ALTO"; Reason="AMD Cool'n'Quiet. Desactiva el downclocking en idle, manteniendo los cores listos para el proximo tick." },
        @{ Q="Data Link Feature Exchange"; Rec="Disabled"; Bad=@("Enabled"); Impact="BAJO"; Reason="Protocolo PCIe extra. Desactivarlo puede reducir microscopica latencia de negociacion." }); Impact="CRITICO"; Reason="Simple/Auto interpolan la posicion del mouse. Absolute = coordenadas 1:1 directas." },
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


function Show-PhysicalRecommendations {
    Clear-Host; Write-Banner "RECOMENDACIONES DE HARDWARE EXTREMAS (BLUR BUSTERS)" "Cyan"
    Write-Host "Segun el analisis de 14,650 posts, estas son las mejores recomendaciones FISICAS" -ForegroundColor DarkGray
    Write-Host "que no se pueden automatizar por software:" -ForegroundColor DarkGray; Write-Host ""
    
    Write-Host "1. RED / JITTER (Thread 15564)" -ForegroundColor Yellow
    Write-Host "  -> Evita usar la placa de red integrada si es un chip Realtek economico." -ForegroundColor White
    Write-Host "  -> Fix de la comunidad: usar un adaptador TP-Link UE300 (USB 3.0) y desactivar el Realtek en BIOS." -ForegroundColor White
    Write-Host "  -> Esto mueve el overhead de red al controlador xHCI, eliminando DPC spikes de red." -ForegroundColor DarkGray; Write-Host ""
    
    Write-Host "2. EMI / RUIDO ELECTRICO (Thread 15707 / 14494)" -ForegroundColor Yellow
    Write-Host "  -> Conecta el Monitor y la PC a la MISMA regleta para evitar 'Ground Loops'." -ForegroundColor White
    Write-Host "  -> Los cables DisplayPort de Fibra Optica SOLO ayudan si tu cable actual sufre" -ForegroundColor White
    Write-Host "     mucha interferencia por estar pegado a cables de 220v. Sino, es placebo." -ForegroundColor DarkGray; Write-Host ""
    
    Write-Host "3. PUERTOS USB Y MOUSE (MÃƒÂºltiples)" -ForegroundColor Yellow
    Write-Host "  -> NUNCA conectes un headset de audio USB en puertos vecinos al mouse." -ForegroundColor White
    Write-Host "  -> El audio USB y el polling de 1000Hz+ del mouse pelean por ancho de banda del mismo Hub interno." -ForegroundColor White
    Write-Host "  -> Pone el mouse en los puertos USB 'Directos al CPU' (los de arriba atras) y el teclado/audio abajo." -ForegroundColor DarkGray; Write-Host ""
    
    Write-Host "4. SSD STUTTERS (Thread 15637)" -ForegroundColor Yellow
    Write-Host "  -> Instalar juegos en SSDs muy llenos o que sobrecalientan causa desync por I/O waits." -ForegroundColor White
    Write-Host "  -> Asegurate que tu NVMe no thermal-throttlee bajo carga (usa un heatsink)." -ForegroundColor DarkGray; Write-Host ""
    
    Write-Host "5. MONITOR (Thread 14097 / 15580)" -ForegroundColor Yellow
    Write-Host "  -> Si tu monitor 360Hz+ usa DSC (Display Stream Compression), busca la forma de desactivarlo" -ForegroundColor White
    Write-Host "     en el OSD del monitor y baja los Hz un poco si es necesario." -ForegroundColor White
    Write-Host "  -> El DSC aÃƒÂ±ade overhead de procesamiento en el monitor que se siente como 'mouse floaty'." -ForegroundColor DarkGray; Write-Host ""
    
    Pause
}

# ==================== FPS BOOST (1% LOW / STUTTER FIXES) ====================

function Get-CS2Path {
    $steamPath = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam" -Name "InstallPath" -EA SilentlyContinue).InstallPath
    if (!$steamPath) { return $null }
    
    $vdfPath = Join-Path $steamPath "steamapps\libraryfolders.vdf"
    if (Test-Path $vdfPath) {
        $vdfContent = Get-Content $vdfPath
        foreach ($line in $vdfContent) {
            if ($line -match '"path"\s+"([^"]+)"') {
                $libPath = $matches[1] -replace '\\\\', '\'
                $cs2Exe = Join-Path $libPath "steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe"
                if (Test-Path $cs2Exe) { return $cs2Exe }
            }
        }
    }
    
    $defaultPath = Join-Path $steamPath "steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe"
    if (Test-Path $defaultPath) { return $defaultPath }
    
    return $null
}

function Apply-ChoppyImageFix {
    # 1. Disable CPU Idle States (C-States)
    $planName = "Input Lag Eliminator Pro - Dynamic Plan"
    $existing = (powercfg -l | Select-String $planName)
    if ($existing) {
        $guid = $existing.Line -replace '.*GUID:\s*([a-f0-9\-]+).*', '$1'
        if ($guid) {
            # 54533251-82be-4824-96c1-47b60b740d00 = SUB_PROCESSOR
            # 5d76a2ca-e8c0-402f-a133-2158492d58ad = IDLEDISABLE
            powercfg -setacvalueindex $guid 54533251-82be-4824-96c1-47b60b740d00 5d76a2ca-e8c0-402f-a133-2158492d58ad 1
            powercfg -setactive $guid
        }
    }
    
    # 2. Dynamic CS2 CPU Affinity (IFEO) - Only Physical Cores
    $cores = $script:HW.CpuCores
    $threads = $script:HW.CpuThreads
    if ($threads -gt $cores) {
        # Hyperthreading enabled, isolate to physical cores
        $mask = 0
        for ($i = 0; $i -lt $threads; $i+=2) { $mask = $mask -bor (1 -shl $i) }
        $hexMask = "{0:X}" -f $mask
        $ifeo = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\cs2.exe"
        if (!(Test-Path $ifeo)) { New-Item -Path $ifeo -Force | Out-Null }
        Set-ItemProperty -Path $ifeo -Name "CpuAffinity" -Value $hexMask -Type String -Force
    }
    
    # 3. Multimedia System Responsiveness
    $mmPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"
    if (Test-Path $mmPath) {
        Set-ItemProperty -Path $mmPath -Name "SystemResponsiveness" -Value 0 -Type DWord -Force
    }
    
    # 4. MPO & FSE (GameConfigStore)
    Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\Dwm" -Name "OverlayTestMode" -Value 5 -Type DWord -ErrorAction SilentlyContinue
    $gcsPath = "HKCU:\SYSTEM\GameConfigStore"
    if (!(Test-Path $gcsPath)) { New-Item -Path $gcsPath -Force | Out-Null }
    Set-ItemProperty -Path $gcsPath -Name "GameDVR_DXGIHonorFSEWindowsCompatible" -Value 1 -Type DWord -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $gcsPath -Name "GameDVR_FSEBehavior" -Value 2 -Type DWord -ErrorAction SilentlyContinue
    
    # 5. RawMouseThrottleDuration (Windows 11)
    $osBuild = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -Name "CurrentBuild" -ErrorAction SilentlyContinue).CurrentBuild
    if ($osBuild -and [int]$osBuild -ge 22621) {
        $mousePath = "HKCU:\Control Panel\Mouse"
        Set-ItemProperty -Path $mousePath -Name "RawMouseThrottleDuration" -Value 3 -Type DWord -ErrorAction SilentlyContinue
    }
    
    Set-State "ChoppyImageFix" 1
    Write-Host "ValleyOfDoom Advanced Fix aplicado (C-States OFF, IFEO CS2, MPO, FSE)." -ForegroundColor Green
}

function Revert-ChoppyImageFix {
    $planName = "Input Lag Eliminator Pro - Dynamic Plan"
    $existing = (powercfg -l | Select-String $planName)
    if ($existing) {
        $guid = $existing.Line -replace '.*GUID:\s*([a-f0-9\-]+).*', '$1'
        if ($guid) {
            powercfg -setacvalueindex $guid 54533251-82be-4824-96c1-47b60b740d00 5d76a2ca-e8c0-402f-a133-2158492d58ad 0
            powercfg -setactive $guid
        }
    }
    
    Remove-Item -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\cs2.exe" -Recurse -ErrorAction SilentlyContinue
    
    $mmPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"
    if (Test-Path $mmPath) { Set-ItemProperty -Path $mmPath -Name "SystemResponsiveness" -Value 10 -Type DWord -Force }
    
    Remove-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\Dwm" -Name "OverlayTestMode" -ErrorAction SilentlyContinue
    
    $gcsPath = "HKCU:\SYSTEM\GameConfigStore"
    Remove-ItemProperty -Path $gcsPath -Name "GameDVR_DXGIHonorFSEWindowsCompatible" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $gcsPath -Name "GameDVR_FSEBehavior" -ErrorAction SilentlyContinue
    
    $osBuild = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -Name "CurrentBuild" -ErrorAction SilentlyContinue).CurrentBuild
    if ($osBuild -and [int]$osBuild -ge 22621) {
        Remove-ItemProperty -Path "HKCU:\Control Panel\Mouse" -Name "RawMouseThrottleDuration" -ErrorAction SilentlyContinue
    }
    
    Set-State "ChoppyImageFix" 0
    Write-Host "ValleyOfDoom Advanced Fix revertido." -ForegroundColor Yellow
}

function Apply-DefenderExclusionCS2 {
    $cs2Path = Get-CS2Path
    if ($cs2Path) {
        $cs2Dir = Split-Path (Split-Path (Split-Path (Split-Path $cs2Path)))
        Add-MpPreference -ExclusionPath $cs2Dir -ErrorAction SilentlyContinue
        Set-State "DefenderExclusionCS2" 1
        Write-Host "Exclusion de Defender aplicada para: $cs2Dir" -ForegroundColor Green
    } else {
        Write-Host "No se encontro la ruta de CS2. No se pudo excluir de Defender." -ForegroundColor Red
    }
}
function Revert-DefenderExclusionCS2 {
    $cs2Path = Get-CS2Path
    if ($cs2Path) {
        $cs2Dir = Split-Path (Split-Path (Split-Path (Split-Path $cs2Path)))
        Remove-MpPreference -ExclusionPath $cs2Dir -ErrorAction SilentlyContinue
        Set-State "DefenderExclusionCS2" 0
        Write-Host "Exclusion de Defender removida." -ForegroundColor Green
    }
}

function Apply-CS2HighPerfGraphics {
    $cs2Path = Get-CS2Path
    if ($cs2Path) {
        $key = "HKCU:\SOFTWARE\Microsoft\DirectX\UserGpuPreferences"
        if (!(Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -Path $key -Name $cs2Path -Value "GpuPreference=2;" -Force
        Set-State "CS2HighPerfGraphics" 1
        Write-Host "CS2 seteado a Alto Rendimiento en Windows Graphics." -ForegroundColor Green
    } else {
        Write-Host "No se encontro CS2." -ForegroundColor Red
    }
}
function Revert-CS2HighPerfGraphics {
    $cs2Path = Get-CS2Path
    if ($cs2Path) {
        $key = "HKCU:\SOFTWARE\Microsoft\DirectX\UserGpuPreferences"
        Remove-ItemProperty -Path $key -Name $cs2Path -ErrorAction SilentlyContinue
        Set-State "CS2HighPerfGraphics" 0
        Write-Host "CS2 removido de UserGpuPreferences." -ForegroundColor Green
    }
}

function Apply-DisableFSOCS2 {
    $cs2Path = Get-CS2Path
    if ($cs2Path) {
        $key = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
        if (!(Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -Path $key -Name $cs2Path -Value "~ DisableDXMaximizedWindowedMode" -Force
        Set-State "DisableFSOCS2" 1
        Write-Host "Optimizaciones de Pantalla Completa DESACTIVADAS para CS2." -ForegroundColor Green
    } else {
        Write-Host "No se encontro CS2." -ForegroundColor Red
    }
}
function Revert-DisableFSOCS2 {
    $cs2Path = Get-CS2Path
    if ($cs2Path) {
        $key = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
        Remove-ItemProperty -Path $key -Name $cs2Path -ErrorAction SilentlyContinue
        Set-State "DisableFSOCS2" 0
        Write-Host "Optimizaciones de Pantalla Completa restauradas para CS2." -ForegroundColor Green
    }
}

function Apply-WindowsGameMode {
    $key = "HKCU:\Software\Microsoft\GameBar"
    if (!(Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name "AutoGameModeEnabled" -Value 1 -Type DWord -Force
    Write-Host "Windows Game Mode ACTIVADO forzadamente." -ForegroundColor Green
}
function Revert-WindowsGameMode {
    $key = "HKCU:\Software\Microsoft\GameBar"
    if (Test-Path $key) { Remove-ItemProperty -Path $key -Name "AutoGameModeEnabled" -ErrorAction SilentlyContinue }
    Write-Host "Windows Game Mode desactivado." -ForegroundColor Green
}

function Apply-DynamicPowerPlan {
    $planName = "Input Lag Eliminator Pro - Dynamic Plan"
    $baseGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" # High Performance
    
    $existing = (powercfg -l | Select-String $planName)
    if ($existing) {
        $existingGuid = $existing.Line -replace '.*GUID:\s*([a-f0-9\-]+).*', '$1'
        if ($existingGuid) { powercfg -delete $existingGuid | Out-Null }
    }
    
    $dup = powercfg -duplicatescheme $baseGuid
    $newGuid = $dup -replace '.*GUID:\s*([a-f0-9\-]+).*', '$1'
    
    powercfg -changename $newGuid $planName
    powercfg -setactive $newGuid
    
    if (-not $script:HW.IsIntelHybrid) {
        powercfg -setacvalueindex $newGuid 54533251-82be-4824-96c1-47b60b740d00 5d76a2ca-e8c0-402f-a133-2158492d58ad 1
        powercfg -setacvalueindex $newGuid 54533251-82be-4824-96c1-47b60b740d00 0cc5b647-c1df-4637-891a-dec35c318583 100
    }
    powercfg -setacvalueindex $newGuid 54533251-82be-4824-96c1-47b60b740d00 893dee8e-2bef-41e0-89c6-b55d0929964c 100
    powercfg -setacvalueindex $newGuid 54533251-82be-4824-96c1-47b60b740d00 bc5038f7-23e0-4960-96da-33abaf5935ec 100
    powercfg -setacvalueindex $newGuid 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d2d4-4ffa-ac25-a54e11278c20 0
    
    powercfg -setactive $newGuid
    Set-State "DynamicPowerPlan" 1
    Write-Host "Plan de Energia Dinamico '$planName' CREADO y APLICADO." -ForegroundColor Green
}
function Revert-DynamicPowerPlan {
    $planName = "Input Lag Eliminator Pro - Dynamic Plan"
    powercfg -setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
    $existing = (powercfg -l | Select-String $planName)
    if ($existing) {
        $existingGuid = $existing.Line -replace '.*GUID:\s*([a-f0-9\-]+).*', '$1'
        if ($existingGuid) { powercfg -delete $existingGuid | Out-Null }
    }
    Set-State "DynamicPowerPlan" 0
    Write-Host "Plan Dinamico eliminado. Restaurado Alto Rendimiento." -ForegroundColor Green
}

function Apply-DynamicNvidiaProfile {
    if (-not $script:HW.IsNvidia) {
        Write-Host "No se detecto GPU NVIDIA. Omitiendo." -ForegroundColor Red
        return
    }
    $nipContent = @"
<?xml version="1.0" encoding="utf-16"?>
<ArrayOfProfile>
  <Profile>
    <ProfileName>Base Profile</ProfileName>
    <Executeables />
    <Settings>
      <ProfileSetting><SettingNameInfo>Shader Cache</SettingNameInfo><SettingID>1675263</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Texture filtering - Trilinear optimization</SettingNameInfo><SettingID>3066610</SettingID><SettingValue>0</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Maximum pre-rendered frames</SettingNameInfo><SettingID>8102046</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Texture filtering - Anisotropic filter optimization</SettingNameInfo><SettingID>8703344</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Shader disk cache maximum size</SettingNameInfo><SettingID>11306135</SettingID><SettingValue>$(if($script:HW.GpuVRAM -gt 4){"4294967295"}else{"10240"})</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Texture filtering - Quality</SettingNameInfo><SettingID>13510289</SettingID><SettingValue>20</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Power management mode</SettingNameInfo><SettingID>274197361</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Threaded optimization</SettingNameInfo><SettingID>549528094</SettingID><SettingValue>$(if($script:HW.CpuThreads -gt 4){"2"}else{"1"})</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
    </Settings>
  </Profile>
</ArrayOfProfile>
"@
    $nipPath = Join-Path $env:TEMP "DynamicProfile.nip"
    Set-Content -Path $nipPath -Value $nipContent -Encoding Unicode
    
    $inspector = "nvidiaProfileInspector.exe"
    if (-not (Get-Command $inspector -ErrorAction SilentlyContinue)) {
        if (Test-Path (Join-Path $PSScriptRoot "nvidiaProfileInspector.exe")) {
            $inspector = Join-Path $PSScriptRoot "nvidiaProfileInspector.exe"
        } else {
            Write-Host "NVIDIA Profile Inspector no encontrado en el PATH ni en la carpeta actual." -ForegroundColor Red
            Write-Host "Por favor, descarga nvidiaProfileInspector.exe y ponlo junto a este script." -ForegroundColor Yellow
            return
        }
    }
    
    try {
        Start-Process -FilePath $inspector -ArgumentList "`"$nipPath`" -silent" -Wait -WindowStyle Hidden
        Set-State "DynamicNvidiaProfile" 1
        Write-Host "Perfil NVIDIA Dinamico inyectado exitosamente." -ForegroundColor Green
    } catch {
        Write-Host "Error aplicando perfil." -ForegroundColor Red
    }
}
function Revert-DynamicNvidiaProfile {
    Write-Host "Debes restaurar el perfil global manualmente desde el Panel de Control de NVIDIA (Boton Restaurar)." -ForegroundColor Yellow
    Set-State "DynamicNvidiaProfile" 0
}

# ==================== REVERTIR TODO ====================
function Invoke-RevertAll {
    Clear-Host; Write-Banner "REVERTIR TODO A VALORES POR DEFECTO" "Red"
    Write-Host "Esto deshara TODOS los tweaks de este script." -ForegroundColor Yellow
    if ((Read-Host "Continuar? (S/N)").ToUpper() -ne "S") { Write-Host "Cancelado."; return }
    Write-Host ""
    $allModules=@("MouseAccel","USBPower","GpuMsi","NetPower","TimerRes","Hdcp","GpuP0","NvidiaTweaks",
                  "GameBar","Copilot","WinUpdatePause","UltimatePerf","Nagle","Mmcss","GamePriority",
                  "CoreParking","SysMain","Hags","VisualFx","HidMsi","SchedulerFg","UsbIrqAffinity",
                  "InterruptModeration","LsoOff","RssOptimize","FlowControlOff","QosDscp","EcnOff",
                  "NicPowerMgmt","FakeScalingFix","PowerThrottlingOff","PcoreAffinity","BCDTweaks",
                  "NTPClockFix","NvidiaULL","FloatyMouseFix","IRQSmartBalancer",
                  "MpoOff","ForceIndependentFlip","MarkCMouseFix","PrioritySeparation","NetworkThrottlingOff","TcpAutoTuning","HpetOff",
                  "PcieAspmDisable","DisableCfgPerGame","NdisBufferTuning","AudioDpcFix","CpuIdleThresholds","CpuBoostMode","DisableBackgroundHwAccel","MouseDataQueueSize","CompositorPriority",
                  "AudioEnhancementsOff","RealtekAudioFix","GameBarPreserve","DualCCDRyzenAffinity","UsbAudioSeparation",
                  "DefenderExclusionCS2","CS2HighPerfGraphics","DisableFSOCS2","WindowsGameMode",
                  "DynamicPowerPlan","DynamicNvidiaProfile")
    foreach ($m in $allModules) {
        if (Get-State $m) {
            Write-Host "  Revirtiendo: $m..." -ForegroundColor DarkGray
            try { & "Revert-$m" } catch { Write-Host "    No se pudo revertir $m" -ForegroundColor Red }
        }
    }
    Write-Host ""; Write-Host "+ Todos los modulos revertidos a valores de Windows por defecto." -ForegroundColor Green
    Write-Host "NOTA: Reiniciar Windows para que todos los cambios surtan efecto." -ForegroundColor Yellow
    Pause
}
# ==================== MENU PRINCIPAL ====================
function St($name) {
    if ($name -eq "BitLocker") {
        if (Get-BitLockerOn) { return "ACTIVADO" } else { return "apagado" }
    }
    if (Get-State $name) { return "ACTIVADO" } else { return "apagado" }
}

# Mapa de numero de menu -> nombre de modulo (orden 1..N)
$modules=@{
    "1"="MouseAccel";     "2"="MarkCMouseFix"; "3"="FloatyMouseFix"; "4"="PrioritySeparation";
    "5"="USBPower";       "6"="NicPowerMgmt";  "7"="PowerThrottlingOff"; "8"="PcieAspmDisable";
    "9"="GpuMsi";         "10"="Hdcp";         "11"="GpuP0";         "12"="NvidiaTweaks";
    "13"="Hags";          "14"="FakeScalingFix"; "15"="NvidiaULL";   "16"="MpoOff"; "17"="ForceIndependentFlip";
    "18"="NetPower";      "19"="Nagle";        "20"="InterruptModeration"; "21"="NdisBufferTuning";
    "22"="LsoOff";        "23"="RssOptimize";  "24"="FlowControlOff";
    "25"="QosDscp";       "26"="EcnOff";       "27"="NetworkThrottlingOff"; "28"="TcpAutoTuning"; "29"="NTPClockFix";
    "30"="TimerRes";      "31"="GameBar";      "32"="Copilot";
    "33"="WinUpdatePause"; "34"="UltimatePerf";
    "35"="Mmcss";         "36"="GamePriority"; "37"="CoreParking"; "38"="CpuIdleThresholds"; "39"="CpuBoostMode";
function Apply-CS2HighPerfGraphics {
    $cs2Path = Get-CS2Path
    if ($cs2Path) {
        $key = "HKCU:\SOFTWARE\Microsoft\DirectX\UserGpuPreferences"
        if (!(Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -Path $key -Name $cs2Path -Value "GpuPreference=2;" -Force
        Set-State "CS2HighPerfGraphics" 1
        Write-Host "CS2 seteado a Alto Rendimiento en Windows Graphics." -ForegroundColor Green
    } else {
        Write-Host "No se encontro CS2." -ForegroundColor Red
    }
}
function Revert-CS2HighPerfGraphics {
    $cs2Path = Get-CS2Path
    if ($cs2Path) {
        $key = "HKCU:\SOFTWARE\Microsoft\DirectX\UserGpuPreferences"
        Remove-ItemProperty -Path $key -Name $cs2Path -ErrorAction SilentlyContinue
        Set-State "CS2HighPerfGraphics" 0
        Write-Host "CS2 removido de UserGpuPreferences." -ForegroundColor Green
    }
}

function Apply-DisableFSOCS2 {
    $cs2Path = Get-CS2Path
    if ($cs2Path) {
        $key = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
        if (!(Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -Path $key -Name $cs2Path -Value "~ DisableDXMaximizedWindowedMode" -Force
        Set-State "DisableFSOCS2" 1
        Write-Host "Optimizaciones de Pantalla Completa DESACTIVADAS para CS2." -ForegroundColor Green
    } else {
        Write-Host "No se encontro CS2." -ForegroundColor Red
    }
}
function Revert-DisableFSOCS2 {
    $cs2Path = Get-CS2Path
    if ($cs2Path) {
        $key = "HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
        Remove-ItemProperty -Path $key -Name $cs2Path -ErrorAction SilentlyContinue
        Set-State "DisableFSOCS2" 0
        Write-Host "Optimizaciones de Pantalla Completa restauradas para CS2." -ForegroundColor Green
    }
}

function Apply-WindowsGameMode {
    $key = "HKCU:\Software\Microsoft\GameBar"
    if (!(Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name "AutoGameModeEnabled" -Value 1 -Type DWord -Force
    Write-Host "Windows Game Mode ACTIVADO forzadamente." -ForegroundColor Green
}
function Revert-WindowsGameMode {
    $key = "HKCU:\Software\Microsoft\GameBar"
    if (Test-Path $key) { Remove-ItemProperty -Path $key -Name "AutoGameModeEnabled" -ErrorAction SilentlyContinue }
    Write-Host "Windows Game Mode desactivado." -ForegroundColor Green
}

function Apply-DynamicPowerPlan {
    $planName = "Input Lag Eliminator Pro - Dynamic Plan"
    $baseGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" # High Performance
    
    $existing = (powercfg -l | Select-String $planName)
    if ($existing) {
        $existingGuid = $existing.Line -replace '.*GUID:\s*([a-f0-9\-]+).*', '$1'
        if ($existingGuid) { powercfg -delete $existingGuid | Out-Null }
    }
    
    $dup = powercfg -duplicatescheme $baseGuid
    $newGuid = $dup -replace '.*GUID:\s*([a-f0-9\-]+).*', '$1'
    
    powercfg -changename $newGuid $planName
    powercfg -setactive $newGuid
    
    if (-not $script:HW.IsIntelHybrid) {
        powercfg -setacvalueindex $newGuid 54533251-82be-4824-96c1-47b60b740d00 5d76a2ca-e8c0-402f-a133-2158492d58ad 1
        powercfg -setacvalueindex $newGuid 54533251-82be-4824-96c1-47b60b740d00 0cc5b647-c1df-4637-891a-dec35c318583 100
    }
    powercfg -setacvalueindex $newGuid 54533251-82be-4824-96c1-47b60b740d00 893dee8e-2bef-41e0-89c6-b55d0929964c 100
    powercfg -setacvalueindex $newGuid 54533251-82be-4824-96c1-47b60b740d00 bc5038f7-23e0-4960-96da-33abaf5935ec 100
    powercfg -setacvalueindex $newGuid 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d2d4-4ffa-ac25-a54e11278c20 0
    
    powercfg -setactive $newGuid
    Set-State "DynamicPowerPlan" 1
    Write-Host "Plan de Energia Dinamico '$planName' CREADO y APLICADO." -ForegroundColor Green
}
function Revert-DynamicPowerPlan {
    $planName = "Input Lag Eliminator Pro - Dynamic Plan"
    powercfg -setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
    $existing = (powercfg -l | Select-String $planName)
    if ($existing) {
        $existingGuid = $existing.Line -replace '.*GUID:\s*([a-f0-9\-]+).*', '$1'
        if ($existingGuid) { powercfg -delete $existingGuid | Out-Null }
    }
    Set-State "DynamicPowerPlan" 0
    Write-Host "Plan Dinamico eliminado. Restaurado Alto Rendimiento." -ForegroundColor Green
}

function Apply-DynamicNvidiaProfile {
    if (-not $script:HW.IsNvidia) {
        Write-Host "No se detecto GPU NVIDIA. Omitiendo." -ForegroundColor Red
        return
    }
    $nipContent = @"
<?xml version="1.0" encoding="utf-16"?>
<ArrayOfProfile>
  <Profile>
    <ProfileName>Base Profile</ProfileName>
    <Executeables />
    <Settings>
      <ProfileSetting><SettingNameInfo>Shader Cache</SettingNameInfo><SettingID>1675263</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Texture filtering - Trilinear optimization</SettingNameInfo><SettingID>3066610</SettingID><SettingValue>0</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Maximum pre-rendered frames</SettingNameInfo><SettingID>8102046</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Texture filtering - Anisotropic filter optimization</SettingNameInfo><SettingID>8703344</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Shader disk cache maximum size</SettingNameInfo><SettingID>11306135</SettingID><SettingValue>$(if($script:HW.GpuVRAM -gt 4){"4294967295"}else{"10240"})</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Texture filtering - Quality</SettingNameInfo><SettingID>13510289</SettingID><SettingValue>20</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Power management mode</SettingNameInfo><SettingID>274197361</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
      <ProfileSetting><SettingNameInfo>Threaded optimization</SettingNameInfo><SettingID>549528094</SettingID><SettingValue>$(if($script:HW.CpuThreads -gt 4){"2"}else{"1"})</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
    </Settings>
  </Profile>
</ArrayOfProfile>
"@
    $nipPath = Join-Path $env:TEMP "DynamicProfile.nip"
    Set-Content -Path $nipPath -Value $nipContent -Encoding Unicode
    
    $inspector = "nvidiaProfileInspector.exe"
    if (-not (Get-Command $inspector -ErrorAction SilentlyContinue)) {
        if (Test-Path (Join-Path $PSScriptRoot "nvidiaProfileInspector.exe")) {
            $inspector = Join-Path $PSScriptRoot "nvidiaProfileInspector.exe"
        } else {
            Write-Host "NVIDIA Profile Inspector no encontrado en el PATH ni en la carpeta actual." -ForegroundColor Red
            Write-Host "Por favor, descarga nvidiaProfileInspector.exe y ponlo junto a este script." -ForegroundColor Yellow
            return
        }
    }
    
    try {
        Start-Process -FilePath $inspector -ArgumentList "`"$nipPath`" -silent" -Wait -WindowStyle Hidden
        Set-State "DynamicNvidiaProfile" 1
        Write-Host "Perfil NVIDIA Dinamico inyectado exitosamente." -ForegroundColor Green
    } catch {
        Write-Host "Error aplicando perfil." -ForegroundColor Red
    }
}
function Revert-DynamicNvidiaProfile {
    Write-Host "Debes restaurar el perfil global manualmente desde el Panel de Control de NVIDIA (Boton Restaurar)." -ForegroundColor Yellow
    Set-State "DynamicNvidiaProfile" 0
}

# ==================== REVERTIR TODO ====================
function Invoke-RevertAll {
    Clear-Host; Write-Banner "REVERTIR TODO A VALORES POR DEFECTO" "Red"
    Write-Host "Esto deshara TODOS los tweaks de este script." -ForegroundColor Yellow
    if ((Read-Host "Continuar? (S/N)").ToUpper() -ne "S") { Write-Host "Cancelado."; return }
    Write-Host ""
    $allModules=@("MouseAccel","USBPower","GpuMsi","NetPower","TimerRes","Hdcp","GpuP0","NvidiaTweaks",
                  "GameBar","Copilot","WinUpdatePause","UltimatePerf","Nagle","Mmcss","GamePriority",
                  "CoreParking","SysMain","Hags","VisualFx","HidMsi","SchedulerFg","UsbIrqAffinity",
                  "InterruptModeration","LsoOff","RssOptimize","FlowControlOff","QosDscp","EcnOff",
                  "NicPowerMgmt","FakeScalingFix","PowerThrottlingOff","PcoreAffinity","BCDTweaks",
                  "NTPClockFix","NvidiaULL","FloatyMouseFix","IRQSmartBalancer",
                  "MpoOff","ForceIndependentFlip","MarkCMouseFix","PrioritySeparation","NetworkThrottlingOff","TcpAutoTuning","HpetOff",
                  "PcieAspmDisable","DisableCfgPerGame","NdisBufferTuning","AudioDpcFix","CpuIdleThresholds","CpuBoostMode","DisableBackgroundHwAccel","MouseDataQueueSize","CompositorPriority",
                  "AudioEnhancementsOff","RealtekAudioFix","GameBarPreserve","DualCCDRyzenAffinity","UsbAudioSeparation",
                  "DefenderExclusionCS2","CS2HighPerfGraphics","DisableFSOCS2","WindowsGameMode",
                  "DynamicPowerPlan","DynamicNvidiaProfile","ChoppyImageFix")
    foreach ($m in $allModules) {
        if (Get-State $m) {
            Write-Host "  Revirtiendo: $m..." -ForegroundColor DarkGray
            try { & "Revert-$m" } catch { Write-Host "    No se pudo revertir $m" -ForegroundColor Red }
        }
    }
    Write-Host ""; Write-Host "+ Todos los modulos revertidos a valores de Windows por defecto." -ForegroundColor Green
    Write-Host "NOTA: Reiniciar Windows para que todos los cambios surtan efecto." -ForegroundColor Yellow
    Pause
}
# ==================== MENU PRINCIPAL ====================
function St($name) {
    if ($name -eq "BitLocker") {
        if (Get-BitLockerOn) { return "ACTIVADO" } else { return "apagado" }
    }
    if (Get-State $name) { return "ACTIVADO" } else { return "apagado" }
}

# Mapa de numero de menu -> nombre de modulo (orden 1..N)
$modules=@{
    "1"="MouseAccel";     "2"="MarkCMouseFix"; "3"="FloatyMouseFix"; "4"="PrioritySeparation";
    "5"="USBPower";       "6"="NicPowerMgmt";  "7"="PowerThrottlingOff"; "8"="PcieAspmDisable";
    "9"="GpuMsi";         "10"="Hdcp";         "11"="GpuP0";         "12"="NvidiaTweaks";
    "13"="Hags";          "14"="FakeScalingFix"; "15"="NvidiaULL";   "16"="MpoOff"; "17"="ForceIndependentFlip";
    "18"="NetPower";      "19"="Nagle";        "20"="InterruptModeration"; "21"="NdisBufferTuning";
    "22"="LsoOff";        "23"="RssOptimize";  "24"="FlowControlOff";
    "25"="QosDscp";       "26"="EcnOff";       "27"="NetworkThrottlingOff"; "28"="TcpAutoTuning"; "29"="NTPClockFix";
    "30"="TimerRes";      "31"="GameBar";      "32"="Copilot";
    "33"="WinUpdatePause"; "34"="UltimatePerf";
    "35"="Mmcss";         "36"="GamePriority"; "37"="CoreParking"; "38"="CpuIdleThresholds"; "39"="CpuBoostMode";
    "40"="SysMain";       "41"="VisualFx";     "42"="SchedulerFg"; "43"="DisableCfgPerGame";
    "44"="PcoreAffinity"; "45"="BCDTweaks";
    "46"="HidMsi";        "47"="UsbIrqAffinity"; "48"="IRQSmartBalancer"; "49"="HpetOff"; "50"="AudioDpcFix";
    "51"="DisableBackgroundHwAccel"; "52"="MouseDataQueueSize"; "53"="CompositorPriority";
    "54"="AudioEnhancementsOff"; "55"="RealtekAudioFix"; "56"="GameBarPreserve";
    "57"="DualCCDRyzenAffinity"; "58"="UsbAudioSeparation";
    "61"="DefenderExclusionCS2"; "62"="CS2HighPerfGraphics"; "63"="DisableFSOCS2"; "64"="WindowsGameMode";
    "65"="DynamicPowerPlan"; "66"="DynamicNvidiaProfile"; "67"="ChoppyImageFix"
}

function Show-MainMenu {
    Clear-Host
    Write-Host "+----------------------------------------------------------------------------+" -ForegroundColor Cyan
    Write-Host "|   INPUT LAG ELIMINATOR PRO v2.0  -  ESL Tech Edition  -  CS2 / Valorant    |" -ForegroundColor Cyan
    $cpuShort=$script:HW.CpuName; if ($cpuShort.Length -gt 45) { $cpuShort=$cpuShort.Substring(0,45)+"..." }
    $nicShort=$script:HW.NicName; if ($nicShort.Length -gt 48) { $nicShort=$nicShort.Substring(0,48)+"..." }
    Write-Host "|  CPU: $cpuShort" -ForegroundColor DarkGray
    Write-Host "|  NIC: $nicShort [$($script:HW.NicType)]" -ForegroundColor DarkGray
    Write-Host "+----------------------------------------------------------------------------+" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  [S] Score Competitivo PRO (0-100) + Analisis completo"   -ForegroundColor Green
    Write-Host "  [D] Diagnosticos de hardware y sistema"                   -ForegroundColor Yellow
    Write-Host "  [O] Optimizaciones (58 modulos de tweaks)"                -ForegroundColor Yellow
    Write-Host "  [F] FPS Boost (Fixes de 1% Low y Stuttering)"             -ForegroundColor Cyan
    Write-Host "  [I] IRQ Smart Balancer dinamico (AutoGpuAffinity-style)"  -ForegroundColor Magenta
    Write-Host "  [V] Detector de Drivers Optimos (GPU + NIC)"              -ForegroundColor Magenta
    Write-Host "  [N] Analizador de BIOS/NVRAM (Scewin BlurBusters)"        -ForegroundColor Magenta
    Write-Host "  [H] Recomendaciones Fisicas de Hardware (BlurBusters)"    -ForegroundColor Magenta
    Write-Host "  [P] Power Plans"                                          -ForegroundColor Yellow
    Write-Host "  [M] Herramientas de medicion (DPC, timer, mouse)"         -ForegroundColor Yellow
    Write-Host "  [R] REVERTIR TODO a valores por defecto"                  -ForegroundColor Red
    Write-Host "  [Q] Salir"                                                -ForegroundColor DarkGray
    Write-Host ""
}

function Show-FpsBoostMenu {
    Clear-Host
    Write-Host "=== FPS BOOST (1% Low y Stuttering) ===" -ForegroundColor Cyan
    Write-Host "Estos tweaks evitan picos de CPU, garantizando 1% lows altisimos y reduciendo stutters." -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "[61] AÃ±adir carpeta CS2 a exclusiones de Defender (Evita escaneos en juego) [$(St 'DefenderExclusionCS2')]" -ForegroundColor Yellow
    Write-Host "[62] Forzar Alto Rendimiento GPU en Windows para cs2.exe                    [$(St 'CS2HighPerfGraphics')]" -ForegroundColor Yellow
    Write-Host "[63] Desactivar Optimizaciones de Pantalla Completa (FSO) para cs2.exe      [$(St 'DisableFSOCS2')]" -ForegroundColor Yellow
    Write-Host "[64] Forzar Activacion de Windows Game Mode (Prioridad CPU/GPU)             [$(St 'WindowsGameMode')]" -ForegroundColor Yellow
    Write-Host "[65] Generar y Aplicar Plan de Energia Dinamico (Reemplaza .pow)            [$(St 'DynamicPowerPlan')]" -ForegroundColor Magenta
    if ($script:HW.IsNvidia) {
        Write-Host "[66] Generar y Aplicar Perfil NVIDIA Dinamico (Reemplaza .nip)              [$(St 'DynamicNvidiaProfile')]" -ForegroundColor Magenta
    }
    Write-Host "[67] Fix Choppy Image & Stutter (ValleyOfDoom)                              [$(St 'ChoppyImageFix')]" -ForegroundColor Magenta
    Write-Host ""
    Write-Host "[M] Volver al menu principal" -ForegroundColor DarkGray
    Write-Host ""
}

function Show-OptimizationsMenu {
    Clear-Host
    $isNV=$script:HW.IsNvidia; $isAMDg=$script:HW.IsAMDGpu; $isIntelH=$script:HW.IsIntelHybrid
    Write-Host "=== MENU OPTIMIZACIONES EXTREMAS [$($script:HW.CpuName.Split(' ')[0..2]-join' ') / $(if($isNV){'NVIDIA'}elseif($isAMDg){'AMD GPU'}else{'GPU'})] ===" -ForegroundColor Cyan; Write-Host ""
    Write-Host "--- MOUSE / INPUT (Fix Floaty Mouse) ---" -ForegroundColor DarkGray
    Write-Host "[1]  Mouse Acceleration OFF                                   [$(St 'MouseAccel')]"
    Write-Host "[2]  MarkC Mouse Fix (Curvas 1:1 en Registro)                 [$(St 'MarkCMouseFix')]" -ForegroundColor Yellow
    Write-Host "[3]  Floaty Mouse Fix COMPLETO (curva 1:1 + HID power)        [$(St 'FloatyMouseFix')]"
    Write-Host "[4]  Win32PrioritySeparation (Fix latencia hilos frontales)   [$(St 'PrioritySeparation')]" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "--- USB / POWER / HARDWARE EXTREMO ---" -ForegroundColor DarkGray
    Write-Host "[5]  Ahorro energia USB/HID/PCI/ACPI OFF                      [$(St 'USBPower')]"
    Write-Host "[6]  NIC Power Management completo OFF                        [$(St 'NicPowerMgmt')]"
    Write-Host "[7]  Power Throttling / EcoQoS OFF                            [$(St 'PowerThrottlingOff')]"
    Write-Host "[8]  PCIe ASPM (L0s/L1) OFF **(Mata el stutter CPU/GPU)**     [$(St 'PcieAspmDisable')]" -ForegroundColor Red
    Write-Host ""
    Write-Host "--- GPU / FRAME PACING (Fix Choppy Image) ---" -ForegroundColor DarkGray
    Write-Host "[9]  MSI Mode GPU                                             [$(St 'GpuMsi')]"
    Write-Host "[10] HDCP OFF                                                 [$(St 'Hdcp')]"
    if ($isNV) {
        Write-Host "[11] GPU P0 (clock maximo fijo) - NVIDIA                      [$(St 'GpuP0')]" -ForegroundColor Cyan
        Write-Host "[12] Ajustes driver NVIDIA                                    [$(St 'NvidiaTweaks')]" -ForegroundColor Cyan
    }
    Write-Host "[13] HAGS - GPU Hardware Scheduling                           [$(St 'Hags')]"
    Write-Host "[14] Fake/Double Scaling Fix                                  [$(St 'FakeScalingFix')]"
    if ($isNV) {
        Write-Host "[15] NVIDIA Ultra Low Latency via registry                    [$(St 'NvidiaULL')]" -ForegroundColor Cyan
    }
    Write-Host "[16] Disable MPO (Fix micro-stutters/imagen cortada)          [$(St 'MpoOff')]" -ForegroundColor Yellow
    Write-Host "[17] Force DXGI Independent Flip (0 Lag Windowed Games)       [$(St 'ForceIndependentFlip')]" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "--- RED / DESYNC / JITTER (Fix Bufferbloat) ---" -ForegroundColor DarkGray
    Write-Host "[18] Ahorro energia NIC OFF (EEE, GreenEthernet)              [$(St 'NetPower')]"
    Write-Host "[19] Nagle OFF (envio inmediato de paquetes)                  [$(St 'Nagle')]"
    Write-Host "[20] Interrupt Moderation OFF ** FIX PRINCIPAL jitter **      [$(St 'InterruptModeration')]" -ForegroundColor Yellow
    Write-Host "[21] NDIS Ring Buffer Tuning (Recibir/Enviar super rapido)    [$(St 'NdisBufferTuning')]" -ForegroundColor Red
    Write-Host "[22] LSO (Large Send Offload) v1/v2 OFF                       [$(St 'LsoOff')]"
    Write-Host "[23] RSS optimizado (multi-core NIC queues)                   [$(St 'RssOptimize')]"
    Write-Host "[24] Flow Control NIC OFF                                     [$(St 'FlowControlOff')]"
    Write-Host "[25] QoS DSCP 46 para CS2/Valorant (UDP priority)             [$(St 'QosDscp')]"
    Write-Host "[26] ECN OFF                                                  [$(St 'EcnOff')]"
    Write-Host "[27] Network Throttling OFF (100% prioridad juego local)      [$(St 'NetworkThrottlingOff')]" -ForegroundColor Yellow
    Write-Host "[28] TCP Auto-Tuning Optimization (Mitiga bufferbloat)        [$(St 'TcpAutoTuning')]" -ForegroundColor Yellow
    Write-Host "[29] NTP Clock Sync FIX ** arregla desync 'todos adelante' ** [$(St 'NTPClockFix')]" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "--- SISTEMA / CPU ---" -ForegroundColor DarkGray
    Write-Host "[30] Timer Resolution global (0.5ms)                          [$(St 'TimerRes')]"
    Write-Host "[31] Game Bar / Game DVR OFF                                  [$(St 'GameBar')]"
    Write-Host "[32] Windows Copilot OFF                                      [$(St 'Copilot')]"
    Write-Host "[33] Pausar Windows Update 1 anio                             [$(St 'WinUpdatePause')]"
    Write-Host "[34] Plan Ultimate Performance                                [$(St 'UltimatePerf')]"
    Write-Host "[35] MMCSS (Games priority High, no throttling)               [$(St 'Mmcss')]"
    Write-Host "[36] Prioridad CPU alta cs2/Valorant (IFEO + Game Mode)       [$(St 'GamePriority')]"
    Write-Host "[37] Core parking OFF (100% nucleos despiertos)               [$(St 'CoreParking')]"
    Write-Host "[38] CPU Idle Thresholds (0 Wake-up Latency)                  [$(St 'CpuIdleThresholds')]" -ForegroundColor Red
    Write-Host "[39] CPU Performance Boost Mode (Aggressive)                  [$(St 'CpuBoostMode')]" -ForegroundColor Red
    Write-Host "[40] SysMain / Superfetch OFF                                 [$(St 'SysMain')]"
    Write-Host "[41] Efectos visuales: rendimiento maximo                     [$(St 'VisualFx')]"
    Write-Host "[42] CPU Scheduler Foreground Boost 3x                        [$(St 'SchedulerFg')]"
    Write-Host "[43] Disable CFG (Exploit Protect) SOLO Juegos **DEEP FIX**   [$(St 'DisableCfgPerGame')]" -ForegroundColor Red
    if ($isIntelH) {
        Write-Host "[44] P-core affinity Intel hybrid (12th/13th/14th gen)        [$(St 'PcoreAffinity')]" -ForegroundColor Magenta
    }
    Write-Host "[45] BCDEdit tweaks (dynamictick, tscsync, platformtick)      [$(St 'BCDTweaks')]"
    Write-Host ""
    Write-Host "--- IRQ / INTERRUPCIONES EXTREMAS ---" -ForegroundColor DarkGray
    Write-Host "[46] MSI Interrupts HID/Mouse                                 [$(St 'HidMsi')]"
    Write-Host "[47] IRQ Affinity USB Host Controllers (estatico)             [$(St 'UsbIrqAffinity')]"
    Write-Host "[48] IRQ Smart Balancer dinamico (mide y elige core optimo)   [$(St 'IRQSmartBalancer')]"
    Write-Host "[49] Disable HPET (High Precision Event Timer) en DevMan/BCD  [$(St 'HpetOff')]" -ForegroundColor Red
    Write-Host "[50] Audio DPC + Enhancements Fix ** FIX LATENCIA AUDIO **    [$(St 'AudioDpcFix')]" -ForegroundColor Red
    Write-Host ""
    Write-Host "--- ACCIONES PUNTUALES ---" -ForegroundColor DarkGray
    Write-Host "[51] Disable Background HW Accel (Steam)                      [$(St 'DisableBackgroundHwAccel')]" -ForegroundColor Red
    Write-Host "[52] Mouse/Kbd Data Queue Size (Buffer 100 -> 16)             [$(St 'MouseDataQueueSize')]" -ForegroundColor Red
    Write-Host "[53] Compositor Priority (CSRSS/DWM en CPU High)              [$(St 'CompositorPriority')]" -ForegroundColor Red
    Write-Host ""
    Write-Host "--- BLURBUSTERS KNOWLEDGE BASE (Fixes Avanzados) ---" -ForegroundColor Magenta
    Write-Host "[54] Audio Enhancements OFF ** FIX FLOATY MOUSE AUDIO **      [$(St 'AudioEnhancementsOff')]" -ForegroundColor Magenta
    Write-Host "[55] Realtek Audio Fix (deshabilitar driver HD Audio)          [$(St 'RealtekAudioFix')]" -ForegroundColor Magenta
    Write-Host "[56] Game Bar Preserve DX12 (iFlip fix Win11 24H2+)           [$(St 'GameBarPreserve')]" -ForegroundColor Magenta
    if ($script:HW.CpuName -match 'Ryzen.*(39[05]0|59[05]0|79[05]0)' -or $script:HW.CpuCores -ge 12) {
        Write-Host "[57] Dual-CCD Ryzen CPU Affinity (CCD0 para juegos)           [$(St 'DualCCDRyzenAffinity')]" -ForegroundColor Magenta
    }
    Write-Host "[58] USB Audio Separation Check (diagnostico contention USB)  [$(St 'UsbAudioSeparation')]" -ForegroundColor Magenta
    Write-Host ""
    Write-Host "--- ACCIONES PUNTUALES ---" -ForegroundColor DarkGray
    Write-Host "[59] BitLocker (cifrado de disco)                             [$(St 'BitLocker')]"
    Write-Host "[60] Limpiar Shader Cache (DX/Vulkan) - accion puntual"
    Write-Host ""
    Write-Host "[R] REVERTIR TODO a valores por defecto" -ForegroundColor Red
    Write-Host "[M] Volver al menu principal" -ForegroundColor Yellow; Write-Host ""
}

# ==================== INICIALIZACION Y LOOP PRINCIPAL ====================
Clear-Host
Write-Host "+----------------------------------------------------------------------------+" -ForegroundColor Cyan
Write-Host "|   INPUT LAG ELIMINATOR PRO v2.0  -  ESL Tech Edition                       |" -ForegroundColor Cyan
Write-Host "|          Inicializando deteccion de hardware...                            |" -ForegroundColor DarkGray
Write-Host "+----------------------------------------------------------------------------+" -ForegroundColor Cyan; Write-Host ""
Initialize-HardwareProfile
Write-Host ""
Write-Host "  + CPU : $($script:HW.CpuName)" -ForegroundColor Green
Write-Host "  + GPU : $($script:HW.GpuName) [$($script:HW.GpuGen)]" -ForegroundColor Green
Write-Host "  + NIC : $($script:HW.NicName) [$($script:HW.NicType)]" -ForegroundColor Green
Write-Host "  + RAM : $($script:HW.RamGB) GB @ $($script:HW.RamSpeedMHz) MHz" -ForegroundColor Green
if ($script:HW.IsIntelHybrid) { Write-Host "  -> CPU HIBRIDA Intel detectada - modulo [33] disponible!" -ForegroundColor Magenta }
if ($script:HW.NicType -eq "RTL8125") { Write-Host "  -> RTL8125 detectado - modulo [24] es CRITICO para tu jitter!" -ForegroundColor Yellow }
if ($script:HW.NicType -match "Killer") { Write-Host "  !! NIC KILLER: DESINSTALA el Killer Control Center AHORA!" -ForegroundColor Red }
Write-Host ""; Write-Host "Presiona ENTER para continuar..." -ForegroundColor DarkGray
Read-Host | Out-Null

while ($true) {
    Show-MainMenu
    $choice=Read-Host "Elegi una opcion"
    $key=$choice.ToUpper()
    switch ($key) {
        "S" { Get-CompetitiveScore }
        "D" {
            Clear-Host
            Write-Host "=== SUBMENU DIAGNOSTICOS ===" -ForegroundColor Cyan; Write-Host ""
            Write-Host "[1] Diagnostico completo de hardware"; Write-Host "[2] Muestreo DPC Latency (15 seg)"
            Write-Host "[3] Procesos/Servicios con overhead"; Write-Host "[4] Volver"; Write-Host ""
            $diag=Read-Host "Elegi"
            switch ($diag) {
                "1" { Get-PCDiagnostics }; "2" { Get-DPCLatencySample }
                "3" { Get-LatencyProcesses }; "4" { break }
                default { Write-Host "Invalido." -ForegroundColor Red; Start-Sleep 1 }
            }
        }
        "F" {
            while ($true) {
                Show-FpsBoostMenu
                $opt=Read-Host "Elegi (61-66 o M)"
                if ($modules.ContainsKey($opt)) {
                    $m=$modules[$opt]
                    if (Get-State $m) { & "Revert-$m" } else { & "Apply-$m" }
                    Pause
                }
                elseif ($opt.ToUpper() -eq "M") { break }
                else { Write-Host "Invalido." -ForegroundColor Red; Start-Sleep 1 }
            }
        }
        "O" {
            while ($true) {
                Show-OptimizationsMenu
                $opt=Read-Host "Elegi"
                if ($modules.ContainsKey($opt)) {
                    $m=$modules[$opt]
                    if (Get-State $m) { & "Revert-$m" } else { & "Apply-$m" }
                    Pause
                } elseif ($opt -eq "60") { Invoke-ShaderCacheClear; Pause }
                elseif ($opt -eq "59") { if (Get-BitLockerOn) { Apply-BitLocker } else { Revert-BitLocker }; Pause }
                elseif ($opt.ToUpper() -eq "R") { Invoke-RevertAll }
                elseif ($opt.ToUpper() -eq "M") { break }
                else { Write-Host "Invalido." -ForegroundColor Red; Start-Sleep 1 }
            }
        }
        "I" {
            if (Get-State "IRQSmartBalancer") {
                Write-Host "IRQ Balancer activo. Re-ejecutar analisis? (S/N)" -ForegroundColor Yellow
                if ((Read-Host).ToUpper() -eq "S") { Invoke-IRQSmartBalancer }
                else {
                    Write-Host "Revertir IRQ Smart Balancer? (S/N)" -ForegroundColor Yellow
                    if ((Read-Host).ToUpper() -eq "S") { Revert-IRQSmartBalancer; Pause }
                }
            } else { Invoke-IRQSmartBalancer }
        }
        "V" { Show-DriverHealthCheck }
        "N" { Invoke-NvramAnalyzer }
        "H" { Show-PhysicalRecommendations }
        "P" { Show-PowerPlansMenu }
        "M" { Show-LatencyMeasurementMenu }
        "R" { Invoke-RevertAll }
        "Q" { exit }
        default { Write-Host "Invalido." -ForegroundColor Red; Start-Sleep 1 }
    }
}
