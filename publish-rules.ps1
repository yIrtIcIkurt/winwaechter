<#
Publishes new rules: bumps "rulesVersion" in rules/rules.json, signs it, verifies against the key
compiled into the app, shows the diff and – only after "j" – commits and pushes to main.
The installed apps fetch rules/rules.json(.sig) from raw.githubusercontent.com (main).
#>
param(
    [string]$KeyPath = 'C:\Users\tahsi\dev\winwaechter-keys\winwaechter-update.key.pem'
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$root = $PSScriptRoot
Set-Location $root

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
if (-not (Test-Path $KeyPath)) { throw "Schlüsseldatei fehlt: $KeyPath" }

function Assert-Ok([string]$what) { if ($LASTEXITCODE -ne 0) { throw "$what fehlgeschlagen (Exit $LASTEXITCODE)." } }

# 1. Only from a clean main branch
$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
Assert-Ok 'git rev-parse'
if ($branch -ne 'main') { throw "Regeln werden nur vom Branch main veröffentlicht (aktuell: $branch)." }
$dirty = & git status --porcelain
Assert-Ok 'git status'
if ($dirty) { throw 'Das Arbeitsverzeichnis ist nicht sauber. Bitte erst committen oder verwerfen.' }

# 2. Bump rulesVersion in the raw text (all other bytes stay as they are; UTF-8 without BOM)
$rulesJson = Join-Path $root 'rules\rules.json'
$rulesSig = Join-Path $root 'rules\rules.json.sig'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$text = [IO.File]::ReadAllText($rulesJson, $utf8)
$m = [regex]::Match($text, '"rulesVersion"\s*:\s*(\d+)')
if (-not $m.Success) { throw 'Kein "rulesVersion" in rules.json gefunden.' }
$old = [int]$m.Groups[1].Value
$next = $old + 1
$g = $m.Groups[1]
$text = $text.Substring(0, $g.Index) + $next + $text.Substring($g.Index + $g.Length)
[IO.File]::WriteAllText($rulesJson, $text, $utf8)
Write-Host "rulesVersion: $old -> $next"

# 3. Sign and verify against the key compiled into the app
$tool = Join-Path $root 'tools\ReleaseTool\ReleaseTool.csproj'
& $dotnet run --project $tool -c Release -- sign-rules $KeyPath $rulesJson
Assert-Ok 'ReleaseTool sign-rules'
& $dotnet run --project $tool -c Release -- verify-rules-embedded $rulesJson $rulesSig
Assert-Ok 'ReleaseTool verify-rules-embedded'

# 4. Show, ask, then commit + push
& git diff --stat
& git status --short
$answer = Read-Host "Regeln Version $next committen und pushen? (j/n)"
if ($answer -ne 'j') {
    Write-Host 'Abgebrochen. Nichts committet; die geänderten Dateien liegen noch im Arbeitsverzeichnis.'
    return
}
& git add rules/rules.json rules/rules.json.sig
Assert-Ok 'git add'
& git commit -m "rules: version $next" -m 'Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>'
Assert-Ok 'git commit'
& git push origin main
Assert-Ok 'git push'
Write-Host "Regeln Version $next veröffentlicht."
