$ErrorActionPreference = 'Stop'

# Firewall: allow TTS web port from Tailscale + home LAN only
if (-not (Get-NetFirewallRule -DisplayName 'TTS Web 8791' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'TTS Web 8791' -Direction Inbound -Protocol TCP -LocalPort 8791 `
        -RemoteAddress '100.64.0.0/10','192.168.7.0/24','192.168.137.0/24' -Action Allow | Out-Null
    Write-Output 'firewall rule added'
} else {
    Write-Output 'firewall rule exists'
}

# Scheduled task: run the streaming TTS web server (S4U, boot trigger, auto-restart)
$action = New-ScheduledTaskAction -Execute 'cmd.exe' `
    -Argument '/c cd /d C:\Tools\tts-web && venv\Scripts\python.exe tts_web.py >> server.log 2>&1' `
    -WorkingDirectory 'C:\Tools\tts-web'
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'ddPC\proseidon' -LogonType S4U -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable
Register-ScheduledTask -TaskName 'TtsWeb' -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName 'TtsWeb'
Write-Output 'task registered and started'
