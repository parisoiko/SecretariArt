<#
.SYNOPSIS
    Runs a separate QA copy of SecretariArt on http://localhost:5200 against its own database (SecretariArt_QA),
    with no demo data and its own platform admin. The dev app (:5100) and its database are not touched.
.PARAMETER Reset
    Drops SecretariArt_QA first, so the tester starts from an empty site.
.PARAMETER RealRateLimits
    Keeps the production rate limits (by default they are raised so the tester is never slowed down).
#>
param([switch]$Reset, [switch]$RealRateLimits)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$database = 'SecretariArt_QA'
$out = Join-Path $env:TEMP 'secretariart-qa'

if ($Reset) {
    Write-Host "Dropping $database..."
    # SINGLE_USER first: a still-open pooled connection would otherwise block the drop.
    sqlcmd -S '(localdb)\mssqllocaldb' -b -Q "IF DB_ID('$database') IS NOT NULL BEGIN ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]; END"
    if ($LASTEXITCODE -ne 0) { throw "Could not drop $database." }
}

# Build into a temp folder so this works while F5 holds src/SecretariArt/bin locked.
dotnet build (Join-Path $repo 'src/SecretariArt') -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# Process-scoped overrides only: appsettings files stay as they are.
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__SecretariArt = "Server=(localdb)\mssqllocaldb;Database=$database;Trusted_Connection=True;MultipleActiveResultSets=true"
$env:SeedDemoData = 'false'
$env:PlatformAdmin__Email = 'qa-platform@test.secretariart.app'
$env:PlatformAdmin__Password = 'QaTest!2026'
$env:PlatformAdmin__FirstName = 'QA'
$env:PlatformAdmin__LastName = 'Platform'
# Emails (password resets) are saved where the tester can read them, apart from the dev app's.
$env:Email__PickupDirectory = Join-Path $repo 'qa/reports/mail'
# The tester works from one address and creates many accounts: rate limits stay on (same code as production) but high
# enough never to get in its way. Pass -RealRateLimits to test with the production limits instead.
if (-not $RealRateLimits) {
    foreach ($limit in 'Global', 'SignIn', 'Register', 'InviteCodes', 'OrganizationRequests', 'Generation', 'AccountRecovery') {
        Set-Item "env:RateLimits__${limit}__PermitLimit" 100000
    }
}

Write-Host "QA app on http://localhost:5200 (database $database). Ctrl+C to stop."
dotnet (Join-Path $out 'SecretariArt.dll') --contentRoot (Join-Path $repo 'src/SecretariArt') --urls http://localhost:5200
