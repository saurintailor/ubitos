#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time host setup for ubit on Windows 11 Pro, Enterprise or Education.
.DESCRIPTION
  1. Checks that Hyper-V is enabled (offers to enable it; needs a restart).
  2. Adds the current user to "Hyper-V Administrators" so the app runs without elevation.
  3. Registers the ubit agent's Hyper-V socket service (vsock port 7710 in the guest).
  Sign out and back in afterwards so the group membership applies.
#>
param([string]$User = "$env:USERDOMAIN\$env:USERNAME")
$ErrorActionPreference = 'Stop'

# 1. Hyper-V
$feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All
if ($feature.State -ne 'Enabled') {
    Write-Host 'Hyper-V is not enabled. Enabling it now; Windows will ask to restart.'
    Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All
    Write-Host 'Restart Windows, then run this script again.'
    exit 0
}

# 2. Hyper-V Administrators (well-known SID, so this works in any language)
$group = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-578')).Translate(
    [System.Security.Principal.NTAccount]).Value.Split('\')[-1]
if (-not (Get-LocalGroupMember -Group $group -ErrorAction SilentlyContinue | Where-Object Name -eq $User)) {
    Add-LocalGroupMember -Group $group -Member $User
    Write-Host "Added $User to '$group'. Sign out and back in for it to apply."
}

# 3. Hyper-V socket service for the agent: vsock template GUID with port 7710 (0x1E1E).
$serviceId = '00001e1e-facb-11e6-bd58-64006a7986d3'
$path = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\$serviceId"
if (-not (Test-Path $path)) {
    New-Item -Path $path -Force | Out-Null
}
Set-ItemProperty -Path $path -Name ElementName -Value 'ubit agent'
Write-Host "Registered Hyper-V socket service $serviceId (ubit agent)."
Write-Host 'Setup complete.'
