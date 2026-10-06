#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$ApplicationVersion = '',
    [string]$ApplicationRepository = (Join-Path $PSScriptRoot '..'),
    [string]$ModuleRepository = '',
    [string[]]$ModuleIds = @(),
    [string]$OutputPath = '',
    [string]$ExpectedHostSha256 = '',
    [switch]$Force,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offline-bundle.ps1')
Invoke-OfflineBundle @PSBoundParameters
