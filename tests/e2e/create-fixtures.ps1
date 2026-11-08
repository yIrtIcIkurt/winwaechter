# Legt harmlose Test-Einträge an, damit WinWächter etwas zum Optimieren findet.
# Läuft ohne Adminrechte. Wieder entfernen mit remove-fixtures.ps1.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'WW-Test'
New-Item -ItemType Directory -Force $dir | Out-Null

# Unsignierte Programmdatei: Kopie von whoami.exe mit einem geänderten Byte (Signatur dadurch ungültig).
$exe = Join-Path $dir 'ww-test.exe'
$bytes = [IO.File]::ReadAllBytes("$env:SystemRoot\System32\whoami.exe")
$bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 0xFF
[IO.File]::WriteAllBytes($exe, $bytes)

# Autostart-Eintrag (nur für dich, HKCU).
New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WW-Test' -Value "`"$exe`"" -PropertyType String -Force | Out-Null

# Versteckte geplante Aufgabe, die nie läuft (Termin im Jahr 2099).
$action = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -Once -At ([datetime]'2099-01-01T03:00:00')
$settings = New-ScheduledTaskSettingsSet -Hidden
Register-ScheduledTask -TaskName 'Ping' -TaskPath '\WW-Test\' -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null

# Drei alte Temp-Dateien (5 Tage alt).
$tmp = Join-Path $env:TEMP 'WW-Test'
New-Item -ItemType Directory -Force $tmp | Out-Null
1..3 | ForEach-Object {
    $f = Join-Path $tmp "alt-$_.tmp"
    Set-Content -Path $f -Value "WinWaechter Testdatei $_" -Encoding UTF8
    (Get-Item $f).LastWriteTime = (Get-Date).AddDays(-5)
}
"Test-Einträge angelegt: Autostart 'WW-Test', Aufgabe '\WW-Test\Ping', 3 Dateien in $tmp"
