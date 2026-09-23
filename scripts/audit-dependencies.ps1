$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# Policy: fail on every npm advisory, including low severity.
Push-Location (Join-Path $repositoryRoot 'planner-edge-widget\widget')
try {
    npm audit --audit-level=low
    if ($LASTEXITCODE -ne 0) { throw 'Planner npm dependency audit found an advisory or failed.' }
} finally { Pop-Location }

Push-Location (Join-Path $repositoryRoot 'outlook-edge-widget')
try {
    npm audit --audit-level=low
    if ($LASTEXITCODE -ne 0) { throw 'Outlook npm dependency audit found an advisory or failed.' }
} finally { Pop-Location }

$helperTests = Join-Path $repositoryRoot 'microsoft-widgets-helper\tests\MicrosoftWidgets.Helper.Tests\MicrosoftWidgets.Helper.Tests.csproj'
dotnet restore $helperTests --force-evaluate '-p:NuGetAudit=true' '-p:NuGetAuditMode=all' '-warnaserror:NU1901,NU1902,NU1903,NU1904'
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore or advisory audit failed.' }

dotnet list $helperTests package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerable-package query failed.' }
