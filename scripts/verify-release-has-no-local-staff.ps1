# Proves the admin API's development-only staff sign-in is not in a Release build.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-has-no-local-staff.ps1 [-PublishDirectory publish\admin-api]
#
# The local staff identity (src/Koleksi.AdminApi/Development/LocalStaffIdentity.cs) is wrapped in `#if DEBUG`, so a Release build
# never contains it. A comment cannot prove that, so this script looks: it searches the compiled bootstrap.dll for the type's name and
# the sign-in route, once in the Release artefact (which must NOT contain them) and once in the Debug build (which MUST, as a
# control: a search that finds nothing anywhere would pass for the wrong reason).
#
# Type names live in the assembly's metadata as UTF-8, and string literals such as the route in its user-string heap as UTF-16, so
# both encodings are searched. Run it after publishing; it fails the deploy checklist if anything is found.

param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\publish\admin-api'),
    [string]$DebugDirectory = (Join-Path $PSScriptRoot '..\src\Koleksi.AdminApi\bin\Debug\net10.0')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$needles = @('LocalStaffIdentity', 'LocalStaffAccounts', '/dev/staff/sign-in', '/dev/staff/refresh', 'KoleksiAdmin:LocalStaff', 'DevDisputeFixture', '/disputes/seed')

function Find-Needles {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $text8 = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)
    $text16 = [System.Text.Encoding]::Unicode.GetString($bytes)
    # Unicode decoding depends on alignment, so read the odd-offset view too.
    $odd = New-Object byte[] ($bytes.Length - 1)
    [Array]::Copy($bytes, 1, $odd, 0, $odd.Length)
    $text16odd = [System.Text.Encoding]::Unicode.GetString($odd)

    $found = @()
    foreach ($needle in $needles) {
        if ($text8.Contains($needle) -or $text16.Contains($needle) -or $text16odd.Contains($needle)) { $found += $needle }
    }
    return $found
}

$releaseDll = Join-Path $PublishDirectory 'bootstrap.dll'
$debugDll = Join-Path $DebugDirectory 'bootstrap.dll'

if (-not (Test-Path $releaseDll)) { Write-Host "No bootstrap.dll in $PublishDirectory. Publish the admin API first (see README)." -ForegroundColor Red; exit 2 }
if (-not (Test-Path $debugDll)) { Write-Host "No Debug bootstrap.dll in $DebugDirectory. Run: dotnet build src\Koleksi.AdminApi -c Debug" -ForegroundColor Red; exit 2 }

$control = @(Find-Needles -Path $debugDll)
if ($control.Count -lt 3) {
    Write-Host "The control failed: the Debug build should contain the local sign-in but only $($control.Count) of $($needles.Count) markers were found, so this search cannot be trusted." -ForegroundColor Red
    exit 3
}

$release = @(Find-Needles -Path $releaseDll)
if ($release.Count -gt 0) {
    Write-Host ('FAIL: the Release artefact contains the local staff sign-in: ' + ($release -join ', ')) -ForegroundColor Red
    exit 1
}

Write-Host ("OK: the Debug build contains $($control.Count) of $($needles.Count) markers (control), the Release artefact contains none.") -ForegroundColor Green
Write-Host "  Checked: $releaseDll"
exit 0
