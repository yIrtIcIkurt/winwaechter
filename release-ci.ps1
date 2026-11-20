<#
Release from the GitHub build (.github/workflows/build.yml), whose app files and setup SignPath signs.
The private update key never leaves this computer:

  1. .\release-ci.ps1 -Version x.y.z -PrepareRules
     signs rules\rules.json with the local key. Commit rules.json.sig, then push main and the tag vx.y.z.
  2. Approve both signing requests in SignPath and wait until the tag's workflow run is green.
  3. .\release-ci.ps1 -Version x.y.z [-Publish]
     downloads that run's setup, checks its Authenticode signature, signs SHA256SUMS with the local
     update key and checks it the way the app does. -Publish also creates the GitHub release
     (only after approval!).
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$PrepareRules,
    [switch]$Publish,
    [switch]$AllowUnsigned,
    [string]$KeyPath = 'C:\Users\tahsi\dev\winwaechter-keys\winwaechter-update.key.pem',
    [string]$Repo = 'yIrtIcIkurt/winwaechter'
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Ungültige Version '$Version' (erwartet x.y.z)." }
$root = $PSScriptRoot
Set-Location $root
if (-not (Test-Path $KeyPath)) { throw "Schlüsseldatei fehlt: $KeyPath" }

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$tool = Join-Path $root 'tools\ReleaseTool\ReleaseTool.csproj'
function Assert-Ok([string]$what) { if ($LASTEXITCODE -ne 0) { throw "$what fehlgeschlagen (Exit $LASTEXITCODE)." } }

$props = [IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'))
if ($props -notmatch "<Version>$([regex]::Escape($Version))</Version>") { throw "Directory.Build.props hat nicht die Version $Version." }

if ($PrepareRules) {
    $rulesJson = Join-Path $root 'rules\rules.json'
    & $dotnet run --project $tool -c Release -- sign-rules $KeyPath $rulesJson
    Assert-Ok 'ReleaseTool sign-rules'
    & $dotnet run --project $tool -c Release -- verify-rules-embedded $rulesJson (Join-Path $root 'rules\rules.json.sig')
    Assert-Ok 'ReleaseTool verify-rules-embedded'
    Write-Host "rules.json.sig aktualisiert. Jetzt committen, dann main und den Tag v$Version pushen."
    return
}

# the tag's successful workflow run, built from exactly the tagged commit
$tagSha = (git rev-parse "v$Version^{commit}").Trim()
Assert-Ok "Tag v$Version"
$runs = gh run list --repo $Repo --workflow build.yml --branch "v$Version" --status success --limit 5 --json databaseId,headSha | ConvertFrom-Json
Assert-Ok 'gh run list'
$run = $runs | Where-Object { $_.headSha -eq $tagSha } | Select-Object -First 1
if (-not $run) { throw "Kein erfolgreicher Build für v$Version ($tagSha) auf GitHub gefunden." }

$dir = Join-Path $root "dist\ci-$Version"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
gh run download $run.databaseId --repo $Repo -n "WinWaechter-Setup-$Version" -D $dir
Assert-Ok 'gh run download'
$setup = Join-Path $dir "WinWaechter-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Setup fehlt im heruntergeladenen Build: $setup" }

$sig = Get-AuthenticodeSignature $setup
if ($sig.Status -ne 'Valid') {
    if (-not $AllowUnsigned) { throw "Das Setup ist nicht gültig signiert ($($sig.Status)). Mit -AllowUnsigned trotzdem fortfahren." }
    Write-Warning "Das Setup ist nicht signiert ($($sig.Status))."
} else {
    Write-Host "Signiert von: $($sig.SignerCertificate.Subject)"
}

# update checksums with the local key, then the self-check against the key compiled into the app
& $dotnet run --project $tool -c Release -- sign $KeyPath $Version $setup $dir
Assert-Ok 'ReleaseTool sign'
$sums = Join-Path $dir 'SHA256SUMS'
$sumsSig = Join-Path $dir 'SHA256SUMS.sig'
& $dotnet run --project $tool -c Release -- verify-embedded '0.0.0' $sums $sumsSig $setup
Assert-Ok 'ReleaseTool verify-embedded'
Write-Host "Fertig: $setup"

if ($Publish) {
    $notes = Join-Path $env:TEMP "winwaechter-notes-$Version.md"
    $changelog = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'))
    $m = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($Version))\s*\r?\n(.*?)(?=^## |\z)")
    if (-not $m.Success) { throw "CHANGELOG.md hat keinen Abschnitt für Version $Version." }
    [IO.File]::WriteAllText($notes, $m.Groups[1].Value.Trim())
    gh release create "v$Version" $setup $sums $sumsSig --repo $Repo --title "WinWächter $Version" --notes-file $notes --verify-tag
    Assert-Ok 'gh release create'
}
