<#
.SYNOPSIS
Verifies compiled package local initialization and the excluded solution assemblies.
.DESCRIPTION
Run after a Release solution build. Reads PE metadata without loading dependencies.
Every package must have both supported outputs; every solution project must have an output.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$Configuration = 'Release',
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-LocalInitializationPolicy([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $skip = $false
        foreach ($handle in $metadata.GetModuleDefinition().GetCustomAttributes()) {
            $attribute = $metadata.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne 'MemberReference') { continue }
            $constructor = $metadata.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if ($constructor.Parent.Kind -ne 'TypeReference') { continue }
            $type = $metadata.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
            if ($metadata.GetString($type.Namespace) -eq 'System.Runtime.CompilerServices' -and
                $metadata.GetString($type.Name) -eq 'SkipLocalsInitAttribute') { $skip = $true }
        }
        $initialized = 0
        $uninitialized = 0
        foreach ($handle in $metadata.MethodDefinitions) {
            $method = $metadata.GetMethodDefinition($handle)
            if ($method.RelativeVirtualAddress -eq 0) { continue }
            $body = [System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody($pe, $method.RelativeVirtualAddress)
            if ($body.LocalSignature.IsNil) { continue }
            if ($body.LocalVariablesInitialized) { $initialized++ } else { $uninitialized++ }
        }
        [pscustomobject]@{ Path = $Path; SkipLocalsInit = $skip; InitializedBodies = $initialized; UninitializedBodies = $uninitialized }
    }
    finally { $pe.Dispose(); $stream.Dispose() }
}

[xml]$props = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
$packageNames = @($props.SelectSingleNode('//RespirePackageProjects').InnerText.Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
    ForEach-Object { $_.Trim() } | Where-Object { $_ })
$frameworks = $props.SelectSingleNode('//TargetFrameworks').InnerText.Split(';')
[xml]$solution = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Respire.slnx') -Raw
$reports = [Collections.Generic.List[object]]::new()
foreach ($project in $solution.SelectNodes('//Project')) {
    $projectPath = Join-Path $RepositoryRoot $project.Path
    $name = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $outputRoot = Join-Path (Split-Path $projectPath -Parent) "bin/$Configuration"
    $package = $name -in $packageNames
    $outputs = @(Get-ChildItem -LiteralPath $outputRoot -Filter "$name.dll" -File -Recurse |
        Where-Object { $_.FullName -notmatch '[/\\](ref|refint)[/\\]' })
    if ($outputs.Count -eq 0) { throw "Missing compiled output for $name. Build the solution first." }
    if ($package) {
        foreach ($framework in $frameworks) {
            $expected = Join-Path $outputRoot "$framework/$name.dll"
            if (!(Test-Path -LiteralPath $expected)) { throw "Missing package output: $expected" }
        }
    }
    foreach ($output in $outputs) {
        $policy = Get-LocalInitializationPolicy $output.FullName
        if ($policy.SkipLocalsInit -ne $package) { throw "Wrong module attribute policy: $($output.FullName)" }
        # Synthesized helpers can retain initlocals independently of the module policy
        # (for example <>z__ReadOnlySingleElementList<T>.Enumerator.MoveNext).
        # Verify the opt-out takes effect without requiring the compiler to remove every flag.
        if ($package -and $policy.UninitializedBodies -eq 0) {
            throw "Package did not compile with uninitialized local bodies: $($output.FullName)"
        }
        if (!$package -and $policy.UninitializedBodies -ne 0) {
            throw "Excluded assembly contains uninitialized local bodies: $($output.FullName)"
        }
        $reports.Add($policy)
    }
}
$observedPackages = @($reports | Where-Object SkipLocalsInit | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Path) } | Sort-Object -Unique)
if (@(Compare-Object ($packageNames | Sort-Object) $observedPackages).Count -ne 0) { throw 'Package inventory differs from the solution outputs.' }
if ($ReportPath) { $reports | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $ReportPath }
foreach ($report in $reports) {
    $assembly = [IO.Path]::GetFileName($report.Path)
    $framework = [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($report.Path))
    Write-Output "$assembly ($framework): SkipLocalsInit=$($report.SkipLocalsInit); initialized bodies=$($report.InitializedBodies); uninitialized bodies=$($report.UninitializedBodies)"
}
