#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory=$true)][System.Net.IPAddress]$ClientIP,
    [ValidateRange(1024,65533)][int]$BasePort=5050
)
$ErrorActionPreference='Stop'
if($ClientIP.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw 'Use the zone client IPv4 address.' }
$serverExe=Join-Path $PSScriptRoot 'AccessControlServer\bin\Debug\net10.0-windows\AccessControlServer.exe'
if(!(Test-Path -LiteralPath $serverExe)) { throw 'Run Start_Server.cmd first.' }
$name="AccessControl-12.0-$ClientIP-$BasePort"
Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Name $name -DisplayName $name -Direction Inbound -Action Allow -Protocol TCP `
    -LocalPort @($BasePort,($BasePort+1),($BasePort+2)) -RemoteAddress $ClientIP.IPAddressToString `
    -Program $serverExe -Profile Any | Out-Null
Write-Host 'Allowed only the specified zone client on the three application ports. PostgreSQL port 5432 was not opened.'
