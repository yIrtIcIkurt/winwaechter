# Entfernt die Test-Einträge aus create-fixtures.ps1 wieder.
$ErrorActionPreference = 'Continue'
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WW-Test' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name 'WW-Test' -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'Ping' -TaskPath '\WW-Test\' -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'WW-Test') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:TEMP 'WW-Test') -Recurse -Force -ErrorAction SilentlyContinue
"Test-Einträge entfernt."
