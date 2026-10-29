#requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
. "$PSScriptRoot/Common.ps1"
Push-Location $ProjectRoot
try {
    Invoke-DotNet restore sarupdf.slnx --locked-mode
    Invoke-DotNet build sarupdf.slnx -c $Configuration --no-restore
} finally { Pop-Location }
