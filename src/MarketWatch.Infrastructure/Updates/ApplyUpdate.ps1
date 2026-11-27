param(
    [Parameter(Mandatory=$true)][int]$TargetPid,
    [Parameter(Mandatory=$true)][string]$TargetExe,
    [Parameter(Mandatory=$true)][string]$StagedExe
)
$ErrorActionPreference = 'Stop'
$attempt = [Guid]::NewGuid().ToString('N')
$ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, "Local\MarketWatch.UpdateReady.$attempt")
$candidate = "$TargetExe.$attempt.new"
$backup = "$TargetExe.$attempt.bak"
$failed = "$TargetExe.$attempt.failed"
$old = $null
$child = $null

function Invoke-FileReplaceWithRetry {
    param(
        [Parameter(Mandatory=$true)][string]$SourcePath,
        [Parameter(Mandatory=$true)][string]$DestinationPath,
        [Parameter(Mandatory=$true)][string]$BackupPath
    )
    for ($try = 1; $try -le 6; $try++) {
        try {
            if ([IO.File]::Exists($DestinationPath)) {
                [IO.File]::Replace($SourcePath, $DestinationPath, $BackupPath)
            } else {
                [IO.File]::Move($SourcePath, $DestinationPath)
            }
            return
        } catch [IO.IOException], [UnauthorizedAccessException] {
            if ($try -eq 6) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
}

try {
    $old = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
    if ($old -and -not $old.WaitForExit(30000)) {
        throw 'Original MarketWatch process did not exit.'
    }

    try {
        # Stage beside the target so replacement and rollback work across download/install volumes.
        [IO.File]::Copy($StagedExe, $candidate)
        Invoke-FileReplaceWithRetry -SourcePath $candidate -DestinationPath $TargetExe -BackupPath $backup
        $child = Start-Process -FilePath $TargetExe -ArgumentList "--update-attempt=$attempt" -PassThru
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        while (-not $ready.WaitOne(200)) {
            if ($child.HasExited) { throw 'Updated process exited before startup completed.' }
            if ($elapsed.Elapsed.TotalSeconds -ge 60) { throw 'Timed out waiting for MarketWatch startup.' }
        }
        if ($child.HasExited) { throw 'Updated process exited during startup.' }
    } catch {
        if ([IO.File]::Exists($backup)) {
            if ($child -and -not $child.HasExited) {
                try { $child.Kill() } catch { if (-not $child.HasExited) { throw } }
                if (-not $child.WaitForExit(10000)) { throw "Cannot restore while the new process is running. Backup: $backup" }
            }
            Invoke-FileReplaceWithRetry -SourcePath $backup -DestinationPath $TargetExe -BackupPath $failed
            Remove-Item -LiteralPath $failed -Force -ErrorAction SilentlyContinue
        }
        Start-Process -FilePath $TargetExe
        throw
    }
    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $StagedExe -Force -ErrorAction SilentlyContinue
} finally {
    $ready.Dispose()
    if ($child) { $child.Dispose() }
    if ($old) { $old.Dispose() }
    Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
}
