#requires -Version 7.0
. "$PSScriptRoot/Common.ps1"
function Quote-Bash([string]$Value) { return "'" + $Value.Replace("'", "'\''") + "'" }
function ConvertTo-WslPath([string]$Value) {
    $absolute = [System.IO.Path]::GetFullPath($Value, $ProjectRoot)
    if ($absolute -notmatch '^([A-Za-z]):\\(.*)$') { throw 'WSL 用のドライブパスが必要です。' }
    return '/mnt/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2].Replace('\', '/')
}
$root = Quote-Bash (ConvertTo-WslPath $ProjectRoot)
# Ubuntu 24.04 official packages, pinned. Extract only; no apt install / sudo.
$command = "cd $root && mkdir -p .tools/nsis-packages .tools/nsis-root && cd .tools/nsis-packages && apt-get download nsis=3.09-4ubuntu1 nsis-common=3.09-4ubuntu1 && dpkg-deb -x nsis_3.09-4ubuntu1_amd64.deb ../nsis-root && dpkg-deb -x nsis-common_3.09-4ubuntu1_all.deb ../nsis-root && NSISDIR='../nsis-root/usr/share/nsis' ../nsis-root/usr/bin/makensis -VERSION"
& wsl.exe bash -lc $command
if ($LASTEXITCODE -ne 0) { throw 'NSIS の準備に失敗しました。WSL Ubuntu 24.04 と公式パッケージへの接続を確認してください。' }
