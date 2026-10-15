"""Reproduce #1745's phase statistics and qualitative EventPipe stack inventory.

Run with Python 3 from any directory. Reads the sibling manifest and ZIPs, verifies
all hashes, and prints JSON. No packages, extraction, or benchmark execution needed.
The stack inventory establishes presence only: exported intervals are not measured
CPU time, per-operation latency, or counts of original EventPipe samples. Profiles
include startup, both clients' setup, warmup, measurement, cleanup and background work.
"""

import hashlib
import json
import math
from pathlib import Path
from zipfile import ZipFile

if not __debug__:
    raise RuntimeError('Run without -O: evidence validation requires assertions.')


def stack_inventory(data):
    frames = [frame['name'] for frame in data['shared']['frames']]
    observed = set()
    leaves = set()
    for profile in data['profiles']:
        assert profile['type'] == 'evented' and profile['unit'] == 'milliseconds'
        stack = []
        previous = profile['startValue']
        for event in profile['events']:
            assert event['at'] >= previous
            if stack and event['at'] > previous and frames[stack[-1]] == 'CPU_TIME':
                observed.update(frames[index] for index in stack[:-1])
                leaves.add(frames[stack[-2]])
            previous = event['at']
            if event['type'] == 'O':
                stack.append(event['frame'])
            else:
                assert event['type'] == 'C'
                opened = stack.pop()
                assert opened == event['frame']
        assert not stack

    groups = {
        'publicationAndTransport': (
            'Respire.RespireClient.PublishAsync', 'RespireConnection+<FlushLoopAsync>',
            'RespireConnection+<ReceiveLoopAsync>', 'RespireConnection.DeliverPush',
            'CompletionScheduler.ExecuteCore', 'AsyncFlushSignal.System.Threading',
            'RedisSubscriber.PublishAsync', 'PubSub.PublishAsync',
        ),
        'deliveryAndConsumption': (
            'SubscriptionHub.Deliver(', 'SubscriptionHub.EnqueueMessage(',
            'SubscriptionBuffer.Write(', 'SubscriptionBuffer+<ReadAllAsync>',
            'BoundedChannelWriter[System.Byte].TryWrite',
            'BoundedChannelReader[System.Byte].WaitToReadAsync',
            'ChannelMessageQueue.Write(', 'ChannelMessageQueue.ReadAsync(',
            'ConnectionMultiplexer.OnMessage(', 'RespPushDispatch.Deliver(',
        ),
        'setupAndHarness': (
            'PubSubComparisonBenchmarks+<Setup>', 'PubSubComparisonBenchmarks+<Cleanup>',
            'RespireConnection+<ConnectCoreAsync>', 'RespireConnection+<HandshakeAsync>',
            'BenchmarkDotNet.Engines.Engine.', 'BenchmarkDotNet.Engines.AnonymousPipesHost.',
        ),
    }
    result = {name: sorted(frame for frame in observed if any(part in frame for part in parts))
              for name, parts in groups.items()}
    result['runtimeLeaves'] = sorted(frame for frame in leaves if any(part in frame for part in (
        'LowLevelLifoSemaphore.', 'LowLevelSpinWaiter.', 'Monitor.Enter_Slowpath',
        'Thread.<PollGC>', 'GC.RunFinalizers', 'Task.InternalWaitCore',
    )))
    result['unresolvedLeafPresent'] = '?!?' in leaves
    result['profileCount'] = len(data['profiles'])
    return result


def analyze(root):
    results = []
    for entry in json.loads((root / 'manifest.json').read_text(encoding='utf-8')):
        archive = root / entry['archive']
        assert hashlib.sha256(archive.read_bytes()).hexdigest() == entry['sha256']
        with ZipFile(archive) as source:
            assert sorted(source.namelist()) == sorted(file['path'] for file in entry['files'])
            for file in entry['files']:
                assert hashlib.sha256(source.read(file['path'])).hexdigest() == file['sha256']
            rig = source.read('rig.txt').decode()
            assert entry['respireCommit'] in rig and entry['run'] in rig
            assets = json.loads(source.read('project.assets.json'))
            assert f"StackExchange.Redis/{entry['stackExchangeRedis']}" in assets['libraries']
            report = json.loads(source.read(
                'results/Respire.ComparisonBenchmarks.PubSubComparisonBenchmarks-report-full-compressed.json'))
            assert len(report['Benchmarks']) == 2
            comparisons = {}
            for case in report['Benchmarks']:
                stats = case['Statistics']
                assert stats['N'] > 0 and case['Measurements']
                comparisons[case['Method']] = {
                    'meanMicroseconds': stats['Mean'] / 1000,
                    'standardDeviationMicroseconds': stats['StandardDeviation'] / 1000,
                    'allocatedBytes': case['Memory']['BytesAllocatedPerOperation'],
                    'retainedIterations': stats['N'],
                }

            phases = json.loads(source.read('pubsub-profile/phases.json'))
            assert phases['SchemaVersion'] == 1 and len(phases['Samples']) == 10000
            assert phases['Protocol'] == 'RESP3' and phases['Subscribers'] == 1
            assert phases['PayloadBytes'] == 18 and phases['WarmupPerClient'] == 500
            summaries = {}
            for client in ('Respire', 'StackExchange.Redis'):
                rows = [row for row in phases['Samples'] if row['Client'] == client]
                assert len(rows) == 5000
                summaries[client] = {'synchronousReads': sum(row['ReadCompletedSynchronously'] for row in rows)}
                for phase in ('AckMicroseconds', 'AfterAckMicroseconds'):
                    values = sorted(row[phase] for row in rows)
                    assert all(math.isfinite(value) and value >= 0 for value in values)
                    summaries[client][phase] = {
                        'mean': sum(values) / len(values),
                        **{name: values[math.ceil(len(values) * p) - 1]
                           for name, p in [('p50', .5), ('p95', .95), ('p99', .99)]},
                    }
            traces = {}
            for method in ('Respire_PublishAndConsume', 'StackExchange_PublishAndConsume'):
                names = [name for name in source.namelist() if method in name and name.endswith('.speedscope.json')]
                assert len(names) == 1
                assert source.read(names[0].replace('.speedscope.json', '.nettrace'))
                traces[names[0]] = stack_inventory(json.loads(source.read(names[0])))
            results.append({'run': entry['run'], 'stackExchangeRedis': entry['stackExchangeRedis'],
                            'comparisons': comparisons, 'phases': summaries, 'traces': traces})
    return results


if __name__ == '__main__':
    print(json.dumps(analyze(Path(__file__).resolve().parent), indent=2))
