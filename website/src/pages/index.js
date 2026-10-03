import {useRef, useState} from 'react';
import clsx from 'clsx';
import Link from '@docusaurus/Link';
import CodeBlock from '@theme/CodeBlock';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';
import ByteField from '@site/src/components/home/ByteField';
import Encoder from '@site/src/components/home/Encoder';
import BlockingDemo from '@site/src/components/home/BlockingDemo';
import CacheRace from '@site/src/components/home/CacheRace';
import PipelineDemo from '@site/src/components/home/PipelineDemo';
import WireTicker from '@site/src/components/home/WireTicker';
import styles from './index.module.css';

// One RESP line is [prefix, payload]. An empty prefix marks the payload line
// that follows a `$` length header.
const traces = [
  {
    id: 'get',
    label: 'GET',
    code: 'string? name = await redis.GetStringAsync("user:42:name");',
    request: [['*', '2'], ['$', '3'], ['', 'GET'], ['$', '12'], ['', 'user:42:name']],
    reply: [['$', '3'], ['', 'Ada']],
    result: 'name is "Ada"',
    resultType: 'string?',
  },
  {
    id: 'set',
    label: 'SET with expiry',
    code: 'await redis.SetAsync("greeting", "hello", expiry: TimeSpan.FromMinutes(5));',
    request: [['*', '5'], ['$', '3'], ['', 'SET'], ['$', '8'], ['', 'greeting'], ['$', '5'], ['', 'hello'], ['$', '2'], ['', 'PX'], ['$', '6'], ['', '300000']],
    reply: [['+', 'OK']],
    result: 'the call completes',
    resultType: 'ValueTask',
  },
  {
    id: 'incr',
    label: 'INCR',
    code: 'long visits = await redis.IncrementAsync("visits");',
    request: [['*', '2'], ['$', '4'], ['', 'INCR'], ['$', '6'], ['', 'visits']],
    reply: [[':', '1024']],
    result: 'visits is 1024',
    resultType: 'long',
  },
  {
    id: 'hgetall',
    label: 'HGETALL',
    code: 'Dictionary<string, string> user = await redis.Hashes.GetAllAsync("user:42");',
    request: [['*', '2'], ['$', '7'], ['', 'HGETALL'], ['$', '7'], ['', 'user:42']],
    reply: [['%', '2'], ['$', '4'], ['', 'name'], ['$', '3'], ['', 'Ada'], ['$', '6'], ['', 'logins'], ['$', '1'], ['', '7']],
    result: 'user["name"] is "Ada"',
    resultType: 'Dictionary<string, string>',
  },
  {
    id: 'error',
    label: 'Wrong type',
    code: 'await redis.IncrementAsync("user:42"); // user:42 is a hash',
    request: [['*', '2'], ['$', '4'], ['', 'INCR'], ['$', '7'], ['', 'user:42']],
    reply: [['-', 'WRONGTYPE Operation against a key holding the wrong kind of value']],
    result: 'throws RespireServerException',
    resultType: 'exception',
  },
  {
    id: 'push',
    label: 'Cache invalidation',
    code: '// Another client runs: SET user:42:name Grace',
    requestNote: 'Nothing. Redis starts this conversation.',
    reply: [['>', '2'], ['$', '10'], ['', 'invalidate'], ['*', '1'], ['$', '12'], ['', 'user:42:name']],
    result: 'the local copy of user:42:name is evicted',
    resultType: 'client-side cache',
  },
];

const prefixes = [
  {glyph: '+', name: 'Simple string', kind: 'simple'},
  {glyph: '-', name: 'Error', kind: 'error'},
  {glyph: ':', name: 'Integer', kind: 'integer'},
  {glyph: '$', name: 'Bulk string', kind: 'bulk'},
  {glyph: '* %', name: 'Array, map', kind: 'array'},
  {glyph: '>', name: 'Push', kind: 'push'},
];

const kindOf = {'+': 'simple', '-': 'error', ':': 'integer', $: 'bulk', '*': 'array', '%': 'array', '>': 'push', '': 'payload'};

function Frame({lines}) {
  return (
    <code className={styles.frame}>
      {lines.map(([prefix, payload], index) => (
        <span key={index} className={clsx(styles.frameLine, styles[kindOf[prefix]])} style={{'--i': index}}>
          <b>{prefix}</b>{payload}<i>\r\n</i>
        </span>
      ))}
    </code>
  );
}

function WireTrace() {
  const [active, setActive] = useState(traces[0].id);
  const trace = traces.find((item) => item.id === active);

  // Arrow keys, Home and End move between tabs, as in the WAI-ARIA tabs pattern.
  const onKeyDown = (event) => {
    const index = traces.findIndex((item) => item.id === active);
    const next = {
      ArrowRight: (index + 1) % traces.length,
      ArrowLeft: (index - 1 + traces.length) % traces.length,
      Home: 0,
      End: traces.length - 1,
    }[event.key];
    if (next === undefined) {
      return;
    }
    event.preventDefault();
    setActive(traces[next].id);
    document.getElementById(`trace-tab-${traces[next].id}`)?.focus();
  };

  return (
    <div className={styles.trace}>
      <div className={styles.traceTabs} role="tablist" aria-label="Example commands">
        {traces.map((item) => (
          <button
            key={item.id}
            type="button"
            role="tab"
            id={`trace-tab-${item.id}`}
            aria-selected={item.id === active}
            aria-controls="trace-panel"
            tabIndex={item.id === active ? 0 : -1}
            className={clsx(styles.traceTab, item.id === active && styles.traceTabActive)}
            onClick={() => setActive(item.id)}
            onKeyDown={onKeyDown}>
            {item.label}
          </button>
        ))}
      </div>
      <div className={styles.tracePanel} id="trace-panel" role="tabpanel" aria-labelledby={`trace-tab-${trace.id}`} tabIndex={0} key={trace.id}>
        <div className={styles.traceCode}>
          <h3>You write</h3>
          <CodeBlock language="csharp">{trace.code}</CodeBlock>
        </div>
        <div className={styles.traceStep}>
          <h3>Respire sends</h3>
          {trace.request ? <Frame lines={trace.request} /> : <p className={styles.traceNote}>{trace.requestNote}</p>}
        </div>
        <div className={styles.traceStep}>
          <h3>{trace.request ? 'Redis replies' : 'Redis pushes'}</h3>
          <Frame lines={trace.reply} />
        </div>
        <div className={styles.traceStep}>
          <h3>You get</h3>
          <p className={styles.traceResult}>
            <strong>{trace.result}</strong>
            <span>{trace.resultType}</span>
          </p>
        </div>
      </div>
      <ul className={styles.legend} aria-label="RESP type prefixes">
        {prefixes.map((prefix) => (
          <li key={prefix.name} className={styles[prefix.kind]}>
            <b>{prefix.glyph}</b>{prefix.name}
          </li>
        ))}
      </ul>
    </div>
  );
}

function InstallCommand({className}) {
  const command = 'dotnet add package Respire --prerelease';
  const [copied, setCopied] = useState(false);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 1600);
    } catch {
      setCopied(false);
    }
  };

  return (
    <div className={clsx(styles.install, className)}>
      <code>{command}</code>
      <button type="button" onClick={copy} aria-live="polite">{copied ? 'Copied' : 'Copy'}</button>
    </div>
  );
}

// A giant RESP fragment used as a section's display line. Each line is
// [prefix, payload]; the CRLF terminator is drawn small after it.
function Fragment({lines, className}) {
  return (
    <p className={clsx(styles.fragment, className)} aria-hidden="true">
      {lines.map(([prefix, payload], index) => (
        <span key={index} className={styles[kindOf[prefix]]}>
          <b>{prefix}</b>{payload}<i>\r\n</i>
        </span>
      ))}
    </p>
  );
}

function Hero() {
  const field = useRef(null);

  return (
    <header className={styles.hero}>
      <ByteField ref={field} className={styles.field} />
      <div className={clsx('container', styles.heroInner)}>
        <div className={styles.heroCopy}>
          <Heading as="h1" className={styles.wordmark} aria-label="Respire">
            <span className={styles.wordmarkLength} aria-hidden="true">$7<i>\r\n</i></span>
            <span aria-hidden="true">Respire<i>\r\n</i></span>
          </Heading>
          <div className={styles.heroSide}>
            <p className={styles.lede}>
              A Redis client for .NET. Your calls go out as RESP, the replies come back as <code>string?</code>,{' '}
              <code>long</code>, and your own types.
            </p>
            <div className={styles.actions}>
              <Link className={styles.primaryButton} to="/docs/getting-started">Get started</Link>
              <InstallCommand />
            </div>
          </div>
        </div>
        <Encoder onEncode={(text) => field.current?.pulse(text)} />
      </div>
    </header>
  );
}

function Section({fragment, title, children, aside, className}) {
  return (
    <section className={clsx(styles.section, className)}>
      <div className="container">
        <Fragment lines={fragment} />
        <div className={styles.sectionGrid}>
          <div className={styles.sectionCopy}>
            <Heading as="h2">{title}</Heading>
            {children}
          </div>
          <div className={styles.sectionAside}>{aside}</div>
        </div>
      </div>
    </section>
  );
}

const catalogue = [
  {
    title: 'Data types',
    links: [
      ['Strings and keys', '/docs/commands/strings-and-keys'],
      ['Hashes, lists, sets, sorted sets, streams', '/docs/commands/collections'],
      ['JSON', '/docs/guides/json'],
      ['Search', '/docs/guides/search'],
      ['Time series', '/docs/guides/timeseries'],
      ['Probabilistic types', '/docs/guides/probabilistic'],
      ['Vector sets', '/docs/guides/vector-sets'],
    ],
  },
  {
    title: 'Coordination',
    links: [
      ['Distributed locks', '/docs/guides/distributed-locks'],
      ['Blocking work queues', '/docs/guides/blocking-queues'],
      ['Pub/sub', '/docs/guides/pub-sub'],
      ['Keyspace notifications', '/docs/guides/keyspace-notifications'],
      ['Batches and transactions', '/docs/guides/batches-and-transactions'],
      ['Fencing locks and leases', '/docs/guides/coordination'],
    ],
  },
  {
    title: 'Production',
    links: [
      ['Connections, cluster, and Sentinel', '/docs/fundamentals/connections'],
      ['Reconnect policy', '/docs/guides/reconnect-policy'],
      ['Failover groups', '/docs/guides/failover-groups'],
      ['AWS IAM credentials', '/docs/guides/aws-iam-credentials'],
      ['Azure Managed Redis', '/docs/guides/azure-managed-redis'],
      ['OpenTelemetry', '/docs/integrations/observability'],
    ],
  },
  {
    title: '.NET integration',
    links: [
      ['Dependency injection', '/docs/integrations/dependency-injection'],
      ['IDistributedCache and HybridCache', '/docs/integrations/caching'],
      ['Values and serialization', '/docs/fundamentals/values-and-serialization'],
      ['Value codecs', '/docs/guides/value-codecs'],
      ['Testing with containers', '/docs/guides/testing-containers'],
      ['In-memory test server', '/docs/guides/in-memory-testing'],
    ],
  },
];

const utf8 = new TextEncoder();
const lengthOf = (text) => utf8.encode(text).length;

// The docs index, written out as the RESP3 map reply it would be.
function Catalogue() {
  return (
    <section className={styles.catalogueSection}>
      <div className="container">
        <Fragment lines={[['%', String(catalogue.length)]]} />
        <Heading as="h2" className={styles.catalogueTitle}>What the docs cover</Heading>
        <div className={styles.catalogue}>
          {catalogue.map((group) => (
            <div key={group.title} className={styles.catalogueGroup}>
              <Heading as="h3">
                <span className={styles.bulk}>${lengthOf(group.title)}</span> {group.title}
              </Heading>
              <p className={styles.array}>*{group.links.length}</p>
              <ul>
                {group.links.map(([label, to]) => (
                  <li key={to}>
                    <span aria-hidden="true">${lengthOf(label)}</span>
                    <Link to={to}>{label}</Link>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

function Closing() {
  return (
    <section className={styles.closing}>
      <div className={clsx('container', styles.closingInner)}>
        <p className={styles.ok} aria-hidden="true"><b>+</b>OK<i>\r\n</i></p>
        <div className={styles.closingCopy}>
          <Heading as="h2">Respire is pre-release. Try it, and tell us what breaks.</Heading>
          <InstallCommand className={styles.installDark} />
          <div className={styles.actions}>
            <Link className={styles.primaryButton} to="/docs/getting-started">Get started</Link>
            <Link className={styles.secondaryButton} to="/docs/stackexchange-redis">Coming from StackExchange.Redis</Link>
            <Link className={styles.secondaryButton} href="https://github.com/thomhurst/Respire/issues">Open an issue</Link>
          </div>
        </div>
      </div>
    </section>
  );
}

export default function Home() {
  return (
    <Layout title="Redis client for .NET" description="Respire is a fast, typed, async-first Redis client for .NET with server-assisted client-side caching.">
      <main className={styles.home}>
        <Hero />
        <WireTicker />
        <Section
          fragment={[['$', '3'], ['', 'Ada']]}
          title="Replies come back as C# types"
          aside={<WireTrace />}>
          <p>
            Pick a command to see the frame Respire writes, the frame Redis sends back, and the value your code
            receives. Errors become exceptions. Pushes become cache evictions.
          </p>
          <Link to="/docs/fundamentals/values-and-serialization">Values and serialization</Link>
        </Section>
        <Section
          fragment={[['*', '2'], ['*', '2'], ['*', '2']]}
          title="Concurrent callers share one socket write"
          aside={<PipelineDemo />}>
          <p>
            Each <code>await</code> looks like its own round trip. Requests that arrive together are written to the
            socket together, and replies are matched back in order. There is no batching switch to remember.
          </p>
          <Link to="/docs/performance">How the wire layer works</Link>
        </Section>
        <Section
          className={styles.cacheSection}
          fragment={[['>', '2'], ['', 'invalidate']]}
          title="Hot reads never leave the process"
          aside={
            <>
              <CacheRace />
              <p className={styles.scaleNote}>
                Measured with BenchmarkDotNet on .NET 10: 186.5 μs for a StackExchange.Redis GET against a local
                server, 151.5 ns and 0 B allocated for a Respire cache hit.{' '}
                <Link href="https://github.com/thomhurst/Respire/actions/runs/31848970849">See the benchmark run</Link>.
              </p>
            </>
          }>
          <p>
            Turn on client-side caching and eligible reads stay in bounded process memory. Redis sends a{' '}
            <code>&gt;</code> push frame when a key you read changes, and Respire evicts the stale copy for you.
          </p>
          <CodeBlock language="csharp">{`var options = new RespireOptions
{
    Endpoints = { new("localhost") },
    ClientSideCache = new(),
};`}</CodeBlock>
          <Link to="/docs/fundamentals/client-side-caching">Read about client-side caching</Link>
        </Section>
        <Section
          fragment={[['$', '5'], ['', 'BLPOP']]}
          title="Blocking reads wait on their own connections"
          aside={<BlockingDemo />}>
          <p>
            <code>BLPOP</code> and blocking stream reads hold a connection until data arrives. Respire sends them over
            a dedicated pool, so a worker waiting 30 seconds for a job never delays the GETs behind it.
          </p>
          <CodeBlock language="csharp">{`string? job = await redis.Lists.LeftPopAsync(
    "jobs",
    waitFor: TimeSpan.FromSeconds(30));`}</CodeBlock>
          <Link to="/docs/guides/blocking-queues">Build a work queue</Link>
        </Section>
        <Catalogue />
        <Closing />
      </main>
    </Layout>
  );
}
