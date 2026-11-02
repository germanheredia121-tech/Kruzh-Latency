$ErrorActionPreference = "Stop"
try {
    $sig='[DllImport("ntdll.dll")] public static extern int NtQueryTimerResolution(out uint a, out uint b, out uint c);'
    Add-Type -MemberDefinition $sig -Name TimerQ2 -Namespace Win32
    $mn=0u;$mx=0u;$cur=0u
    [Win32.TimerQ2]::NtQueryTimerResolution([ref]$mn,[ref]$mx,[ref]$cur) | Out-Null
    Write-Host "Current: $($cur / 10000.0)"
} catch {
    Write-Host "Error: $_"
}
