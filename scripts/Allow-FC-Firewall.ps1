#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [int]$SyncPort = 45832,
    [int]$DiscoveryPort = 45833
)

$ErrorActionPreference = 'Stop'
$tcpRule = 'FC LAN Sync (TCP 45832)'
$udpRule = 'FC LAN Discovery (UDP 45833)'

Get-NetFirewallRule -DisplayName $tcpRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName $udpRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule

New-NetFirewallRule `
    -DisplayName $tcpRule `
    -Description 'Allows FC peer-to-peer folder synchronization from the local office subnet only.' `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $SyncPort `
    -RemoteAddress LocalSubnet `
    -Profile Private,Domain | Out-Null

New-NetFirewallRule `
    -DisplayName $udpRule `
    -Description 'Allows paired FC devices to rediscover each other after DHCP/IP address changes.' `
    -Direction Inbound `
    -Action Allow `
    -Protocol UDP `
    -LocalPort $DiscoveryPort `
    -RemoteAddress LocalSubnet `
    -Profile Private,Domain | Out-Null

Write-Host "Created FC Windows Firewall rules." -ForegroundColor Green
Write-Host "TCP $SyncPort: synchronization, LocalSubnet only."
Write-Host "UDP $DiscoveryPort: LAN peer rediscovery, LocalSubnet only."
