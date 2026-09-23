$ErrorActionPreference = "Stop"

Push-Location (Join-Path $PSScriptRoot "..")
try {
    & ..\microsoft-widgets-helper\scripts\verify.ps1
    if ($LASTEXITCODE -ne 0) { throw "Complete helper verification failed." }
    Push-Location widget
    try {
        npm test
        if ($LASTEXITCODE -ne 0) { throw "Widget tests failed." }
    }
    finally { Pop-Location }
    Push-Location ..\outlook-edge-widget
    try {
        npm test
        if ($LASTEXITCODE -ne 0) { throw "Outlook widget tests failed." }
    }
    finally { Pop-Location }
}
finally { Pop-Location }
