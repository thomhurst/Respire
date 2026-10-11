<#
.SYNOPSIS
Evaluates vector-set replacement durability in a disposable Redis container.

.DESCRIPTION
Requires Docker and a locally available Redis image with vector sets. No host ports
are published. Reports whether direct VADD, Lua VADD, and Lua VREM/VADD replacements
survive an abrupt kill and AOF reload. A false result is evidence of a server
limitation, not a failing assertion about Respire. Insertion is a positive control.
Remove/add is a diagnostic only: Lua does not roll back a failed add after removal.

.EXAMPLE
./scripts/Test-VectorSetDurability.ps1 -Image redis:8.10-alpine | ConvertTo-Json -Depth 6
#>
[CmdletBinding()]
param([string]$Image = 'redis:8.6.0-alpine')

$ErrorActionPreference = 'Stop'
$container = $null

function Invoke-Docker([string[]]$DockerArguments) {
    $output = & docker @DockerArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Docker failed: $([string]::Join([Environment]::NewLine, $output))"
    }
    [string]::Join([Environment]::NewLine, $output)
}

function Invoke-Redis([string[]]$RedisArguments) {
    $json = Invoke-Docker -DockerArguments (@('exec', $container, 'redis-cli', '-2', '-e', '--json') + $RedisArguments)
    ConvertFrom-Json -InputObject $json -NoEnumerate
}

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if (($Actual -join ',') -cne ($Expected -join ',')) {
        throw "$Message Expected [$($Expected -join ',')], got [$($Actual -join ',')]."
    }
}

function Wait-RedisReady {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            Assert-Equal (Invoke-Redis -RedisArguments @('PING')) 'PONG' 'Redis is not ready.'
            return
        }
        catch {
            $lastFailure = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt 30)
    throw "Redis did not become ready within 30 seconds. Last failure: $lastFailure"
}

try {
    $container = Invoke-Docker -DockerArguments @('run', '--detach', '--pull', 'never', $Image,
        'redis-server', '--appendonly', 'yes', '--appendfsync', 'always',
        '--auto-aof-rewrite-percentage', '0', '--save', '')
    Wait-RedisReady
    $info = Invoke-Docker -DockerArguments @('exec', $container, 'redis-cli', '--raw', 'INFO', 'server')
    $version = [regex]::Match($info, '(?m)^redis_version:([^\r\n]+)').Groups[1].Value
    $imageId = Invoke-Docker -DockerArguments @('inspect', '--format', '{{.Image}}', $container)
    $original = '{"revision":1}'
    $replacement = '{"revision":2}'
    $luaAdd = "return redis.call('VADD',KEYS[1],'VALUES',3,0,1,0,'a','NOQUANT','SETATTR',ARGV[1])"
    $luaReplace = "redis.call('VREM',KEYS[1],'a'); " + $luaAdd

    foreach ($key in @('control', 'direct', 'lua', 'remove-add', 'remove-add-surviving')) {
        Assert-Equal (Invoke-Redis -RedisArguments @('VADD', $key, 'VALUES', '3', '1', '0', '0', 'a',
            'NOQUANT', 'SETATTR', $original)) 1 "Insertion failed for $key."
    }
    Assert-Equal (Invoke-Redis -RedisArguments @('VADD', 'remove-add-surviving', 'VALUES', '3', '0', '0', '1',
        'sentinel', 'NOQUANT', 'SETATTR', $original)) 1 'Surviving-set sentinel insertion failed.'
    Assert-Equal (Invoke-Redis -RedisArguments @('VADD', 'direct', 'VALUES', '3', '0', '1', '0', 'a',
        'NOQUANT', 'SETATTR', $replacement)) 0 'Replacement must report an existing member.'
    Assert-Equal (Invoke-Redis -RedisArguments @('EVAL', $luaAdd, '1', 'lua', $replacement)) 0 'Lua replacement failed.'
    Assert-Equal (Invoke-Redis -RedisArguments @('EVAL', $luaReplace, '1', 'remove-add', $replacement)) 1 'Remove/add failed.'
    Assert-Equal (Invoke-Redis -RedisArguments @('EVAL', $luaReplace, '1', 'remove-add-surviving', $replacement)) 1 'Surviving-set remove/add failed.'
    foreach ($key in @('direct', 'lua', 'remove-add', 'remove-add-surviving')) {
        Assert-Equal ([double[]](Invoke-Redis -RedisArguments @('VEMB', $key, 'a'))) @(0, 1, 0) "Replacement vector is wrong for $key."
        Assert-Equal (Invoke-Redis -RedisArguments @('VGETATTR', $key, 'a')) $replacement "Replacement attributes are wrong for $key."
    }

    # Keep the set alive so dimension and quantization settings survive removal of a.
    Assert-Equal (Invoke-Redis -RedisArguments @('VADD', 'failure', 'VALUES', '3', '1', '0', '0',
        'sentinel', 'NOQUANT')) 1 'Failure-path sentinel insertion failed.'
    foreach ($invalidAdd in @(
        "redis.call('VADD',KEYS[1],'VALUES',2,1,0,'a','NOQUANT')",
        "redis.call('VADD',KEYS[1],'VALUES',3,0,1,0,'a')"
    )) {
        Assert-Equal (Invoke-Redis -RedisArguments @('VADD', 'failure', 'VALUES', '3', '1', '0', '0',
            'a', 'NOQUANT')) 1 'Failure-path original insertion failed.'
        $script = "redis.call('VREM',KEYS[1],'a'); return " + $invalidAdd
        try {
            Invoke-Redis -RedisArguments @('EVAL', $script, '1', 'failure') | Out-Null
            throw 'Remove/add unexpectedly succeeded.'
        }
        catch {
            if ($_.Exception.Message -notmatch '(?i)dimension|quantization') { throw }
        }
        Assert-Equal (Invoke-Redis -RedisArguments @('VISMEMBER', 'failure', 'a')) 0 'Lua unexpectedly restored the removed record.'
    }

    # SIGKILL prevents an orderly shutdown from persisting an RDB snapshot.
    Invoke-Docker -DockerArguments @('kill', '--signal', 'KILL', $container) | Out-Null
    Invoke-Docker -DockerArguments @('start', $container) | Out-Null
    Wait-RedisReady
    Assert-Equal ([double[]](Invoke-Redis -RedisArguments @('VEMB', 'control', 'a'))) @(1, 0, 0) 'Insertion control did not survive AOF reload.'
    Assert-Equal (Invoke-Redis -RedisArguments @('VGETATTR', 'control', 'a')) $original 'Insertion attributes did not survive AOF reload.'
    Assert-Equal ([double[]](Invoke-Redis -RedisArguments @('VEMB', 'remove-add-surviving', 'sentinel'))) @(0, 0, 1) 'Surviving-set sentinel vector did not survive AOF reload.'
    Assert-Equal (Invoke-Redis -RedisArguments @('VGETATTR', 'remove-add-surviving', 'sentinel')) $original 'Surviving-set sentinel attributes did not survive AOF reload.'
    Assert-Equal (Invoke-Redis -RedisArguments @('VCARD', 'remove-add-surviving')) 2 'Surviving set must retain both members.'
    foreach ($key in @('direct', 'lua', 'remove-add', 'remove-add-surviving')) {
        $vector = [double[]](Invoke-Redis -RedisArguments @('VEMB', $key, 'a'))
        $attributes = Invoke-Redis -RedisArguments @('VGETATTR', $key, 'a')
        [pscustomobject]@{
            Image = $Image
            ImageId = $imageId
            RedisVersion = $version
            Operation = $key
            ReplacementSurvives = (($vector -join ',') -eq '0,1,0' -and $attributes -ceq $replacement)
            ReloadedVector = $vector
            ReloadedAttributes = $attributes
        }
    }
}
finally {
    if ($container) { Invoke-Docker -DockerArguments @('rm', '--force', '--volumes', $container) | Out-Null }
}
