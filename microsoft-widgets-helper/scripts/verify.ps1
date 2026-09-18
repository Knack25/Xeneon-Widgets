$ErrorActionPreference = "Stop"
dotnet test (Join-Path $PSScriptRoot "..\tests\MicrosoftWidgets.Helper.Tests\MicrosoftWidgets.Helper.Tests.csproj")
if ($LASTEXITCODE -ne 0) { throw "Helper tests failed." }
node --test (Join-Path $PSScriptRoot "..\tests\update-ui.test.mjs")
if ($LASTEXITCODE -ne 0) { throw "Helper update UI tests failed." }
