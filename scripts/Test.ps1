#requires -Version 7.0
. "$PSScriptRoot/Common.ps1"
Push-Location $ProjectRoot
try {
    Invoke-DotNet restore sarupdf.slnx --locked-mode
    Invoke-DotNet run --project tests/sarupdf.Tests -c Release --no-restore
} finally { Pop-Location }
