$ErrorActionPreference = 'Stop'
$helperRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

dotnet test (Join-Path $helperRoot 'tests\MicrosoftWidgets.Helper.Tests\MicrosoftWidgets.Helper.Tests.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Helper tests failed.' }

foreach ($test in @('helper-api.test.mjs', 'outlook-setup.test.mjs', 'update-ui.test.mjs')) {
    node --test (Join-Path $helperRoot "tests\$test")
    if ($LASTEXITCODE -ne 0) { throw "Helper JavaScript test failed: $test" }
}

node (Join-Path $helperRoot 'tests\setup-browser.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Helper setup browser tests failed.' }

& (Join-Path $helperRoot 'tests\release-security.test.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Release security tests failed.' }
