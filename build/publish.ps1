<#
.SYNOPSIS
    Builds, tests and packages WinNetworkManager.

.DESCRIPTION
    For each runtime identifier this script produces, under .\artifacts:
      publish\<rid>\                                  self-contained application folder (no .NET installation needed)
      WinNetworkManager-<version>-<rid>-portable.zip   portable build (settings and history are kept next to the program)
      WinNetworkManager-<version>-<rid>.msi            Windows Installer package (per-machine, Program Files)
      SHA256SUMS.txt                                   checksums of the packages
    Executables and the MSI are signed when -CertificateThumbprint is given (signtool from the Windows SDK is required).

.EXAMPLE
    .\build\publish.ps1
    .\build\publish.ps1 -Runtime win-x64, win-arm64 -SkipTests
    .\build\publish.ps1 -CertificateThumbprint 0123456789ABCDEF... -TimestampUrl http://timestamp.digicert.com
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime = @('win-x64'),
    [string] $Configuration = 'Release',
    [string] $Version,
    [switch] $SkipTests,
    [switch] $SkipInstaller,
    [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

if (-not $Version) {
    [xml] $props = Get-Content -Raw (Join-Path $root 'Directory.Build.props')
    $Version = $props.SelectSingleNode('//VersionPrefix').InnerText
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' must be major.minor.patch (Windows Installer requirement)." }

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)." }
}

function Invoke-Sign([string[]] $Files) {
    if (-not $CertificateThumbprint) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe was not found (install the Windows SDK) but -CertificateThumbprint was given.' }
    Invoke-Step "Sign $($Files.Count) file(s)" {
        & $signtool.FullName sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d 'WinNetworkManager' @Files
    }
}

if (-not $SkipTests) {
    Invoke-Step 'Run tests' { dotnet test --solution (Join-Path $root 'WinNetworkManager.slnx') -c $Configuration }
}

$packages = @()
foreach ($rid in $Runtime) {
    $platform = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
    $publish = Join-Path $artifacts "publish\$rid"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    $common = @('-c', $Configuration, '-r', $rid, '--self-contained', 'true', "-p:Version=$Version",
                '-p:PublishReadyToRun=true', '-p:DebugType=none', '-p:DebugSymbols=false', '-o', $publish)
    Invoke-Step "Publish WinNetworkManager ($rid)" { dotnet publish (Join-Path $root 'src\WinNetworkManager.App\WinNetworkManager.App.csproj') @common }
    Invoke-Step "Publish collector helper ($rid)" { dotnet publish (Join-Path $root 'src\WinNetworkManager.Collector\WinNetworkManager.Collector.csproj') @common }
    foreach ($exe in 'WinNetworkManager.exe', 'WinNetworkManager.Collector.exe') {
        if (-not (Test-Path (Join-Path $publish $exe))) { throw "$exe is missing from $publish." }
    }
    Invoke-Sign @((Join-Path $publish 'WinNetworkManager.exe'), (Join-Path $publish 'WinNetworkManager.Collector.exe'))

    # Portable ZIP: the marker file makes WinNetworkManager keep settings, history and logs in a Data folder next to it.
    $staging = Join-Path $artifacts "staging\$rid\WinNetworkManager"
    if (Test-Path (Split-Path $staging)) { Remove-Item (Split-Path $staging) -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Copy-Item (Join-Path $publish '*') $staging -Recurse
    Set-Content -Path (Join-Path $staging 'portable.txt') -Encoding ascii -Value @(
        'This file turns on portable mode: settings, history and logs are stored in the Data folder next to WinNetworkManager.exe.',
        'Delete this file to store them in %LOCALAPPDATA%\WinNetworkManager instead.')
    $zip = Join-Path $artifacts "WinNetworkManager-$Version-$rid-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Invoke-Step "Create $([IO.Path]::GetFileName($zip))" { Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal; $global:LASTEXITCODE = 0 }
    $packages += $zip

    if (-not $SkipInstaller) {
        $wixproj = Join-Path $root 'installer\WinNetworkManager.Installer.wixproj'
        Invoke-Step "Build MSI ($platform)" {
            dotnet build $wixproj -c $Configuration "-p:Platform=$platform" "-p:ProductVersion=$Version" "-p:PublishDir=$publish/" "-p:OutputPath=$artifacts/installer/$rid/"
        }
        $msi = Join-Path $artifacts "WinNetworkManager-$Version-$rid.msi"
        Copy-Item (Get-ChildItem (Join-Path $artifacts "installer\$rid") -Filter *.msi -Recurse | Select-Object -First 1).FullName $msi -Force
        Invoke-Sign @($msi)
        $packages += $msi
    }
}

$sums = Join-Path $artifacts 'SHA256SUMS.txt'
$packages | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_) } | Set-Content -Path $sums -Encoding ascii
Write-Host ''
Write-Host 'Packages:' -ForegroundColor Green
$packages + $sums | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_, ((Get-Item $_).Length / 1MB)) }
