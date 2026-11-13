#requires -Version 7.0
param([string]$OutputRoot = 'artifacts/release', [switch]$SkipPublish)
. "$PSScriptRoot/Common.ps1"
if (!$SkipPublish) { & "$PSScriptRoot/Publish.ps1" -OutputRoot $OutputRoot }
[xml]$project = Get-Content (Join-Path $ProjectRoot 'src/sarupdf/sarupdf.csproj') -Raw
$version = $project.Project.PropertyGroup.Version
$output = [IO.Path]::GetFullPath($OutputRoot, $ProjectRoot)
$payload = Join-Path $output "SaruPDF-$version-win-x64"
if (!(Test-Path -LiteralPath (Join-Path $payload 'SaruPDF.exe'))) { throw 'Publish output missing.' }
$package = Join-Path $output "SaruPDF-$version-Update-x64.zip"
Add-Type -AssemblyName System.IO.Compression
[IO.File]::Delete($package)
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName)
$manifest = @{Product='SaruPDF'; Version=$version; Files=@()}
$zip = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\', '/')
        if ($relative -in @('Uninstall.exe', 'SaruPDF.install.ini', 'update-manifest.json') -or $relative.StartsWith('../')) { throw 'Unexpected update payload file.' }
        $manifest.Files += @{Path=$relative; Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $entry = $zip.CreateEntry('update-manifest.json')
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write(($manifest | ConvertTo-Json -Depth 5)) } finally { $writer.Dispose() }
} finally { $zip.Dispose() }
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output "SaruPDF-$version-Update-SHA256SUMS.txt"), "$hash  $([IO.Path]::GetFileName($package))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Update package: $package"
