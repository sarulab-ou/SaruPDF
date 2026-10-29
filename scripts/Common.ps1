#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$script:ProjectRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $script:ProjectRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $script:ProjectRoot '.tools/nuget'

function Get-DotNet {
    $local = Join-Path $script:ProjectRoot '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw '.NET 10 SDK がありません。scripts/Install-Sdk.ps1 を実行してください。'
}

function Invoke-DotNet {
    & (Get-DotNet) @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet が終了コード $LASTEXITCODE で失敗しました。" }
}
