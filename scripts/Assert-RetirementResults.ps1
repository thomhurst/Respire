[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
# The original five-class regression set has 51 cases on both supported frameworks.
# Keep a floor rather than an exact count so additional categorized cases are welcome.
$minimumExecuted = 51
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File)
if ($reports.Count -eq 0) { throw 'The constrained retirement run produced no TRX reports.' }

$executed = 0
$passed = 0
$notExecuted = 0
foreach ($report in $reports) {
    [xml]$results = Get-Content -LiteralPath $report.FullName -Raw
    $counters = $results.SelectSingleNode('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
    if ($null -eq $counters) { throw "Missing TRX result counters: $($report.Name)" }
    foreach ($attribute in @('executed', 'passed', 'notExecuted')) {
        $value = 0
        if (-not [int]::TryParse($counters.GetAttribute($attribute), [ref]$value) -or $value -lt 0) {
            throw "Invalid TRX $attribute counter: $($report.Name)"
        }
    }
    $executed += [int]$counters.GetAttribute('executed')
    $passed += [int]$counters.GetAttribute('passed')
    $notExecuted += [int]$counters.GetAttribute('notExecuted')
}
if ($executed -lt $minimumExecuted -or $passed -ne $executed -or $notExecuted -ne 0) {
    throw "Incomplete constrained retirement coverage: executed=$executed, passed=$passed, notExecuted=$notExecuted; require at least $minimumExecuted passing cases and no skips."
}
Write-Output "Constrained retirement coverage verified: $passed passing cases, no skips."
