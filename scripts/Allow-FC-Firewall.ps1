#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [int]$Port = 45832
)

$ErrorActionPreference = 'Stop'
$ruleName = 'FC LAN Sync (TCP 45832)'

Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

New-NetFirewallRule `
    -DisplayName $ruleName `
    -Description 'Allows FC peer-to-peer folder synchronization from the local office subnet only.' `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -RemoteAddress LocalSubnet `
    -Profile Private,Domain | Out-Null

Write-Host "Created Windows Firewall rule '$ruleName'." -ForegroundColor Green
Write-Host "Inbound TCP $Port is allowed only from LocalSubnet on Private/Domain profiles."
