#requires -Version 7.0
# Microsoft 公式 ZIP を作業フォルダーに展開。管理者権限・PATH 変更は不要。
. "$PSScriptRoot/Common.ps1"
$version = (Get-Content (Join-Path $ProjectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$metadata = Invoke-RestMethod 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
$sdks = @($metadata.releases | ForEach-Object { $_.sdks; $_.sdk })
$sdk = $sdks | Where-Object version -eq $version | Select-Object -First 1
$file = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.url.EndsWith('.zip') } | Select-Object -First 1
if (!$file) { throw "Microsoft のメタデータに SDK $version の ZIP がありません。" }
$tools = Join-Path $ProjectRoot '.tools'
New-Item -ItemType Directory -Path $tools -Force | Out-Null
$archive = Join-Path $tools 'dotnet-sdk.zip'
Invoke-WebRequest $file.url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $file.hash) { throw 'SDK の SHA512 が一致しません。' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $tools 'dotnet') -Force
Invoke-DotNet --version
