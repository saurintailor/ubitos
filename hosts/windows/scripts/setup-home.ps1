#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time setup for ubit on Windows 11 Home (no Hyper-V manager).
.DESCRIPTION
  1. Turns on Windows Hypervisor Platform, so QEMU runs the VM at near-native speed.
     (It is available on Home; Windows may ask to restart.)
  2. Installs QEMU with winget if it is not there yet.
  The ubit app then picks the QEMU backend automatically.
#>
$ErrorActionPreference = 'Stop'
$restart = $false

# 1. Windows Hypervisor Platform
$whp = Get-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform
if ($whp.State -ne 'Enabled') {
    Write-Host 'Turning on Windows Hypervisor Platform...'
    $result = Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -All -NoRestart
    $restart = $result.RestartNeeded
} else {
    Write-Host 'Windows Hypervisor Platform is on.'
}

# 2. QEMU
$qemu = Join-Path $env:ProgramFiles 'qemu\qemu-system-x86_64.exe'
if (-not (Test-Path $qemu)) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw 'winget is not available. Install QEMU from https://qemu.weilnetz.de/w64/ and run this again.'
    }
    Write-Host 'Installing QEMU...'
    winget install --id SoftwareFreedomConservancy.QEMU --exact --silent --accept-package-agreements --accept-source-agreements
    if (-not (Test-Path $qemu)) {
        throw "QEMU did not install to $qemu. Install it manually, or set UBIT_QEMU_DIR to its folder."
    }
}
& $qemu --version | Select-Object -First 1

foreach ($fw in 'edk2-x86_64-code.fd', 'edk2-i386-vars.fd') {
    if (-not (Test-Path (Join-Path $env:ProgramFiles "qemu\share\$fw"))) {
        Write-Warning "QEMU's UEFI firmware file $fw is missing; reinstall QEMU."
    }
}

if ($restart) {
    Write-Host 'Setup done. Restart Windows once, then open the ubit app.'
} else {
    Write-Host 'Setup done. Open the ubit app.'
}
