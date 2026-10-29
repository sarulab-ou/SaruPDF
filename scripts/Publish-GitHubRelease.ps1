#requires -Version 7.0
param([string]$OutputRoot = 'artifacts/release', [switch]$Draft)
. "$PSScriptRoot/Common.ps1"
function Quote-Bash([string]$Value) { return "'" + $Value.Replace("'", "'\''") + "'" }
function ConvertTo-WslPath([string]$Value) {
    $absolute = [System.IO.Path]::GetFullPath($Value, $ProjectRoot)
    if ($absolute -notmatch '^([A-Za-z]):\\(.*)$') { throw 'WSL 用のドライブパスが必要です。' }
    return '/mnt/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2].Replace('\', '/')
}
[xml]$project = Get-Content (Join-Path $ProjectRoot 'src/sarupdf/sarupdf.csproj') -Raw
$version = $project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$output = [System.IO.Path]::GetFullPath($OutputRoot, $ProjectRoot)
$assets = @("SaruPDF-$version-Setup-x64.exe", "SaruPDF-$version-SHA256SUMS.txt")
foreach ($asset in $assets) { if (!(Test-Path -LiteralPath (Join-Path $output $asset))) { throw "Missing release asset: $asset" } }
$checksumLines = @(Get-Content (Join-Path $output "SaruPDF-$version-SHA256SUMS.txt"))
if ($checksumLines.Count -ne 1) { throw 'Checksum manifest must contain only the installer.' }
foreach ($line in $checksumLines) {
    if ($line -notmatch '^([a-f0-9]{64})  (SaruPDF-[^/\\]+)$') { throw 'Invalid checksum manifest.' }
    if ($Matches[2] -ne $assets[0]) { throw 'Unexpected checksum asset.' }
    if ((Get-FileHash -LiteralPath (Join-Path $output $Matches[2]) -Algorithm SHA256).Hash -ne $Matches[1]) { throw 'Release checksum mismatch.' }
}
$notes = Join-Path $ProjectRoot "docs/releases/v$version.md"
if (!(Test-Path -LiteralPath $notes)) { throw 'Release notes missing.' }
$root = Quote-Bash (ConvertTo-WslPath $ProjectRoot)
$status = & wsl.exe bash -lc "cd $root && git status --porcelain"
if ($LASTEXITCODE -ne 0 -or $status) { throw '未コミット変更があります。コミット・push 後に実行してください。' }
$revision = & wsl.exe bash -lc "cd $root && git rev-parse HEAD"
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40}$') { throw 'Git コミットを取得できません。' }
# No commit, push, visibility changes, clobber or replacement of existing assets.
$arguments = @('release', 'create', "v$version", '--repo', 'sarulab-ou/SaruPDF', '--title', "SaruPDF $version", '--notes-file', (ConvertTo-WslPath $notes))
if ($Draft) { $arguments += '--draft' }
$arguments += $assets | ForEach-Object { ConvertTo-WslPath (Join-Path $output $_) }
$quoted = ($arguments | ForEach-Object { Quote-Bash $_ }) -join ' '
$command = "cd $root && gh api repos/sarulab-ou/SaruPDF/commits/$revision --silent && gh $quoted --target $revision"
& wsl.exe bash -lc $command
if ($LASTEXITCODE -ne 0) { throw 'GitHub リリースの作成に失敗しました。未コミット変更・push 状態・認証・同名リリースを確認してください。' }
