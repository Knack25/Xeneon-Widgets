param(
    [string]$PipeName = 'Knack25.MicrosoftWidgetsHelper.Control.v1',
    [string]$MutexName = 'Local\Knack25.MicrosoftWidgetsHelper',
    [ValidateRange(100, 30000)][int]$OverallTimeoutMilliseconds = 9000,
    [ValidateRange(50, 10000)][int]$ConnectTimeoutMilliseconds = 3000,
    [ValidateRange(50, 10000)][int]$MutexWaitMilliseconds = 5000
)

$ErrorActionPreference = 'Stop'

if ($env:MICROSOFT_WIDGETS_STOP_PIPE) { $PipeName = $env:MICROSOFT_WIDGETS_STOP_PIPE }
if ($env:MICROSOFT_WIDGETS_STOP_MUTEX) { $MutexName = $env:MICROSOFT_WIDGETS_STOP_MUTEX }
if ($env:MICROSOFT_WIDGETS_STOP_OVERALL_TIMEOUT_MS) { $OverallTimeoutMilliseconds = [int]$env:MICROSOFT_WIDGETS_STOP_OVERALL_TIMEOUT_MS }
if ($env:MICROSOFT_WIDGETS_STOP_CONNECT_TIMEOUT_MS) { $ConnectTimeoutMilliseconds = [int]$env:MICROSOFT_WIDGETS_STOP_CONNECT_TIMEOUT_MS }
if ($env:MICROSOFT_WIDGETS_STOP_MUTEX_WAIT_MS) { $MutexWaitMilliseconds = [int]$env:MICROSOFT_WIDGETS_STOP_MUTEX_WAIT_MS }

$worker = {
    param(
        [string]$PipeName,
        [string]$MutexName,
        [int]$ConnectTimeoutMilliseconds,
        [int]$MutexWaitMilliseconds
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

    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        try {
            $pipe.Connect($ConnectTimeoutMilliseconds)
        } catch [TimeoutException] {
            if (Test-HelperMutexExists) { exit 4 }
            exit 0
        }

        $message = [Text.Encoding]::UTF8.GetBytes('stop')
        $pipe.WriteByte([byte]$message.Length)
        $pipe.Write($message, 0, $message.Length)
        $pipe.Flush()
        if ($pipe.ReadByte() -ne 1) { exit 2 }
    } catch [IO.IOException] {
        exit 3
    } catch [UnauthorizedAccessException] {
        exit 5
    } finally {
        $pipe.Dispose()
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
    '-MutexWaitMilliseconds ' + $MutexWaitMilliseconds
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
