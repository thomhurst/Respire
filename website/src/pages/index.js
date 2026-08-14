import clsx from 'clsx';
import Link from '@docusaurus/Link';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';
import styles from './index.module.css';

const features = [
  {
    number: '01',
    title: 'Natural .NET APIs',
    text: 'Real return types, honest nullability, TimeSpan-based expiry, and async streams. Protocol details stay below the surface.',
  },
  {
    number: '02',
    title: 'Hot reads stay local',
    text: 'Redis-assisted client caching serves eligible reads from bounded process memory and evicts them from RESP3 invalidation pushes.',
  },
  {
    number: '03',
    title: 'Built for real services',
    text: 'Reconnects, resubscribing pub/sub, OpenTelemetry, dependency injection, typed serialization, and caching adapters included.',
  },
];

const ticks = ['Redis', 'Valkey', 'KeyDB', 'RESP3 cache', '.NET 8+', 'Async-only'];

function CodeWindow() {
  return (
    <div className={styles.codeWindow} aria-label="Respire quick-start example">
      <div className={styles.codeTopbar}>
        <div className={styles.windowDots}><i /><i /><i /></div>
        <span>Program.cs</span>
        <span className={styles.live}><i /> connected</span>
      </div>
      <pre className={styles.code}><code><span className={styles.keyword}>await using var</span> redis = <span className={styles.keyword}>await</span>{'\n'}    RespireClient.ConnectAsync(<span className={styles.string}>&quot;redis://localhost&quot;</span>);{'\n\n'}<span className={styles.keyword}>await</span> redis.SetAsync({'\n'}    <span className={styles.string}>&quot;greeting&quot;</span>,{'\n'}    <span className={styles.string}>&quot;hello&quot;</span>,{'\n'}    expiry: TimeSpan.FromMinutes(<span className={styles.number}>5</span>));{'\n\n'}<span className={styles.type}>string</span>? greeting = <span className={styles.keyword}>await</span>{'\n'}    redis.GetStringAsync(<span className={styles.string}>&quot;greeting&quot;</span>);</code></pre>
      <div className={styles.pipeline}>
        <span>caller</span><div className={styles.track}><i /><i /><i /></div><span>RESP</span>
      </div>
    </div>
  );
}

function Hero() {
  return (
    <header className={styles.hero}>
      <div className={styles.glow} />
      <div className={clsx('container', styles.heroGrid)}>
        <div className={styles.heroCopy}>
          <div className={styles.eyebrow}><span>Pre-release</span> A modern RESP client for .NET</div>
          <Heading as="h1">Let your Redis code <em>breathe.</em></Heading>
          <p>Fast, typed, async-first access to Redis, Valkey, KeyDB, and other RESP-compatible servers—with built-in server-assisted caching for hot Redis reads.</p>
          <div className={styles.actions}>
            <Link className={styles.primaryButton} to="/docs/getting-started">Start building <span>→</span></Link>
            <Link className={styles.secondaryButton} href="https://github.com/thomhurst/Respire">View source</Link>
          </div>
          <div className={styles.trustLine}><span>MIT licensed</span><span>Pure C#</span><span>No sync-over-async</span></div>
        </div>
        <CodeWindow />
      </div>
      <div className={styles.ticker} aria-hidden="true">
        <div>{[...ticks, ...ticks].map((tick, index) => <span key={`${tick}-${index}`}><i />{tick}</span>)}</div>
      </div>
    </header>
  );
}

function CacheSection() {
  return (
    <section className={styles.cacheSection}>
      <div className="container">
        <div className={styles.cacheHeading}>
          <div>
            <span className={styles.kicker}>A reason to switch</span>
            <Heading as="h2">Hot Redis reads.<br />No Redis round trip.</Heading>
          </div>
          <p>Enable one option. Respire stores eligible responses in bounded process memory while Redis tells it exactly when tracked keys change. No cache-aside wrappers, notification channels, or application invalidation handlers.</p>
        </div>
        <div className={styles.cacheGrid}>
          <div className={styles.cacheVisual} aria-label="Client-side cache invalidation flow">
            <div className={styles.cacheStep}><span>01 · miss</span><strong>Read Redis</strong><small>Cache response locally</small></div>
            <div className={styles.cacheArrow}>↓</div>
            <div className={styles.cacheStep}><span>02 · change</span><strong>Redis pushes invalidation</strong><small>Respire evicts stale entry</small></div>
            <div className={styles.cacheArrow}>↓</div>
            <div className={styles.cacheStep}><span>03 · next read</span><strong>Refresh lazily</strong><small>Then serve hot reads locally</small></div>
          </div>
          <div className={styles.cacheCopy}>
            <span className={styles.comparisonLabel}>StackExchange.Redis has no equivalent built-in cache</span>
            <pre><code>{`await using var redis = await
    RespireClient.ConnectAsync(new RespireOptions
    {
        Endpoints = { new("localhost") },
        ClientSideCache = new(),
    });

// Same API. First call misses; hot reads stay local.
string? name = await redis.GetStringAsync("user:42:name");`}</code></pre>
            <div className={styles.cacheMetrics}>
              <div><strong>151.5 ns</strong><span>cached GET</span></div>
              <div><strong>186.5 μs</strong><span>StackExchange server GET</span></div>
              <div><strong>0 B</strong><span>missing GET cache hit</span></div>
            </div>
            <p className={styles.benchmarkNote}>net10 BenchmarkDotNet short run. Cache hit versus server read; uncached clients measured statistically the same.</p>
            <div className={styles.cacheLinks}>
              <Link to="/docs/fundamentals/client-side-caching">Explore client caching <span>→</span></Link>
              <Link href="https://github.com/thomhurst/Respire/actions/runs/31848970849">See benchmark run</Link>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}

function FeatureSection() {
  return (
    <section className={styles.featureSection}>
      <div className="container">
        <div className={styles.sectionIntro}>
          <span className={styles.kicker}>Designed from first principles</span>
          <Heading as="h2">Serious wire layer.<br />Calm application code.</Heading>
          <p>Respire handles connection choreography so your code can speak in the language of your domain.</p>
        </div>
        <div className={styles.featureGrid}>
          {features.map((feature) => (
            <article className={styles.featureCard} key={feature.number}>
              <span>{feature.number}</span>
              <Heading as="h3">{feature.title}</Heading>
              <p>{feature.text}</p>
            </article>
          ))}
        </div>
      </div>
    </section>
  );
}

function BlockingSection() {
  return (
    <section className={styles.blockingSection}>
      <div className={clsx('container', styles.blockingGrid)}>
        <div className={styles.queueVisual} aria-hidden="true">
          <div className={styles.queueLabel}><span>multiplexed</span><strong>regular traffic</strong></div>
          <div className={styles.queueLines}><i /><i /><i /><i /></div>
          <div className={styles.queueLabel}><span>dedicated pool</span><strong>BLPOP · XREAD</strong></div>
        </div>
        <div className={styles.blockingCopy}>
          <span className={styles.kicker}>Headline capability</span>
          <Heading as="h2">Blocking commands.<br />Nothing else blocked.</Heading>
          <p>Respire routes blocking list and stream operations through dedicated pooled connections. Normal traffic keeps moving.</p>
          <pre><code>{`string? job = await redis.Lists.LeftPopAsync(\n    "jobs",\n    waitFor: TimeSpan.FromSeconds(30));`}</code></pre>
          <Link to="/docs/guides/blocking-queues">Build a work queue <span>→</span></Link>
        </div>
      </div>
    </section>
  );
}

function FinalCta() {
  return (
    <section className={styles.finalCta}>
      <div className="container">
        <span className={styles.kicker}>Ready when you are</span>
        <Heading as="h2">One connection string.<br />Your first command.</Heading>
        <p>Explore core concepts, production integrations, and every escape hatch.</p>
        <Link className={styles.primaryButton} to="/docs/getting-started">Read the quickstart <span>→</span></Link>
      </div>
    </section>
  );
}

export default function Home() {
  return (
    <Layout title="Modern Redis client for .NET" description="Respire is a fast, modern RESP client for .NET with built-in Redis server-assisted client-side caching.">
      <main><Hero /><FeatureSection /><CacheSection /><BlockingSection /><FinalCta /></main>
    </Layout>
  );
}
