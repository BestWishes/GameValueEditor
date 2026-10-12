#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [string]$ApplicationRepository = (Join-Path $PSScriptRoot '..'),
    [string]$ModuleRepository = '',
    [switch]$IncludeModuleArchives,
    [switch]$Apply,
    [switch]$Json
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'local-artifact-cleanup.ps1')
$plan = Get-LocalArtifactCleanupPlan -ApplicationRepository $ApplicationRepository `
    -ModuleRepository $ModuleRepository -IncludeModuleArchives:$IncludeModuleArchives
if ($Json) { $plan | ConvertTo-Json -Depth 8 }
else {
    $plan.Rows | Select-Object Action,Category,Name,Reason | Format-Table -AutoSize
    Write-Host "Preview: $($plan.DeleteCount) obsolete ZIPs, $([math]::Round($plan.DeleteBytes / 1GB, 2)) GiB. Unknown files, directories, receipts and personal data are preserved."
}
if ($Apply) {
    if ($PSCmdlet.ShouldProcess($plan.ApplicationRepository, 'Apply reviewed local ZIP cleanup plan')) {
        Invoke-LocalArtifactCleanup -Plan $plan -Confirm:$false
    }
} else { Write-Host 'Preview only. To remove these generated ZIPs explicitly, rerun with -Apply.' }
