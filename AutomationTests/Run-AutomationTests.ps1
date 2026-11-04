#requires -Version 5.1
# Compatibility entry point; the suite owns only isolated test instances and profiles.
param([ValidateSet('Debug','Release')][string]$Configuration='Debug',[switch]$SkipBuild,[switch]$SkipDesktop)
& (Join-Path $PSScriptRoot 'Run-All.ps1') -Configuration $Configuration -SkipBuild:$SkipBuild -SkipDesktop:$SkipDesktop
