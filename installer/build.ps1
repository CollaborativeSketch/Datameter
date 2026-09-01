# Publishes Datameter for every supported architecture and builds one installer each.
#
# Run:  powershell -File installer\build.ps1
# Output: dist\DatameterSetup-<version>-<arch>.exe

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Project = Join-Path $Root 'src\Datameter.App\Datameter.App.csproj'
$Iss = Join-Path $Root 'installer\Datameter.iss'
$Dist = Join-Path $Root 'dist'
$Tfm = 'net9.0-windows10.0.19041.0'

$Iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $Iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

if (-not (Test-Path $Dist)) { New-Item -ItemType Directory -Path $Dist | Out-Null }

# x86 last: it is the fallback build, and listing it last keeps the summary readable.
$targets = @(
    @{ Rid = 'win-x64';   Arch = 'x64' },
    @{ Rid = 'win-arm64'; Arch = 'arm64' },
    @{ Rid = 'win-x86';   Arch = 'x86' }
)

foreach ($t in $targets) {
    Write-Host "=== publishing $($t.Rid) ===" -ForegroundColor Cyan
    dotnet publish $Project -c Release -r $t.Rid --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $($t.Rid)" }

    $publish = Join-Path $Root "src\Datameter.App\bin\Release\$Tfm\$($t.Rid)\publish"
    if (-not (Test-Path (Join-Path $publish 'Datameter.exe'))) { throw "no output for $($t.Rid)" }

    # `dotnet publish` drops the compiled XAML for unpackaged WinUI 3 apps; the project has a
    # target that puts it back. If that ever regresses, the app installs and then dies on
    # launch with "XAML parsing failed", so fail loudly here instead.
    if (-not (Test-Path (Join-Path $publish 'App.xbf'))) { throw "XAML assets missing from $($t.Rid) publish" }

    Write-Host "=== building installer for $($t.Arch) ===" -ForegroundColor Cyan
    & $Iscc "/DArch=$($t.Arch)" "/DPublishDir=$publish" "/DDistDir=$Dist" $Iss | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "iscc failed for $($t.Arch)" }
}

Write-Host ""
Write-Host "=== done ===" -ForegroundColor Green
Get-ChildItem $Dist -Filter '*.exe' |
    Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
