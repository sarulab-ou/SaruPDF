#requires -Version 7.0
param([string]$OutputRoot = 'artifacts')
. "$PSScriptRoot/Common.ps1"
Push-Location $ProjectRoot
try {
    [xml]$project = Get-Content (Join-Path $ProjectRoot 'src/sarupdf/sarupdf.csproj') -Raw
    $version = $project.Project.PropertyGroup.Version
    $output = Join-Path $OutputRoot "SaruPDF-$version-win-x64"
    $output = [System.IO.Path]::GetFullPath($output, $ProjectRoot)
    Invoke-DotNet restore src/sarupdf/sarupdf.csproj --locked-mode
    Invoke-DotNet publish src/sarupdf/sarupdf.csproj -c Release -r win-x64 --self-contained true --no-restore '-p:PublishSingleFile=false' -o $output
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'README.md') -Destination $output
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'THIRD-PARTY-NOTICES.md') -Destination $output
    New-Item -ItemType Directory -Path (Join-Path $output 'docs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'docs/verification.md') -Destination (Join-Path $output 'docs')
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'docs/development.md') -Destination (Join-Path $output 'docs')
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'docs/screenshots') -Destination (Join-Path $output 'docs') -Recurse -Force
    $webViewPackage = Join-Path $env:NUGET_PACKAGES 'microsoft.web.webview2/1.0.4314.35'
    Copy-Item -LiteralPath (Join-Path $webViewPackage 'LICENSE.txt') -Destination (Join-Path $output 'WebView2-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $webViewPackage 'NOTICE.txt') -Destination (Join-Path $output 'WebView2-NOTICE.txt')
    $licenses = Join-Path $output 'third-party-licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'assets/licenses/PDFsharp-LICENSE.txt') -Destination $licenses
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'assets/licenses/PdfPig-LICENSE.txt') -Destination $licenses
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'assets/licenses/PdfPig-NOTICES.txt') -Destination $licenses
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'assets/licenses/Windows-SDK-LICENSE.txt') -Destination $licenses
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'assets/licenses/CsWinRT-LICENSE.txt') -Destination $licenses
    foreach ($package in @(
        @{ Path = 'microsoft.extensions.logging.abstractions/8.0.3'; Name = 'Microsoft.Extensions.Logging.Abstractions' },
        @{ Path = 'microsoft.extensions.dependencyinjection.abstractions/8.0.2'; Name = 'Microsoft.Extensions.DependencyInjection.Abstractions' }
    )) {
        foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES "$($package.Path)/$notice") -Destination (Join-Path $licenses "$($package.Name)-$notice")
        }
    }
    Write-Output "インストーラー用ファイル: $output"
} finally { Pop-Location }
