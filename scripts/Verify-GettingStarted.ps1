<#
Builds the getting-started page's exact Program.cs against freshly packed packages.
No snippet harness or repository build defaults are available to hide missing setup.
Use -UseAgentGuard for local agent validation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagesPath,

    [Parameter(Mandatory)]
    [string]$Version,

    [switch]$UseAgentGuard
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$packages = (Resolve-Path -LiteralPath $PackagesPath).Path
foreach ($package in @('Respire', 'Respire.DependencyInjection')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packages "$package.$Version.nupkg"))) {
        throw "Missing packed package $package.$Version.nupkg."
    }
}

$document = Get-Content -LiteralPath (Join-Path $repositoryRoot 'website/docs/getting-started.md') -Raw
$snippets = [regex]::Matches($document, '(?ms)^```csharp title="Program.cs"\r?\n(?<source>.*?)^```\s*$')
if ($snippets.Count -ne 1) {
    throw 'Expected exactly one complete Program.cs C# fence in getting-started.md.'
}

$generated = Join-Path $repositoryRoot 'artifacts/getting-started'
New-Item -ItemType Directory -Force -Path $generated | Out-Null
[IO.File]::WriteAllText((Join-Path $generated 'Program.cs'), $snippets[0].Groups['source'].Value)
# Stop directory-based imports here: the sample must supply all its own settings.
foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
    [IO.File]::WriteAllText((Join-Path $generated $file), '<Project />')
}
$project = Join-Path $generated 'RespireQuickstart.csproj'
[IO.File]::WriteAllText($project, @'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <LangVersion>13</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Respire.DependencyInjection" Version="$(RespirePackageVersion)" />
  </ItemGroup>
</Project>
'@)

$arguments = @(
    'build', $project, '-c', 'Release',
    "-p:RespirePackageVersion=$Version", "-p:RestoreAdditionalProjectSources=$packages",
    "-p:RestorePackagesPath=$(Join-Path $repositoryRoot 'artifacts/getting-started-packages')"
)
if ($UseAgentGuard) {
    & (Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1') -SingleNode -DotNetArguments $arguments
}
else {
    & dotnet @arguments
}
if ($LASTEXITCODE -ne 0) {
    throw "Getting-started app build failed with exit code $LASTEXITCODE."
}
