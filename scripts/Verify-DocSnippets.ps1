[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagesPath,

    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$resolvedPackagesPath = (Resolve-Path -LiteralPath $PackagesPath).Path
$generatedDirectory = Join-Path $repositoryRoot 'artifacts/doc-tests/generated'
$generatedPath = Join-Path $generatedDirectory 'GeneratedSnippets.g.cs'
$nugetConfigPath = Join-Path $generatedDirectory 'NuGet.config'
$restorePackagesPath = Join-Path $generatedDirectory 'packages'
$projectPath = Join-Path $repositoryRoot 'tests/Respire.DocTests/Respire.DocTests.csproj'
$documentPaths = @(
    (Join-Path $repositoryRoot 'README.md')
    Get-ChildItem (Join-Path $repositoryRoot 'website/docs') -Recurse -File -Include '*.md', '*.mdx' |
        Sort-Object FullName |
        ForEach-Object FullName
)

$requiredPackages = @(
    'Respire'
    'Respire.Extensions.Caching'
    'Respire.Extensions.Caching.Hybrid'
    'Respire.Extensions.DependencyInjection'
)

foreach ($packageId in $requiredPackages)
{
    $packagePath = Join-Path $resolvedPackagesPath "$packageId.$Version.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf))
    {
        throw "Documented package '$packageId' was not packed at '$packagePath'."
    }
}

$snippets = [System.Collections.Generic.List[object]]::new()
$installPackageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$projectCommands = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

foreach ($documentPath in $documentPaths)
{
    $relativePath = [IO.Path]::GetRelativePath($repositoryRoot, $documentPath).Replace('\', '/')
    $lines = Get-Content -LiteralPath $documentPath
    $csharpOrdinal = 0

    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++)
    {
        if ($lines[$lineIndex] -match '^```csharp\s*$')
        {
            $csharpOrdinal++
            $startLine = $lineIndex + 2
            $body = [System.Collections.Generic.List[string]]::new()
            for ($lineIndex++; $lineIndex -lt $lines.Count -and $lines[$lineIndex] -notmatch '^```\s*$'; $lineIndex++)
            {
                $body.Add($lines[$lineIndex])
            }

            if ($lineIndex -ge $lines.Count)
            {
                throw "Unclosed C# fence at ${relativePath}:$startLine."
            }

            $directive = if ($startLine -ge 3) { $lines[$startLine - 3].Trim() } else { '' }
            $ignoreReason = $null
            if ($directive -match '^<!--\s*doc-test-ignore:\s*(.+?)\s*-->$')
            {
                $ignoreReason = $Matches[1]
            }

            $ignored = $null -ne $ignoreReason
            if ($directive -match '^<!--\s*doc-test-ignore' -and -not $ignored)
            {
                throw "Malformed doc-test-ignore directive before ${relativePath}:$startLine. Include a non-empty reason."
            }

            $mode = 'statements'
            $splitBefore = $null
            if ($directive -match '^<!--\s*doc-test-declaration:\s*split-before=(.+?)\s*-->$')
            {
                $mode = 'declaration'
                $splitBefore = $Matches[1]
            }
            elseif ($directive -match '^<!--\s*doc-test-tail-declaration:\s*split-before=(.+?)\s*-->$')
            {
                $mode = 'tail-declaration'
                $splitBefore = $Matches[1]
            }
            elseif ($directive -match '^<!--\s*doc-test-declaration\s*-->$')
            {
                $mode = 'declaration'
            }
            elseif ($directive -match '^<!--\s*doc-test-' -and -not $ignored)
            {
                throw "Unknown or malformed doc-test directive before ${relativePath}:$startLine."
            }

            $source = $body -join "`n"
            if ($ignored)
            {
                Write-Host "EXCLUDED $relativePath#$csharpOrdinal ($ignoreReason)"
                continue
            }

            $snippets.Add([pscustomobject]@{
                Id = "$relativePath#$csharpOrdinal"
                SourcePath = $documentPath.Replace('\', '/')
                Line = $startLine
                Source = $source
                Mode = $mode
                SplitBefore = $splitBefore
            })

            continue
        }

        if ($lines[$lineIndex] -match '^```(?:bash|sh|shell|powershell|pwsh)\s*$')
        {
            for ($lineIndex++; $lineIndex -lt $lines.Count -and $lines[$lineIndex] -notmatch '^```\s*$'; $lineIndex++)
            {
                $command = $lines[$lineIndex].Trim()
                if ($command -match '^dotnet add package\s+([A-Za-z0-9_.-]+)')
                {
                    [void]$installPackageIds.Add($Matches[1])
                }

                if ($command -match '^dotnet run\s+.*--project\s+([^\s]+)')
                {
                    [void]$projectCommands.Add($Matches[1].Trim("'`""))
                }
                elseif ($command -match '^dotnet test\s+--project\s+([^\s]+)')
                {
                    [void]$projectCommands.Add($Matches[1].Trim("'`""))
                }
                elseif ($command -match '^dotnet test\s+(?!-)([^\s]+)')
                {
                    [void]$projectCommands.Add($Matches[1].Trim("'`""))
                }
            }
        }
    }
}

foreach ($packageId in $installPackageIds)
{
    if ($packageId -notin $requiredPackages)
    {
        throw "Shell sample references unexpected package ID '$packageId'."
    }
}

foreach ($relativeProjectPath in $projectCommands)
{
    $commandPath = Join-Path $repositoryRoot $relativeProjectPath
    $projectExists = Test-Path -LiteralPath $commandPath -PathType Leaf
    if (Test-Path -LiteralPath $commandPath -PathType Container)
    {
        $projectExists = $null -ne (Get-ChildItem -LiteralPath $commandPath -File |
            Where-Object { $_.Extension -in '.csproj', '.fsproj', '.vbproj' } |
            Select-Object -First 1)
    }

    if (-not $projectExists)
    {
        throw "Shell sample references missing project '$relativeProjectPath'."
    }
}

New-Item -ItemType Directory -Force -Path $generatedDirectory | Out-Null
if (Test-Path -LiteralPath $restorePackagesPath)
{
    Remove-Item -LiteralPath $restorePackagesPath -Recurse -Force
}

$builder = [Text.StringBuilder]::new()
[void]$builder.AppendLine('// <auto-generated />')
[void]$builder.AppendLine('using System.Text.Json.Serialization;')
[void]$builder.AppendLine('using Microsoft.Extensions.Caching.Distributed;')
[void]$builder.AppendLine('using Microsoft.Extensions.Configuration;')
[void]$builder.AppendLine('using Microsoft.Extensions.DependencyInjection;')
[void]$builder.AppendLine('using Microsoft.Extensions.Logging;')
[void]$builder.AppendLine('using OpenTelemetry.Metrics;')
[void]$builder.AppendLine('using OpenTelemetry.Trace;')
[void]$builder.AppendLine('using Respire;')
[void]$builder.AppendLine('using Respire.Extensions.Caching;')
[void]$builder.AppendLine('using Respire.Extensions.Caching.Hybrid;')
[void]$builder.AppendLine('using Respire.Extensions.DependencyInjection;')
[void]$builder.AppendLine('using Respire.Serialization;')
[void]$builder.AppendLine('#pragma warning disable')

$usingPattern = '(?m)^\s*(?:global\s+)?using\s+(?:(?:static\s+)?[A-Za-z_][A-Za-z0-9_.]*|[A-Za-z_][A-Za-z0-9_]*\s*=\s*(?:global::)?[A-Za-z_][A-Za-z0-9_.]*)\s*;\s*(?://.*)?$'
for ($snippetIndex = 0; $snippetIndex -lt $snippets.Count; $snippetIndex++)
{
    $snippet = $snippets[$snippetIndex]
    $usingDirectives = [regex]::Matches($snippet.Source, $usingPattern) |
        ForEach-Object { $_.Value.Trim() -replace '^global\s+', '' }
    $source = [regex]::Replace($snippet.Source, $usingPattern, '')
    $source = $source -replace '(?m)^\s*#pragma\s+warning\s+(?:disable|restore).*$', ''

    $declarationLine = $snippet.Line
    $statementLine = $snippet.Line
    $sourceSplitIndex = -1
    if ($null -ne $snippet.SplitBefore)
    {
        $originalSplitIndex = $snippet.Source.IndexOf($snippet.SplitBefore, [StringComparison]::Ordinal)
        $sourceSplitIndex = $source.IndexOf($snippet.SplitBefore, [StringComparison]::Ordinal)
        if ($originalSplitIndex -lt 0 -or $sourceSplitIndex -lt 0)
        {
            throw "Split marker '$($snippet.SplitBefore)' was not found in $($snippet.Id)."
        }

        $splitLine = $snippet.Line +
            [regex]::Matches($snippet.Source.Substring(0, $originalSplitIndex), "`n").Count
        if ($snippet.Mode -eq 'declaration')
        {
            $statementLine = $splitLine
        }
        else
        {
            $declarationLine = $splitLine
        }
    }

    [void]$builder.AppendLine('namespace Respire.DocTests')
    [void]$builder.AppendLine('{')
    foreach ($usingDirective in $usingDirectives)
    {
        [void]$builder.AppendLine($usingDirective)
    }
    [void]$builder.AppendLine("internal sealed partial class Snippet$snippetIndex : SnippetContext")
    [void]$builder.AppendLine('{')

    if ($snippet.Mode -eq 'declaration')
    {
        if ($null -eq $snippet.SplitBefore)
        {
            $declarationSource = $source.TrimEnd()
            $source = ''
        }
        else
        {
            $declarationSource = $source.Substring(0, $sourceSplitIndex).TrimEnd()
            $source = $source.Substring($sourceSplitIndex)
        }

        [void]$builder.AppendLine("#line $declarationLine `"$($snippet.SourcePath)`"")
        foreach ($line in ($declarationSource -split "`n"))
        {
            [void]$builder.AppendLine("    $line")
        }
        [void]$builder.AppendLine('#line default')
    }
    elseif ($snippet.Mode -eq 'tail-declaration')
    {
        $declarationSource = $source.Substring($sourceSplitIndex).TrimEnd()
        $source = $source.Substring(0, $sourceSplitIndex).TrimEnd()
        [void]$builder.AppendLine("#line $declarationLine `"$($snippet.SourcePath)`"")
        foreach ($line in ($declarationSource -split "`n"))
        {
            [void]$builder.AppendLine("    $line")
        }
        [void]$builder.AppendLine('#line default')
    }

    [void]$builder.AppendLine('    public static async Task RunAsync()')
    [void]$builder.AppendLine('    {')
    if (-not [string]::IsNullOrWhiteSpace($source))
    {
        [void]$builder.AppendLine("#line $statementLine `"$($snippet.SourcePath)`"")
        foreach ($line in ($source -split "`n"))
        {
            [void]$builder.AppendLine("        $line")
        }
        [void]$builder.AppendLine('#line default')
    }
    [void]$builder.AppendLine('    }')
    [void]$builder.AppendLine('}')
    [void]$builder.AppendLine('}')
}

[void]$builder.AppendLine('namespace Respire.DocTests')
[void]$builder.AppendLine('{')
[void]$builder.AppendLine('internal static class SnippetCatalog')
[void]$builder.AppendLine('{')
[void]$builder.AppendLine('    public static void Report() =>')
[void]$builder.AppendLine("        Console.WriteLine(`"Compiled $($snippets.Count) C# documentation snippets.`");")
[void]$builder.AppendLine('}')
[void]$builder.AppendLine('}')

[IO.File]::WriteAllText($generatedPath, $builder.ToString())

[IO.File]::WriteAllText(
    $nugetConfigPath,
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-packages" value="$([Security.SecurityElement]::Escape($resolvedPackagesPath))" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@)

& dotnet restore $projectPath --configfile $nugetConfigPath --force --packages $restorePackagesPath `
    "-p:RespirePackageVersion=$Version" `
    "-p:GeneratedSnippetsPath=$generatedPath"

if ($LASTEXITCODE -ne 0)
{
    throw "Documentation snippet restore failed with exit code $LASTEXITCODE."
}

& dotnet run --project $projectPath -c Release --no-restore `
    "-p:RespirePackageVersion=$Version" `
    "-p:GeneratedSnippetsPath=$generatedPath"

if ($LASTEXITCODE -ne 0)
{
    throw "Documentation snippet project failed with exit code $LASTEXITCODE."
}
