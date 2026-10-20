<#
Checks the produced nuspecs for every shipping friend of core. Run after packing
all packages so a missing package or dependency group fails instead of being skipped.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagesPath,

    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Write-Host "Core dependency validation: PowerShell $($PSVersionTable.PSVersion); runtime $([System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription); $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
[xml]$core = Get-Content (Join-Path $repositoryRoot 'src/Respire/Respire.csproj') -Raw
$shippingProjects = $props.SelectSingleNode('/Project/PropertyGroup/RespirePackageProjects').InnerText -split ';' |
    ForEach-Object { $_.Trim() } | Where-Object { $_ }
$frameworks = $props.SelectSingleNode('/Project/PropertyGroup/TargetFrameworks').InnerText -split ';'
$friends = @($core.SelectNodes('/Project/ItemGroup/InternalsVisibleTo/@Include') |
    ForEach-Object { $_.Value } | Where-Object { $_ -in $shippingProjects })
$friends += @($shippingProjects | Where-Object {
    [xml]$project = Get-Content (Join-Path $repositoryRoot "src/$_/$_.csproj") -Raw
    $pin = $project.SelectSingleNode('/Project/PropertyGroup/RespirePinCoreDependency')
    $null -ne $pin -and $pin.InnerText -eq 'true'
})
$friends = @($friends | Select-Object -Unique)
if ($friends.Count -eq 0) { throw 'No shipping core friends found; package validation cannot proceed.' }

function Read-PackageNuspec([string]$packageId) {
    # Version selects the exact package filenames produced by this pipeline run.
    $path = Join-Path $PackagesPath "$packageId.$Version.nupkg"
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $path).Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' })
        if ($entries.Count -ne 1) { throw "Expected one nuspec in '$path'." }
        $reader = [System.IO.StreamReader]::new($entries[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        if ($nuspec.package.metadata.id -ne $packageId) { throw "Unexpected package ID in '$path'." }
        return $nuspec
    }
    finally { $archive.Dispose() }
}

$coreNuspec = Read-PackageNuspec 'Respire'
$coreVersion = $coreNuspec.package.metadata.version
foreach ($friend in $friends) {
    $nuspec = Read-PackageNuspec $friend
    $namespaces = [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
    $namespaces.AddNamespace('n', $nuspec.DocumentElement.NamespaceURI)
    foreach ($framework in $frameworks) {
        $dependencies = @($nuspec.SelectNodes("/n:package/n:metadata/n:dependencies/n:group[@targetFramework='$framework']/n:dependency[@id='Respire']", $namespaces))
        if ($dependencies.Count -ne 1) {
            throw "$friend ($framework) must declare exactly one direct Respire dependency."
        }
        if ($dependencies[0].version -cne "[$coreVersion]") {
            throw "$friend ($framework) must pin Respire to [$coreVersion]; found '$($dependencies[0].version)'."
        }
    }
    Write-Host "$friend pins Respire [$coreVersion] for $($frameworks -join ', ')."
}
