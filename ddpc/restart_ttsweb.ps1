Stop-ScheduledTask -TaskName 'TtsWeb' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
$procs = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like '*tts_web*' }
foreach ($p in $procs) {
    Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    Write-Output ("killed old python pid " + $p.ProcessId)
}
Start-Sleep -Seconds 2
Start-ScheduledTask -TaskName 'TtsWeb'
Start-Sleep -Seconds 6
$now = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like '*tts_web*' }
foreach ($p in $now) {
    Write-Output ("running python pid " + $p.ProcessId + " created " + $p.CreationDate)
}
$listen = Get-NetTCPConnection -LocalPort 8791 -State Listen -ErrorAction SilentlyContinue
foreach ($l in $listen) {
    Write-Output ("port 8791 owned by pid " + $l.OwningProcess)
}
Write-Output ("health: " + (curl.exe -s --max-time 10 http://127.0.0.1:8791/health))
Write-Output ("tts2 probe: " + (curl.exe -s -o NUL -w "%{http_code} bytes=%{size_download}" --max-time 30 "http://127.0.0.1:8791/tts2?lang=ja&text=test"))
