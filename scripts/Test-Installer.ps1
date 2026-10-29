#requires -Version 7.0
param([string]$Installer = 'artifacts/release/SaruPDF-0.2.0-Setup-x64.exe')
. "$PSScriptRoot/Common.ps1"
$installerPath = [System.IO.Path]::GetFullPath($Installer, $ProjectRoot)
$testRoot = Join-Path $ProjectRoot 'artifacts/installer-smoke'
$installPath = Join-Path $testRoot 'インストール 試験 #100% &'
$programs = Join-Path ([Environment]::GetFolderPath('Programs')) 'SaruPDF'

function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Read-Value([string]$Key, [string]$Name = '') {
    $registry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Key)
    try { if ($registry) { return $registry.GetValue($Name) } } finally { if ($registry) { $registry.Dispose() } }
}
function Default-Snapshot {
    $values = [ordered]@{ Pdf = (Read-Value 'Software\Classes\.pdf') }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice')
    try {
        if ($key) { foreach ($name in $key.GetValueNames() | Sort-Object) { $values[$name] = $key.GetValue($name) } }
    } finally { if ($key) { $key.Dispose() } }
    return ConvertTo-Json $values -Compress
}
function Run-Setup([string]$Directory, [bool]$NoAssociation = $false) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($installerPath)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    # NSIS requires /D= to be the last, unquoted remainder of the command line.
    $info.Arguments = '/S ' + $(if($NoAssociation){'/NoAssociation '}else{''}) + '/D=' + $Directory
    $process = [System.Diagnostics.Process]::Start($info)
    if (!$process.WaitForExit(60000)) { throw 'Installer timed out.' }
    return $process.ExitCode
}
function Run-Uninstall {
    $copy = Join-Path $testRoot 'smoke-uninstall.exe'
    Copy-Item -LiteralPath (Join-Path $installPath 'Uninstall.exe') -Destination $copy
    $info = [System.Diagnostics.ProcessStartInfo]::new($copy)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Arguments = '/S _?=' + $installPath
    $process = [System.Diagnostics.Process]::Start($info)
    if (!$process.WaitForExit(30000)) { throw 'Uninstaller timed out.' }
    Assert ($process.ExitCode -eq 0) 'Uninstall failed.'
    Remove-Item -LiteralPath $copy
}

Assert (Test-Path -LiteralPath $installerPath) 'Build the installer first.'
Assert (!(Test-Path -LiteralPath $installPath)) 'A previous smoke-test install exists; inspect it before retrying.'
Assert (!(Test-Path -LiteralPath $programs)) 'Existing SaruPDF Start menu entries found; smoke test will not overwrite them.'
foreach ($keyPath in @('Software\SaruPDF\Capabilities', 'Software\Microsoft\Windows\CurrentVersion\Uninstall\SaruPDF', 'Software\Classes\SaruPDF.Pdf', 'Software\Classes\Applications\SaruPDF.exe', 'Software\Microsoft\Windows\CurrentVersion\App Paths\SaruPDF.exe')) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyPath)
    if ($key) { $key.Dispose(); throw "Existing registration found: $keyPath. Smoke test stopped." }
}
Assert ((Read-Value 'Software\SaruPDF' 'InstallDir') -eq $null) 'An existing installation path was found; smoke test stopped.'
Assert ((Read-Value 'Software\RegisteredApplications' 'SaruPDF') -eq $null) 'An existing registered application was found; smoke test stopped.'
Assert ((Read-Value 'Software\Classes\.pdf\OpenWithProgids' 'SaruPDF.Pdf') -eq $null) 'An existing PDF candidate was found; smoke test stopped.'
$before = Default-Snapshot
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

New-Item -ItemType Directory -Path $installPath | Out-Null
$code = Run-Setup $installPath $true
Assert ($code -eq 0) "Install with /NoAssociation failed (exit $code)."
Assert (Test-Path -LiteralPath (Join-Path $installPath 'SaruPDF.exe')) 'Installed executable missing.'
Assert ((Read-Value 'Software\Classes\SaruPDF.Pdf\shell\open\command') -eq $null) '/NoAssociation registered a PDF handler.'
Run-Uninstall
Assert (!(Test-Path -LiteralPath $installPath)) 'Uninstall left payload files.'
Write-Output 'PASS install/uninstall with association disabled'

Assert ((Run-Setup $installPath) -eq 0) 'Install with PDF registration failed.'
$expectedCommand = '"' + (Join-Path $installPath 'SaruPDF.exe') + '" "%1"'
Assert ((Read-Value 'Software\Classes\SaruPDF.Pdf\shell\open\command') -eq $expectedCommand) 'Open command does not quote the installed path.'
Assert ((Read-Value 'Software\SaruPDF\Capabilities\FileAssociations' '.pdf') -eq 'SaruPDF.Pdf') 'Capabilities missing.'
Assert ((Read-Value 'Software\Classes\.pdf\OpenWithProgids' 'SaruPDF.Pdf') -eq '') 'Open-with registration missing.'
Assert ((Default-Snapshot) -eq $before) 'Existing default PDF association was changed.'
Assert (Test-Path -LiteralPath (Join-Path $programs 'PDF の関連付け.lnk')) 'Association shortcut missing.'
Write-Output 'PASS install with PDF registration and unchanged existing default'

$sentinel = Join-Path $installPath 'keep-me.pdf'
[System.IO.File]::WriteAllText($sentinel, 'User-created sentinel; uninstall must preserve it.')
Assert ((Run-Setup $installPath) -eq 0) 'Same-directory update failed.'
Run-Uninstall
Assert (Test-Path -LiteralPath $sentinel) 'Uninstall deleted a user-created file.'
Assert (!(Test-Path -LiteralPath (Join-Path $installPath 'SaruPDF.exe'))) 'Uninstall left executable.'
Assert ((Read-Value 'Software\RegisteredApplications' 'SaruPDF') -eq $null) 'RegisteredApplications value left behind.'
Assert ((Read-Value 'Software\Classes\SaruPDF.Pdf\shell\open\command') -eq $null) 'PDF open command left behind.'
Assert ((Default-Snapshot) -eq $before) 'Uninstall changed the original PDF default.'
Assert (!(Test-Path -LiteralPath $programs)) 'Start menu entries left behind.'
Remove-Item -LiteralPath $sentinel
Remove-Item -LiteralPath $installPath
Write-Output 'PASS update/uninstall, registration cleanup and preservation of user files'

$notOwned = Join-Path $testRoot 'not-owned'
New-Item -ItemType Directory -Path $notOwned -Force | Out-Null
$existing = Join-Path $notOwned 'existing.txt'
[System.IO.File]::WriteAllText($existing, 'Existing user file')
Assert ((Run-Setup $notOwned) -eq 2) 'Installer did not reject an unrelated nonempty directory.'
Assert ([System.IO.File]::ReadAllText($existing) -eq 'Existing user file') 'Existing file was changed.'
Assert (!(Test-Path -LiteralPath (Join-Path $notOwned 'SaruPDF.exe'))) 'Installer wrote files into an unrelated folder.'
Remove-Item -LiteralPath $existing
Remove-Item -LiteralPath $notOwned
Write-Output 'PASS unrelated nonempty-directory protection'
Write-Output 'Installer smoke tests passed. Existing PDF default preserved.'
