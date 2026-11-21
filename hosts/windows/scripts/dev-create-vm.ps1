#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Creates the same VM the ubit app creates, using the Hyper-V PowerShell module.
  Use it to test the image if the app's WMI path misbehaves.
.EXAMPLE
  .\dev-create-vm.ps1 -Vhdx C:\Users\me\Downloads\ubit-windows-x86_64-0.1.0.vhdx
#>
param(
    [Parameter(Mandatory)][string]$Vhdx,
    [string]$Name = 'ubit',
    [UInt64]$DiskSize = 64GB,
    [Int64]$Memory = 4GB,
    [int]$Processors = 2
)
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:LOCALAPPDATA 'ubit\vm'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$disk = Join-Path $dir 'ubit.vhdx'
if ((Resolve-Path $Vhdx).Path -ne $disk) { Copy-Item $Vhdx $disk -Force }
Resize-VHD -Path $disk -SizeBytes $DiskSize

if (Get-VM -Name $Name -ErrorAction SilentlyContinue) { Remove-VM -Name $Name -Force }
New-VM -Name $Name -Generation 2 -MemoryStartupBytes $Memory -VHDPath $disk -SwitchName 'Default Switch' | Out-Null
Set-VM -Name $Name -ProcessorCount $Processors -StaticMemory -EnhancedSessionTransportType HvSocket -AutomaticCheckpointsEnabled $false
Set-VMFirmware -VMName $Name -EnableSecureBoot Off -FirstBootDevice (Get-VMHardDiskDrive -VMName $Name)
Start-VM -Name $Name
vmconnect.exe localhost $Name
