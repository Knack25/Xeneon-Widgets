$ErrorActionPreference = "Stop"

Push-Location (Join-Path $PSScriptRoot "..")
try {
    dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj
    if ($LASTEXITCODE -ne 0) { throw "Helper tests failed." }
    Push-Location widget
    try {
        npm test
        if ($LASTEXITCODE -ne 0) { throw "Widget tests failed." }
    }
    finally { Pop-Location }
}
finally { Pop-Location }
