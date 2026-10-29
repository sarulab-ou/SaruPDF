#requires -Version 7.0
param([string]$OutputRoot = 'artifacts')
. "$PSScriptRoot/Common.ps1"
Push-Location $ProjectRoot
try {
    [xml]$project = Get-Content (Join-Path $ProjectRoot 'src/sarupdf/sarupdf.csproj') -Raw
    $version = $project.Project.PropertyGroup.Version
    $output = Join-Path $OutputRoot "SaruPDF-$version-win-x64"
    $output = [System.IO.Path]::GetFullPath($output, $ProjectRoot)
    $archive = "$output.zip"
    Invoke-DotNet restore src/sarupdf/sarupdf.csproj --locked-mode
    Invoke-DotNet publish src/sarupdf/sarupdf.csproj -c Release -r win-x64 --self-contained true --no-restore '-p:PublishSingleFile=false' -o $output
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'README.md') -Destination $output
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'THIRD-PARTY-NOTICES.md') -Destination $output
    New-Item -ItemType Directory -Path (Join-Path $output 'docs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'docs/verification.md') -Destination (Join-Path $output 'docs')
    $webViewPackage = Join-Path $env:NUGET_PACKAGES 'microsoft.web.webview2/1.0.4314.35'
    Copy-Item -LiteralPath (Join-Path $webViewPackage 'LICENSE.txt') -Destination (Join-Path $output 'WebView2-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $webViewPackage 'NOTICE.txt') -Destination (Join-Path $output 'WebView2-NOTICE.txt')
    Compress-Archive -Path "$output/*" -DestinationPath $archive -Force
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
    Write-Output "配布 ZIP: $archive"
} finally { Pop-Location }
