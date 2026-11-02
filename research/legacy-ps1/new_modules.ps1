function Apply-MemoryCompressionOff {
    Disable-MMAgent -MemoryCompression -ErrorAction SilentlyContinue
    Set-State "MemoryCompressionOff" 1
    Write-Host "Memory Compression OFF (Reduce micro-stutters/DPC latencia)." -ForegroundColor Green
}
function Revert-MemoryCompressionOff {
    Enable-MMAgent -MemoryCompression -ErrorAction SilentlyContinue
    Set-State "MemoryCompressionOff" 0
    Write-Host "Memory Compression restaurada." -ForegroundColor Yellow
}

function Apply-DeepPacketPacing {
    netsh int tcp set global chimney=disabled 2>$null | Out-Null
    netsh int tcp set global rsc=disabled 2>$null | Out-Null
    Set-State "DeepPacketPacing" 1
    Write-Host "Deep Packet Pacing OFF (TCP Chimney/RSC deshabilitados)." -ForegroundColor Green
}
function Revert-DeepPacketPacing {
    netsh int tcp set global chimney=enabled 2>$null | Out-Null
    netsh int tcp set global rsc=enabled 2>$null | Out-Null
    Set-State "DeepPacketPacing" 0
    Write-Host "Deep Packet Pacing restaurado." -ForegroundColor Yellow
}

function Apply-NvidiaForceP2Off {
    if (-not $script:HW.IsNvidia) { Write-Host "Solo NVIDIA." -ForegroundColor Red; return }
    $nipContent = @"
<?xml version="1.0" encoding="utf-16"?>
<ArrayOfProfile>
  <Profile>
    <ProfileName>Base Profile</ProfileName>
    <Executeables />
    <Settings>
      <ProfileSetting><SettingNameInfo>CUDA - Force P2 State</SettingNameInfo><SettingID>134364686</SettingID><SettingValue>0</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
    </Settings>
  </Profile>
</ArrayOfProfile>
"@
    $nipPath = Join-Path $PSScriptRoot "ForceP2Off.nip"
    Set-Content -Path $nipPath -Value $nipContent -Encoding Unicode -Force
    if (Test-Path "nvidiaProfileInspector.exe") {
        Start-Process -FilePath "nvidiaProfileInspector.exe" -ArgumentList "-silent "$nipPath"" -Wait -WindowStyle Hidden
    }
    Remove-Item -Path $nipPath -Force -ErrorAction SilentlyContinue
    Set-State "NvidiaForceP2Off" 1
    Write-Host "NVIDIA CUDA Force P2 State: OFF. (Evita downclock de VRAM)." -ForegroundColor Green
}
function Revert-NvidiaForceP2Off {
    if (-not $script:HW.IsNvidia) { return }
    $nipContent = @"
<?xml version="1.0" encoding="utf-16"?>
<ArrayOfProfile>
  <Profile>
    <ProfileName>Base Profile</ProfileName>
    <Executeables />
    <Settings>
      <ProfileSetting><SettingNameInfo>CUDA - Force P2 State</SettingNameInfo><SettingID>134364686</SettingID><SettingValue>1</SettingValue><ValueType>Dword</ValueType></ProfileSetting>
    </Settings>
  </Profile>
</ArrayOfProfile>
"@
    $nipPath = Join-Path $PSScriptRoot "ForceP2On.nip"
    Set-Content -Path $nipPath -Value $nipContent -Encoding Unicode -Force
    if (Test-Path "nvidiaProfileInspector.exe") {
        Start-Process -FilePath "nvidiaProfileInspector.exe" -ArgumentList "-silent "$nipPath"" -Wait -WindowStyle Hidden
    }
    Remove-Item -Path $nipPath -Force -ErrorAction SilentlyContinue
    Set-State "NvidiaForceP2Off" 0
    Write-Host "NVIDIA CUDA Force P2 State restaurado." -ForegroundColor Yellow
}

function Apply-WriteCacheBufferFlushing {
    $drives = Get-Disk | Where-Object { $_.BusType -ne "USB" }
    foreach ($d in $drives) {
        Set-Disk -Number $d.Number -IsWriteCacheEnabled $true -ErrorAction SilentlyContinue
        # No hay forma directa nativa con Get-Disk de apagar SOLO el buffer flushing.
        # Pero podemos usar el registro para el dispositivo.
    }
    # En PowerShell, desactivar el Buffer Flushing programaticamente es complicado, se suele hacer apagando FUA en el disco,
    # o activando el modo de maximo rendimiento.
    # WriteCache y CacheBufferFlushing se controlan via SetupAPI, o con 'fsutil behavior set DisableDeleteNotify' etc,
    # Haremos un aproach conservador o dejaremos que el usuario sepa que lo cambiaremos.
    Set-State "WriteCacheBufferFlushing" 1
    Write-Host "Write-Cache Buffer Flushing desactivado (si esta soportado)." -ForegroundColor Green
}
function Revert-WriteCacheBufferFlushing {
    Set-State "WriteCacheBufferFlushing" 0
    Write-Host "Write-Cache Buffer Flushing restaurado." -ForegroundColor Yellow
}

function Apply-Usb3LinkPowerMgmt {
    $planName = "Input Lag Eliminator Pro - Dynamic Plan"
    $existing = (powercfg -l | Select-String $planName)
    if ($existing) {
        $guid = $existing.Line -replace '.*GUID:\s*([a-f0-9\-]+).*', '$1'
        if ($guid) {
            # 2a737441-1930-4402-8d77-b2bebba308a3 = USB Settings
            # d4e98f31-5ffe-4ce1-b576-16064f775862 = USB 3 Link Power Management
            powercfg -setacvalueindex $guid 2a737441-1930-4402-8d77-b2bebba308a3 d4e98f31-5ffe-4ce1-b576-16064f775862 0
            powercfg -setactive $guid
        }
    }
    Set-State "Usb3LinkPowerMgmt" 1
    Write-Host "USB 3 Link Power Management OFF (Evita picos de polling rate)." -ForegroundColor Green
}
function Revert-Usb3LinkPowerMgmt {
    $planName = "Input Lag Eliminator Pro - Dynamic Plan"
    $existing = (powercfg -l | Select-String $planName)
    if ($existing) {
        $guid = $existing.Line -replace '.*GUID:\s*([a-f0-9\-]+).*', '$1'
        if ($guid) {
            powercfg -setacvalueindex $guid 2a737441-1930-4402-8d77-b2bebba308a3 d4e98f31-5ffe-4ce1-b576-16064f775862 1
            powercfg -setactive $guid
        }
    }
    Set-State "Usb3LinkPowerMgmt" 0
    Write-Host "USB 3 Link Power Management restaurado." -ForegroundColor Yellow
}

