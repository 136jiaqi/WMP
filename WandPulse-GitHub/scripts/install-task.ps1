param([string]$TaskName='WandPulseGitHub')
$ErrorActionPreference='Stop'
$scriptFile=Join-Path $PSScriptRoot 'run-local.ps1'
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$scriptFile+'"') -WorkingDirectory (Split-Path $PSScriptRoot -Parent)
$regular=New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddDays(1).AddMinutes(15) -RepetitionInterval (New-TimeSpan -Hours 2)
$beforeDaily=New-ScheduledTaskTrigger -Daily -At '09:30'
$logon=New-ScheduledTaskTrigger -AtLogOn -User ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
$principal=New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 50) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($regular,$beforeDaily,$logon) -Principal $principal -Settings $settings -Description 'Collect Wand locally and publish validated snapshots to GitHub' -Force | Out-Null
Write-Output "Installed $TaskName. Requires Windows session signed in. Task times follow Windows timezone."
