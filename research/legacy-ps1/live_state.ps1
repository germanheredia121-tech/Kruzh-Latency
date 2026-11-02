function Get-State($name) {
    switch ($name) {
        'MpoOff' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'ForceIndependentFlip' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'MarkCMouseFix' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'PrioritySeparation' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'NetworkThrottlingOff' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'TcpAutoTuning' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'HpetOff' { return $false # Check de bcdedit }
        'PcieAspmDisable' { return $false # Check de powercfg complejo }
        'DisableCfgPerGame' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'NdisBufferTuning' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'AudioDpcFix' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'CpuIdleThresholds' { return $false # Check de powercfg complejo }
        'CpuBoostMode' { return $false # Check de powercfg complejo }
        'DisableBackgroundHwAccel' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'MouseDataQueueSize' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'CompositorPriority' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'AudioEnhancementsOff' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'RealtekAudioFix' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'GameBarPreserve' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'DualCCDRyzenAffinity' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'UsbAudioSeparation' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'GamingPowerPlan' { return $false # Check de powercfg complejo }
        'MouseAccel' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'USBPower' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'GpuMsi' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'NetPower' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'TimerRes' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'Hdcp' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'GpuP0' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'NvidiaTweaks' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'GameBar' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'Copilot' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'WinUpdatePause' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'UltimatePerf' { return $false # Check de powercfg complejo }
        'BitLocker' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'Nagle' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'Mmcss' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'GamePriority' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'CoreParking' { return $false # Check de powercfg complejo }
        'Hags' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'VisualFx' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'HidMsi' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'SchedulerFg' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'UsbIrqAffinity' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'InterruptModeration' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'LsoOff' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'RssOptimize' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'FlowControlOff' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'QosDscp' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'EcnOff' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'NicPowerMgmt' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'FakeScalingFix' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'PowerThrottlingOff' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'PcoreAffinity' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'BCDTweaks' { return $false # Check de bcdedit }
        'NTPClockFix' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'NvidiaULL' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        'FloatyMouseFix' { return (Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1 }
        default { return ((Get-ItemProperty -Path $StateKey -Name $name -ErrorAction SilentlyContinue).$name -eq 1) }
    }
}
