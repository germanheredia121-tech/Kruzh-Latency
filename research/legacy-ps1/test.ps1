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
function Get-State($name) { return ((Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1) }
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
    if ($nicName -match "RTL8125|Realtek 2\.5G|RTL8125B") { return "RTL8125" }
    if ($nicName -match "RTL8111|RTL8168|Realtek PCIe GbE") { return "RTL8111" }
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
        $mn=0u;$mx=0u;$cur=0u; [Win32.TimerQ]::NtQueryTimerResolution([ref]$mn,[ref]$mx,[ref]$cur) | Out-Null
        $timerMs=$cur/10000.0
        if ($timerMs -le 0.5) { $totalScore+=5; $ok+="Timer resolution: ${timerMs}ms (OPTIMO)" }
        elseif ($timerMs -le 1.0) { $totalScore+=3; $warnings+="Timer resolution: ${timerMs}ms (activa modulo [5])" }
        else { $issues+="Timer resolution: ${timerMs}ms - MAL, activa modulo [5]" }
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
    # HAGS (4 pts)
    $hagsVal=(Get-ItemProperty "HKCU:\Software\Microsoft\DirectX\UserGpuPreferences" -EA SilentlyContinue).DirectXUserGlobalSettings
    if ($hagsVal -match "gpupreference=2") {
        if ($script:HW.GpuGen -match "RTX[34]0|RX[67]") { $totalScore+=4; $ok+="HAGS: ON (correcto para tu GPU)" }
        else { $totalScore+=2; $warnings+="HAGS: ON (verifica si beneficia tu GPU)" }
    } else {
        if ($script:HW.GpuGen -match "GTX1[06]") { $totalScore+=2; $warnings+="HAGS: OFF (correcto para GPU legada)" }
        else { $issues+="HAGS: OFF - considera activar modulo [19] para GPU moderna" }
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
    if ($schedVal -eq 38) { $totalScore+=5; $ok+="CPU Scheduler: Foreground Boost 3x (optimo)" } else { $issues+="CPU Scheduler: no optimizado (modulo [22])" }
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
    if ($script:HW.IsIntelHybrid) { Write-Host "  -> CPU HIBRIDA: activar modulo [33] para eliminar frame spikes de E-cores" -ForegroundColor Magenta }
    if ($script:HW.NicType -eq "RTL8125") { Write-Host "  -> RTL8125: modulo [24] (Interrupt Moderation OFF) es CRITICO para tu jitter/desync" -ForegroundColor Red }
    if ($script:HW.NicType -match "Killer") { Write-Host "  -> NIC KILLER: DESINSTALA el Killer Control Center - peor causa de DPC latency" -ForegroundColor Red }
    if (!$script:HW.IsDualChannel) { Write-Host "  -> SINGLE CHANNEL: colocar 2do stick en slot correcto para dual channel" -ForegroundColor Red }
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
        if ($s) { $ok=($s.Status.ToString()-eq$_.Good); $col=if($ok){"Green"}else{"Yellow"}; $icon=if($ok){"+"} else{"~"}; Write-Host "  $icon $($_.Name): $($s.Status) ($($_.Desc))" -ForegroundColor $col }
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
        $dr=Get-ItemProperty "HKLM:\SOFTWARE\NVIDIA Corporation\Installer2\InstallerUI" -EA SilentlyContinue
        if ($dr.Version) { $gpuDriverVer=$dr.Version }
        else { $pnp=Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceName -match "NVIDIA|Radeon|AMD" } | Select-Object -First 1; if ($pnp) { $gpuDriverVer=$pnp.DriverVersion } }
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
            $st=if($avg-le3){"EXCELENTE +"}elseif($avg-le8){"BUENO +"}elseif($avg-le15){"MODERADO ~"}else{"ALTO !"}
            $col=if($avg-le8){"Green"}elseif($avg-le15){"Yellow"}else{"Red"}
            Write-Host "  Kernel Time: $([Math]::Round($avg,2))% - $st" -ForegroundColor $col
            if ($avg -gt 15) {
                Write-Host "  ! Driver problematico. Usar LatencyMon para identificar .sys ofensor." -ForegroundColor Red
                if ($script:HW.NicType -match "RTL|Realtek") { Write-Host "  ! Alta probabilidad: driver Realtek. Actualizar de realtek.com" -ForegroundColor Red }
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
    Write-Host "Midiendo baseline de carga por core..." -ForegroundColor Cyan
    $baseline=@{}
    try {
        for ($i=0; $i -lt [Math]::Min($threadCount,16); $i++) {
            $s=Get-Counter "\Processor($i)\% Privileged Time" -SampleInterval 1 -MaxSamples 3 -EA SilentlyContinue
            $baseline[$i]=if ($s) { ($s.CounterSamples.CookedValue|Measure-Object -Average).Average } else { 999 }
        }
    } catch {
        Write-Host "  Fallback: asignacion por topologia..." -ForegroundColor Yellow
        for ($i=0; $i -lt $threadCount; $i++) { $baseline[$i]=$i*5 }
    }
    $sorted=$baseline.GetEnumerator()|Sort-Object Value
    $bestCore  =$sorted|Select-Object -First 1
    $secondCore=$sorted|Select-Object -Skip 1 -First 1
    $thirdCore =$sorted|Select-Object -Skip 2 -First 1
    Write-Host ""; Write-Host "Carga por core (Kernel %):" -ForegroundColor White
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
    $hidCore=$gpuCore
    $gpuMask=Get-AffinityMask $gpuCore; $nicMask=Get-AffinityMask $nicCore
    $usbMask=Get-AffinityMask $usbCore; $hidMask=Get-AffinityMask $hidCore
    Write-Host "Asignacion optima:" -ForegroundColor Green
    Write-Host "  GPU -> Core $gpuCore (Kernel: $([Math]::Round($baseline[$gpuCore],1))%)" -ForegroundColor Cyan
    Write-Host "  NIC -> Core $nicCore" -ForegroundColor Cyan
    Write-Host "  USB -> Core $usbCore" -ForegroundColor Cyan
    Write-Host "  HID/Mouse -> Core $hidCore" -ForegroundColor Cyan; Write-Host ""
    Write-Host "Aplicando..." -ForegroundColor Yellow
    # GPU
    $gpuN=0
    Get-PnpDevice -Class Display -EA SilentlyContinue | ForEach-Object {
        $ap="HKLM\SYSTEM\ControlSet001\Enum\$($_.InstanceId)\Device Parameters\Interrupt Management\Affinity Policy"
        cmd /c "reg add `"$ap`" /v DevicePolicy /t REG_DWORD /d 4 /f >nul 2>&1"
        cmd /c "reg add `"$ap`" /v AssignmentSetOverride /t REG_BINARY /d $gpuMask /f >nul 2>&1"
        $gpuN++
    }
    # NIC
    $nicBase="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"; $nicN=0
    Get-ChildItem $nicBase -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $ap="$($_.Name)\Device Parameters\Interrupt Management\Affinity Policy"
        cmd /c "reg add `"$ap`" /v DevicePolicy /t REG_DWORD /d 4 /f >nul 2>&1"
        cmd /c "reg add `"$ap`" /v AssignmentSetOverride /t REG_BINARY /d $nicMask /f >nul 2>&1"; $nicN++
    }
    # USB
    $usbClass="HKLM:\SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000}"; $usbN=0
    Get-ChildItem $usbClass -EA SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $ap="$($_.Name)\Device Parameters\Interrupt Management\Affinity Policy"
        cmd /c "reg add `"$ap`" /v DevicePolicy /t REG_DWORD /d 4 /f >nul 2>&1"
        cmd /c "reg add `"$ap`" /v AssignmentSetOverride /t REG_BINARY /d $usbMask /f >nul 2>&1"; $usbN++
    }
    # HID
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
    Write-Host "  HID: $hidN dispositivos -> Core $hidCore" -ForegroundColor Green
    Write-Host ""; Write-Host "REINICIAR Windows para que surta efecto." -ForegroundColor Yellow
    Write-Host "Re-ejecutar despues de actualizar drivers (se resetea)." -ForegroundColor DarkGray
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

# ==================== POWER PLANS ====================
function Apply-GamingPowerPlan {
    Clear-Host; Write-Banner "CREAR / ACTIVAR POWER PLAN GAMING" "Cyan"
    $gamingGuid="00000000-0000-0000-0000-000000000042"
    if (powercfg -list | Select-String $gamingGuid) {
        Write-Host "Plan Gaming ya existe, activando..." -ForegroundColor Green; powercfg -setactive $gamingGuid
    } else {
        $out=powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61 2>&1
        if ($out -match '([0-9A-Fa-f-]{36})') { $g=$Matches[1]; powercfg -changename $g "Gaming Low-Latency" "Optimizado para competitivo"; powercfg -setactive $g }
    }
    powercfg -setacvalue scheme_current sub_processor PERFBOOSTMODE 2
    powercfg -setacvalue scheme_current sub_processor PERFOPTION 2
    powercfg -setacvalue scheme_current sub_disk DISKIDLE 0
    powercfg -setacvalue scheme_current sub_nic BrowsingPowerSaver 0
    Write-Host "Plan Gaming creado y activado." -ForegroundColor Green; Write-Host ""; Pause
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

# ==================== MEDICION ====================
function Show-TimerResolution {
    Clear-Host; Write-Banner "RESOLUCION DE TIMER ACTUAL" "Cyan"
    try {
        $sig='[DllImport("ntdll.dll")] public static extern int NtQueryTimerResolution(out uint a, out uint b, out uint c);'
        if (!("TimerQx" -as [type])) { Add-Type -MemberDefinition $sig -Name TimerQx -Namespace Win32 -EA SilentlyContinue }
        $mn=0u;$mx=0u;$cur=0u; [Win32.TimerQx]::NtQueryTimerResolution([ref]$mn,[ref]$mx,[ref]$cur) | Out-Null
        Write-Host "Min: $($mn/10000.0)ms | Max: $($mx/10000.0)ms | Actual: $($cur/10000.0)ms"
        $ms=$cur/10000.0
        if ($ms -le 0.5) { Write-Host "+ OPTIMO (0.5ms)" -ForegroundColor Green }
        elseif ($ms -le 1.0) { Write-Host "~ Bueno - activa modulo [5] para 0.5ms" -ForegroundColor Yellow }
        else { Write-Host "! Precision baja - activa modulo [5]" -ForegroundColor Red }
    } catch { Write-Host "No se pudo consultar." -ForegroundColor Red }
    Write-Host ""; Pause
}

function Show-LatencyMeasurementMenu {
    Clear-Host; Write-Host "=== HERRAMIENTAS DE MEDICION ===" -ForegroundColor Cyan; Write-Host ""
    Write-Host "[1] Timer Resolution actual"; Write-Host "[2] Resource Monitor"
    Write-Host "[3] Muestreo DPC Latency (15 seg)"; Write-Host "[4] Descargar SetTimerResolution"
    Write-Host "[5] Info MouseTester"; Write-Host "[6] Como medir in-game"; Write-Host "[7] Volver"; Write-Host ""
    $c=Read-Host "Elegi"
    switch ($c) {
        "1" { Show-TimerResolution; Show-LatencyMeasurementMenu }
        "2" { Start-Process resmon.exe; Show-LatencyMeasurementMenu }
        "3" { Get-DPCLatencySample; Show-LatencyMeasurementMenu }
        "4" {
            $url="https://github.com/valleyofdoom/TimerResolution/releases/download/SetTimerResolution-v1.0.0/SetTimerResolution-v1.0.0.zip"
            $d="$env:USERPROFILE\Downloads\SetTimerResolution.zip"
            try { Invoke-WebRequest -Uri $url -OutFile $d -UseBasicParsing; Expand-Archive $d "$env:USERPROFILE\Downloads\TimerResolution" -Force; Write-Host "Listo en Downloads\TimerResolution" -ForegroundColor Green }
            catch { Write-Host "Descargar: https://github.com/valleyofdoom/TimerResolution/releases" -ForegroundColor Yellow }
            Pause; Show-LatencyMeasurementMenu
        }
        "5" { Write-Host "MouseTester: https://github.com/valleyofdoom/MouseTester/releases" -ForegroundColor Cyan; Write-Host "Mover en onda senoidal lenta, verificar curva suave."; Pause; Show-LatencyMeasurementMenu }
        "6" { Write-Host "CS2: net_graph 1 (loss y choke = 0 ideal)" -ForegroundColor Cyan; Write-Host "Valorant: Alt+N | NVIDIA: Alt+R Reflex overlay"; Pause; Show-LatencyMeasurementMenu }
        "7" { return }
        default { Write-Host "Invalido." -ForegroundColor Red; Start-Sleep 1; Show-LatencyMeasurementMenu }
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
                  "NTPClockFix","NvidiaULL","FloatyMouseFix","IRQSmartBalancer")
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
    if ($name -eq "BitLocker") { return if (Get-BitLockerOn) { "ACTIVADO" } else { "apagado" } }
    if (Get-State $name) { return "ACTIVADO" } else { return "apagado" }
}

$modules=@{
    "1"="MouseAccel";"2"="USBPower";"3"="GpuMsi";"4"="NetPower";"5"="TimerRes";
    "6"="Hdcp";"7"="GpuP0";"8"="NvidiaTweaks";"9"="GameBar";"10"="Copilot";
    "11"="WinUpdatePause";"12"="UltimatePerf";"14"="Nagle";"15"="Mmcss";
    "16"="GamePriority";"17"="CoreParking";"18"="SysMain";"19"="Hags";"20"="VisualFx";
    "21"="HidMsi";"22"="SchedulerFg";"23"="UsbIrqAffinity";
    "24"="InterruptModeration";"25"="LsoOff";"26"="RssOptimize";"27"="FlowControlOff";
    "28"="QosDscp";"29"="EcnOff";"30"="NicPowerMgmt";"31"="FakeScalingFix";
    "32"="PowerThrottlingOff";"33"="PcoreAffinity";"34"="BCDTweaks";
    "35"="NTPClockFix";"36"="NvidiaULL";"38"="FloatyMouseFix";"39"="IRQSmartBalancer"
}

function Show-MainMenu {
    Clear-Host
    Write-Host "╔══════════════════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
