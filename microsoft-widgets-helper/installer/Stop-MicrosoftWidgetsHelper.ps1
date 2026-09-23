param(
    [string]$PipeName = 'Knack25.MicrosoftWidgetsHelper.Control.v1',
    [string]$MutexName = 'Local\Knack25.MicrosoftWidgetsHelper',
    [ValidateRange(100, 30000)][int]$OverallTimeoutMilliseconds = 15000,
    [ValidateRange(50, 10000)][int]$ConnectTimeoutMilliseconds = 3000,
    [ValidateRange(50, 10000)][int]$MutexWaitMilliseconds = 5000
)

$ErrorActionPreference = 'Stop'

if ($env:MICROSOFT_WIDGETS_STOP_PIPE) { $PipeName = $env:MICROSOFT_WIDGETS_STOP_PIPE }
if ($env:MICROSOFT_WIDGETS_STOP_MUTEX) { $MutexName = $env:MICROSOFT_WIDGETS_STOP_MUTEX }
if ($env:MICROSOFT_WIDGETS_STOP_OVERALL_TIMEOUT_MS) { $OverallTimeoutMilliseconds = [int]$env:MICROSOFT_WIDGETS_STOP_OVERALL_TIMEOUT_MS }
if ($env:MICROSOFT_WIDGETS_STOP_CONNECT_TIMEOUT_MS) { $ConnectTimeoutMilliseconds = [int]$env:MICROSOFT_WIDGETS_STOP_CONNECT_TIMEOUT_MS }
if ($env:MICROSOFT_WIDGETS_STOP_MUTEX_WAIT_MS) { $MutexWaitMilliseconds = [int]$env:MICROSOFT_WIDGETS_STOP_MUTEX_WAIT_MS }
$LegacyBaseUri = ''
if ($PipeName -eq 'Knack25.MicrosoftWidgetsHelper.Control.v1') { $LegacyBaseUri = 'http://localhost:8787/' }
if ($env:MICROSOFT_WIDGETS_STOP_LEGACY_BASE_URI) { $LegacyBaseUri = $env:MICROSOFT_WIDGETS_STOP_LEGACY_BASE_URI }

$worker = {
    param(
        [string]$PipeName,
        [string]$MutexName,
        [int]$ConnectTimeoutMilliseconds,
        [int]$MutexWaitMilliseconds,
        [string]$LegacyBaseUri
    )

    $ErrorActionPreference = 'Stop'

    function Test-HelperMutexExists {
        $mutex = $null
        try {
            $mutex = [Threading.Mutex]::OpenExisting($MutexName)
            return $true
        } catch [Threading.WaitHandleCannotBeOpenedException] {
            return $false
        } finally {
            if ($null -ne $mutex) { $mutex.Dispose() }
        }
    }

    function Stop-LegacyHelper {
        if (-not $LegacyBaseUri) { return $false }
        try {
            $base = [Uri]::new($LegacyBaseUri)
            if ($base.Scheme -ne 'http' -or
                $base.Host -notin @('localhost', '127.0.0.1', '[::1]') -or
                $base.UserInfo -or $base.AbsolutePath -ne '/') { return $false }
            $health = Invoke-WebRequest -Uri ([Uri]::new($base, 'health')) -UseBasicParsing -TimeoutSec 2 -MaximumRedirection 0
            if ($health.StatusCode -ne 200) { return $false }
            $identity = $health.Content | ConvertFrom-Json
            if ($identity.version -notin @('0.1.1', '0.1.2', '0.1.3', '0.1.4') -or
                $identity.service -ne 'Microsoft Widgets Helper') { return $false }
            $response = Invoke-WebRequest -Method Post -Uri ([Uri]::new($base, 'host/stop')) -UseBasicParsing -TimeoutSec 2 -MaximumRedirection 0
            return $response.StatusCode -eq 202
        } catch {
            return $false
        }
    }

    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        try {
            $pipe.Connect($ConnectTimeoutMilliseconds)
        } catch [TimeoutException] {
            if (-not (Test-HelperMutexExists)) { exit 0 }
            if (-not (Stop-LegacyHelper)) { exit 4 }
            $pipe.Dispose()
            $pipe = $null
        }

        if ($null -ne $pipe) {
            $message = [Text.Encoding]::UTF8.GetBytes('stop')
            $pipe.WriteByte([byte]$message.Length)
            $pipe.Write($message, 0, $message.Length)
            $pipe.Flush()
            if ($pipe.ReadByte() -ne 1) { exit 2 }
        }
    } catch [IO.IOException] {
        exit 3
    } catch [UnauthorizedAccessException] {
        exit 5
    } finally {
        if ($null -ne $pipe) { $pipe.Dispose() }
    }

    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.ElapsedMilliseconds -lt $MutexWaitMilliseconds) {
        if (-not (Test-HelperMutexExists)) { exit 0 }
        Start-Sleep -Milliseconds 50
    }
    exit 6
}

function ConvertTo-SingleQuotedLiteral([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

$workerInvocation = '& {' + $worker.ToString() + '} ' +
    '-PipeName ' + (ConvertTo-SingleQuotedLiteral $PipeName) + ' ' +
    '-MutexName ' + (ConvertTo-SingleQuotedLiteral $MutexName) + ' ' +
    '-ConnectTimeoutMilliseconds ' + $ConnectTimeoutMilliseconds + ' ' +
    '-MutexWaitMilliseconds ' + $MutexWaitMilliseconds + ' ' +
    '-LegacyBaseUri ' + (ConvertTo-SingleQuotedLiteral $LegacyBaseUri)
$encodedWorker = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($workerInvocation))
$powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$child = Start-Process -FilePath $powerShell -ArgumentList @(
    '-NoLogo', '-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-EncodedCommand', $encodedWorker
) -WindowStyle Hidden -PassThru
try {
    if (-not $child.WaitForExit($OverallTimeoutMilliseconds)) {
        $child.Kill()
        $child.WaitForExit()
        exit 9
    }
    exit $child.ExitCode
} finally {
    $child.Dispose()
}
