$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$generator = Join-Path $repo 'tools/Generate-CommandCatalog.ps1'
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('respire-command-catalog-' + [guid]::NewGuid().ToString('N'))
$redis = Join-Path $temp 'redis'
$valkey = Join-Path $temp 'valkey'
$output = Join-Path $temp 'RespireCommands.g.cs'

function Write-CommandMetadata([string] $Directory, [string] $Name, [string[]] $Flags, [switch] $OmitFlags) {
    $group = if ($Name -like 'EVAL*' -or $Name -like 'FCALL*') {
        'scripting'
    } elseif ($Name -like 'H*') {
        'hash'
    } else {
        'string'
    }
    $definition = [ordered]@{ group = $group }
    if (-not $OmitFlags) {
        $definition.command_flags = @($Flags)
    }
    $json = [ordered]@{}
    $json[$Name] = $definition
    [System.IO.File]::WriteAllText((Join-Path $Directory ($Name.ToLowerInvariant() + '.json')), ($json | ConvertTo-Json -Depth 5))
}

function Assert-GeneratedFlag([string] $Source, [string] $Name, [bool] $Expected) {
    $identifier = $Name -replace '[^A-Z0-9]+', '_'
    $expectedText = $Expected.ToString().ToLowerInvariant()
    $pattern = 'public static readonly RespireCommand ' + [regex]::Escape($identifier) + ' = new\("' +
        [regex]::Escape($Name) + '", [^;]+, ' + $expectedText + '\);'
    if ($Source -notmatch $pattern) {
        throw "Expected generated $Name IsReadOnly=$expectedText."
    }
}

try {
    New-Item -ItemType Directory -Path $redis, $valkey -Force | Out-Null

    foreach ($name in 'GET', 'HGET', 'EVAL_RO', 'EVALSHA_RO', 'FCALL_RO') {
        Write-CommandMetadata $redis $name @('READONLY')
        Write-CommandMetadata $valkey $name @('READONLY')
    }
    foreach ($name in 'SET', 'GETEX', 'EVAL', 'EVALSHA', 'FCALL') {
        Write-CommandMetadata $redis $name @('WRITE')
        Write-CommandMetadata $valkey $name @('WRITE')
    }
    Write-CommandMetadata $redis 'MIXED' @('READONLY')
    Write-CommandMetadata $valkey 'MIXED' @('WRITE')
    Write-CommandMetadata $redis 'MISSING' @('READONLY')
    Write-CommandMetadata $valkey 'MISSING' @() -OmitFlags

    & pwsh -NoProfile -File $generator -RedisCommandPath $redis -ValkeyCommandPath $valkey `
        -RedisVersion fixture -ValkeyVersion fixture -OutputPath $output
    if ($LASTEXITCODE -ne 0) {
        throw "Command catalog generator failed with exit code $LASTEXITCODE."
    }

    $source = Get-Content -Raw -LiteralPath $output
    foreach ($name in 'GET', 'HGET', 'EVAL_RO', 'EVALSHA_RO', 'FCALL_RO') {
        Assert-GeneratedFlag $source $name $true
    }
    foreach ($name in 'SET', 'GETEX', 'EVAL', 'EVALSHA', 'FCALL', 'MIXED', 'MISSING') {
        Assert-GeneratedFlag $source $name $false
    }

    Write-Host 'Command catalog read-only aggregation checks passed.'
}
finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
