<#
Builds a WinWaechter release locally: version, tests, publish, installer, signature, self-check.
-Publish additionally creates the GitHub release (only after approval!).
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$KeyPath = 'C:\Users\tahsi\dev\winwaechter-keys\winwaechter-update.key.pem',
    [string]$PubKeyPath = 'C:\Users\tahsi\dev\winwaechter-keys\winwaechter-update.pub.pem',
    [switch]$Publish,
    [string]$IsccPath = ''
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Ungültige Version '$Version' (erwartet x.y.z)." }
$root = $PSScriptRoot
Set-Location $root

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
if (-not $IsccPath) {
    $IsccPath = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    if (-not (Test-Path $IsccPath)) { $IsccPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
}
if (-not (Test-Path $IsccPath)) { throw 'ISCC.exe nicht gefunden. Mit -IsccPath angeben.' }
foreach ($p in $KeyPath, $PubKeyPath) { if (-not (Test-Path $p)) { throw "Schlüsseldatei fehlt: $p" } }

function Assert-Ok([string]$what) { if ($LASTEXITCODE -ne 0) { throw "$what fehlgeschlagen (Exit $LASTEXITCODE)." } }

# 1. Version in Directory.Build.props
$props = Join-Path $root 'Directory.Build.props'
$text = [IO.File]::ReadAllText($props)
$new = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
if ($text -notmatch '<Version>[^<]*</Version>') { throw 'Kein <Version> in Directory.Build.props gefunden.' }
if ($new -ne $text) { [IO.File]::WriteAllText($props, $new) }

# 2. Tests
& $dotnet test (Join-Path $root 'WinWaechter.sln') -c Release
Assert-Ok 'dotnet test'

# 2b. Regeln signieren (before publish, so the shipped rules.json matches the committed, signed one)
$tool = Join-Path $root 'tools\ReleaseTool\ReleaseTool.csproj'
$rulesJson = Join-Path $root 'rules\rules.json'
$rulesSig = Join-Path $root 'rules\rules.json.sig'
& $dotnet run --project $tool -c Release -- sign-rules $KeyPath $rulesJson
Assert-Ok 'ReleaseTool sign-rules'
& $dotnet run --project $tool -c Release -- verify-rules-embedded $rulesJson $rulesSig
Assert-Ok 'ReleaseTool verify-rules-embedded'
Write-Host 'rules.json.sig aktualisiert – bitte mit committen.'

# 3. Publish
$publishDir = Join-Path $root "publish\$Version"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $dotnet publish (Join-Path $root 'src\WinWaechter.App') -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o $publishDir
Assert-Ok 'dotnet publish'

# 4. Installer
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$setup = Join-Path $dist "WinWaechter-Setup-$Version.exe"
& $IsccPath "/DAppVersion=$Version" "/DSourceDir=$publishDir" (Join-Path $root 'installer\winwaechter.iss')
Assert-Ok 'ISCC'
if (-not (Test-Path $setup)) { throw "Installer fehlt: $setup" }

# 5. Sign
& $dotnet run --project $tool -c Release -- sign $KeyPath $Version $setup $dist
Assert-Ok 'ReleaseTool sign'

# 6. Self-check as if installed version were 0.0.0, against the public key compiled into the app
#    (catches a signing key that does not match UpdateKeys.PublicKeyPem)
$sums = Join-Path $dist 'SHA256SUMS'
$sig = Join-Path $dist 'SHA256SUMS.sig'
& $dotnet run --project $tool -c Release -- verify-embedded '0.0.0' $sums $sig $setup
Assert-Ok 'ReleaseTool verify-embedded'

Write-Host "Fertig: $setup"

# 7. Optional GitHub release
if ($Publish) {
    $notes = Join-Path $env:TEMP "winwaechter-notes-$Version.md"
    $changelog = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'))
    $m = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($Version))\s*\r?\n(.*?)(?=^## |\z)")
    if (-not $m.Success) { throw "CHANGELOG.md hat keinen Abschnitt für Version $Version." }
    $body = $m.Groups[1].Value.Trim()
    [IO.File]::WriteAllText($notes, $body)
    gh release create "v$Version" $setup $sums $sig --repo yIrtIcIkurt/winwaechter --title "WinWächter $Version" --notes-file $notes
    Assert-Ok 'gh release create'
}
