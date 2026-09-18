$ErrorActionPreference = "Stop"

Push-Location (Join-Path $PSScriptRoot "..")
try {
    dotnet test ../microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj
    if ($LASTEXITCODE -ne 0) { throw "Helper tests failed." }
    node --test ../microsoft-widgets-helper/tests/update-ui.test.mjs
    if ($LASTEXITCODE -ne 0) { throw "Helper update UI tests failed." }
    Push-Location widget
    try {
        npm test
        if ($LASTEXITCODE -ne 0) { throw "Widget tests failed." }
    }
    finally { Pop-Location }
}
finally { Pop-Location }
