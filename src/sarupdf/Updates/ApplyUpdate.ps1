$ErrorActionPreference = 'Stop'
$request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
$target = [IO.Path]::GetFullPath($request.Target).TrimEnd('\')
$stage = [IO.Path]::GetFullPath($request.Stage).TrimEnd('\')
$transaction = [IO.Path]::GetFullPath($request.Transaction)
$changes = New-Object 'System.Collections.Generic.List[object]'
$rollbackOk = $true
$committed = $false

function SafePath([string]$root, [string]$relative) {
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Contains('\') -or
        ($relative.Split('/') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ') })) { throw 'Invalid update path' }
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $relative))
    if (-not $path.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Update path escaped its root' }
    $check = $path
    while ($check) {
        if (Test-Path -LiteralPath $check) {
            if ((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked update destination is not supported' }
        }
        $check = [IO.Path]::GetDirectoryName($check)
    }
    return $path
}
function RestartApp {
    $start = @{FilePath = (Join-Path $target 'SaruPDF.exe'); WorkingDirectory = $target}
    if ($request.ResumeSession) { $start.ArgumentList = '--restore-session "' + $request.ResumeSession + '"' }
    elseif ($request.Documents.Count -gt 0) { $start.ArgumentList = (@($request.Documents | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ') }
    Start-Process @start
}
function FileHash([string]$path) {
    $hash = [Security.Cryptography.SHA256]::Create()
    $input = [IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($hash.ComputeHash($input)).Replace('-', '') }
    finally { $input.Dispose(); $hash.Dispose() }
}
try {
    if ($target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\') -or -not (Test-Path -LiteralPath (Join-Path $target 'SaruPDF.exe'))) { throw 'SaruPDF update destination was not found' }
    $manifest = Get-Content -LiteralPath (Join-Path $stage 'update-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.Product -ne 'SaruPDF' -or $manifest.Version -ne $request.Version -or $manifest.Files.Count -eq 0) { throw 'Invalid update manifest' }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.Files) {
        if (-not $seen.Add($file.Path) -or $file.Path -in @('SaruPDF.install.ini', 'Uninstall.exe', 'update-manifest.json')) { throw 'Reserved or duplicate update path' }
        $source = SafePath $stage $file.Path
        $null = SafePath $target $file.Path
        $fresh = SafePath (Join-Path $transaction 'new') $file.Path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fresh)) | Out-Null
        [IO.File]::Copy($source, $fresh, $false)
        if ((FileHash $fresh) -ne $file.Sha256) { throw 'Update file checksum mismatch' }
    }
    if (-not $seen.Contains('SaruPDF.exe') -or -not $seen.Contains('SaruPDF.dll')) { throw 'SaruPDF binaries are missing' }
    $app = Get-Process -Id $request.ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $app -and -not $app.WaitForExit(120000)) { throw 'Timed out waiting for SaruPDF to exit' }
    foreach ($file in $manifest.Files) {
        $destination = SafePath $target $file.Path
        $backup = SafePath (Join-Path $transaction 'backup') $file.Path
        $fresh = SafePath (Join-Path $transaction 'new') $file.Path
        $change = [pscustomobject]@{Destination=$destination; Backup=$backup; OldMoved=$false; NewMoved=$false}
        $changes.Add($change)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        if ([IO.File]::Exists($destination)) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
            [IO.File]::Move($destination, $backup); $change.OldMoved = $true
        }
        [IO.File]::Move($fresh, $destination); $change.NewMoved = $true
    }
    RestartApp
    $committed = $true
    # Refresh informational installation metadata only for this installation.
    try {
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SaruPDF'
        $installed = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($installed -and [IO.Path]::GetFullPath($installed.InstallLocation).TrimEnd('\') -eq $target) {
            Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value $request.Version
        }
    } catch { }
    @{Success=$true; Version=$request.Version} | ConvertTo-Json | Set-Content -LiteralPath $request.Result -Encoding UTF8
    # The transaction is our fresh sibling directory, never the install tree.
    try { [IO.Directory]::Delete($transaction, $true) } catch { }
    exit 0
} catch {
    if ($committed) { exit 0 }
    $failure = $_.Exception.Message
    for ($i = $changes.Count - 1; $i -ge 0; $i--) {
        $change = $changes[$i]
        try {
            if ($change.NewMoved) { [IO.File]::Delete($change.Destination) }
            if ($change.OldMoved) { [IO.File]::Move($change.Backup, $change.Destination) }
        } catch { $rollbackOk = $false }
    }
    @{Success=$false; Error=$failure; RollbackSucceeded=$rollbackOk; ChangedFiles=$changes.Count; Backup=$transaction} | ConvertTo-Json | Set-Content -LiteralPath $request.Result -Encoding UTF8
    if ($rollbackOk) { try { [IO.Directory]::Delete($transaction, $true) } catch { } }
    if ($rollbackOk -and -not (Get-Process -Id $request.ProcessId -ErrorAction SilentlyContinue)) {
        try { RestartApp } catch { }
    }
    if ($request.ShowErrors) {
        Add-Type -AssemblyName System.Windows.Forms
        $message = '更新できませんでした。' + [Environment]::NewLine + $failure
        if (-not $rollbackOk) { $message += [Environment]::NewLine + '復旧用ファイル: ' + $transaction }
        [System.Windows.Forms.MessageBox]::Show($message, 'SaruPDF 更新') | Out-Null
    }
    exit 1
}
