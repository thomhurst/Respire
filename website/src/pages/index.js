import clsx from 'clsx';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import CodeBlock from '@theme/CodeBlock';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';
import Tabs from '@theme/Tabs';
import TabItem from '@theme/TabItem';
import styles from './index.module.css';

const features = [
  {
    number: '01',
    title: 'Natural .NET APIs',
    text: 'Real return types, honest nullability, TimeSpan-based expiry, and async streams. Protocol details stay below the surface.',
    detail: 'string? · TimeSpan · IAsyncEnumerable',
    link: '/docs/fundamentals/values-and-serialization',
    linkText: 'Meet the API',
  },
  {
    number: '02',
    title: 'Hot reads stay local',
    text: 'Redis-assisted client caching serves eligible reads from bounded process memory and evicts them from RESP3 invalidation pushes.',
    detail: 'Read locally. Invalidate automatically.',
    link: '/docs/fundamentals/client-side-caching',
    linkText: 'Explore client caching',
  },
  {
    number: '03',
    title: 'Built for real services',
    text: 'Reconnects, resubscribing pub/sub, OpenTelemetry, dependency injection, typed serialization, and caching adapters included.',
    detail: 'Connect · observe · recover',
    link: '/docs/integrations/dependency-injection',
    linkText: 'Wire up your service',
  },
];

const ticks = ['Redis', 'Valkey', 'KeyDB', 'RESP3 cache', '.NET 8+', 'Async-only'];

const examples = [
  {
    value: 'connect',
    label: 'Connect & go',
    code: `using Respire;

await using var redis = await
    RespireClient.ConnectAsync("redis://localhost");

await redis.SetAsync(
    "greeting", "hello",
    expiry: TimeSpan.FromMinutes(5));

string? greeting = await
    redis.GetStringAsync("greeting");`,
  },
  {
    value: 'typed',
    label: 'Stay typed',
    code: `using Respire;

await using var redis = await
    RespireClient.ConnectAsync("redis://localhost");

await redis.SetAsync("user:ada", new User("Ada", 7));

User? user = await redis.GetAsync<User>("user:ada");

public sealed record User(string Name, int LoginCount);`,
  },
  {
    value: 'cache',
    label: 'Cache locally',
    code: `using Respire;

await using var redis = await
    RespireClient.ConnectAsync(new RespireOptions
    {
        Endpoints = { new("localhost") },
        ClientSideCache = new(),
    });

string? name = await
    redis.GetStringAsync("user:42:name");`,
  },
];

function CodeWindow() {
  return (
    <div className={styles.codeWindow} role="region" aria-label="Quick-start examples">
      <div className={styles.codeTopbar}>
        <div className={styles.windowDots} aria-hidden="true"><i /><i /><i /></div>
        <span>Program.cs</span>
        <span className={styles.codeLanguage}>C# / .NET</span>
      </div>
      <Tabs>
        {examples.map((example) => (
          <TabItem key={example.value} value={example.value} label={example.label}>
            <CodeBlock language="csharp">{example.code}</CodeBlock>
          </TabItem>
        ))}
      </Tabs>
      <div className={styles.pipeline}>
        <span>Async all the way</span><div className={styles.track} aria-hidden="true"><i /></div><span>RESP</span>
      </div>
    </div>
  );
}

function Hero() {
  const logoUrl = useBaseUrl('/img/logo.svg');

  return (
    <header className={styles.hero}>
      <div className={clsx('container', styles.heroGrid)}>
        <div className={styles.heroCopy}>
          <div className={styles.eyebrow}><span /> A little breathing room for .NET</div>
          <Heading as="h1">Let your<br />Redis code<br /><em>breathe.</em></Heading>
          <p>A modern RESP client that feels like C#.<br className={styles.desktopBreak} /> Fast, typed, async-first. From your first connection to your hottest Redis reads.</p>
          <div className={styles.actions}>
            <Link className={styles.primaryButton} to="/docs/getting-started">Start building <span>→</span></Link>
            <Link className={styles.secondaryButton} href="https://github.com/thomhurst/Respire">View source</Link>
          </div>
          <div className={styles.trustLine}><span>MIT licensed</span><span>Pure C#</span><span>No sync-over-async</span></div>
        </div>
        <div className={styles.heroVisual}>
          <div className={styles.breathArt} aria-hidden="true">
            <div className={styles.orbit} /><div className={styles.orbit} /><div className={styles.orbit} />
            <div className={styles.brandMark}><img src={logoUrl} alt="" width="64" height="64" /></div>
            <span className={styles.artLabel}>Less friction. More flow.</span>
          </div>
          <CodeWindow />
          <div className={styles.releaseNote}><span>Pre-release</span> Built in the open. Evolving with you.</div>
        </div>
      </div>
      <div className={styles.compatibility}>
        <div className="container"><span className={styles.compatibilityLabel}>One client. Familiar territory.</span><ul>{ticks.map((tick) => <li key={tick}>{tick}</li>)}</ul></div>
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
            <span className={styles.kicker}>02 / Closer to your data</span>
            <Heading as="h2">Hot Redis reads.<br />No Redis round trip.</Heading>
          </div>
          <p>Enable one option. Respire stores eligible responses in bounded process memory while Redis tells it exactly when tracked keys change. No cache-aside wrappers, notification channels, or application invalidation handlers.</p>
        </div>
        <div className={styles.cacheGrid}>
          <div className={styles.cacheVisual}>
            <div className={styles.cacheVisualHeading}><span className={styles.kicker}>The shorter path</span><strong>Keep hot reads<br />close to home.</strong></div>
            <div className={styles.localRead}><span>Your application</span><span aria-hidden="true">⇄</span><strong>Local cache</strong></div>
            <p className={styles.localReadNote}>Cache hit? No network round trip.</p>
            <div className={styles.cacheStep}><span>01 · miss</span><strong>Read Redis</strong><small>Cache response locally</small></div>
            <div className={styles.cacheArrow}>↓</div>
            <div className={styles.cacheStep}><span>02 · change</span><strong>Redis pushes invalidation</strong><small>Respire evicts stale entry</small></div>
            <div className={styles.cacheArrow}>↓</div>
            <div className={styles.cacheStep}><span>03 · next read</span><strong>Refresh lazily</strong><small>Then serve hot reads locally</small></div>
          </div>
          <div className={styles.cacheCopy}>
            <span className={styles.comparisonLabel}>One option. Same familiar API.</span>
            <CodeBlock language="csharp">{`await using var redis = await
    RespireClient.ConnectAsync(new RespireOptions
    {
        Endpoints = { new("localhost") },
        ClientSideCache = new(),
    });

// Same API. First call misses; hot reads stay local.
string? name = await redis.GetStringAsync("user:42:name");`}</CodeBlock>
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
          <span className={styles.kicker}>01 / Built to feel natural</span>
          <Heading as="h2">Serious wire layer.<br />Calm application code.</Heading>
          <p>Respire handles connection choreography so your code can speak in the language of your domain.</p>
        </div>
        <div className={styles.featureGrid}>
          {features.map((feature) => (
            <article className={styles.featureCard} key={feature.number}>
              <span>{feature.number}</span>
              <div className={styles.featureDetail}>{feature.detail}</div>
              <Heading as="h3">{feature.title}</Heading>
              <p>{feature.text}</p>
              <Link to={feature.link}>{feature.linkText} <span aria-hidden="true">↗</span></Link>
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
          <span className={styles.kicker}>03 / Room to keep moving</span>
          <Heading as="h2">Blocking commands.<br />Nothing else blocked.</Heading>
          <p>Respire routes blocking list and stream operations through dedicated pooled connections. Normal traffic keeps moving.</p>
          <CodeBlock language="csharp">{`string? job = await redis.Lists.LeftPopAsync(\n    "jobs",\n    waitFor: TimeSpan.FromSeconds(30));`}</CodeBlock>
          <Link to="/docs/guides/blocking-queues">Build a work queue <span>→</span></Link>
        </div>
      </div>
    </section>
  );
}

function FinalCta() {
  return (
    <section className={styles.finalCta}>
      <div className={clsx('container', styles.finalGrid)}>
        <div>
          <span className={styles.kicker}>Ready when you are</span>
          <Heading as="h2">Take a breath.<br />Start building.</Heading>
          <p>Explore core concepts, production integrations, and every escape hatch.</p>
          <Link className={styles.primaryButton} to="/docs/getting-started">Read the quickstart <span>→</span></Link>
        </div>
        <div className={styles.docsLinks}>
          <Link to="/docs/commands/strings-and-keys"><span><strong>Find your command</strong><small>Strings, collections, streams, and more</small></span><span aria-hidden="true">↗</span></Link>
          <Link to="/docs/integrations/dependency-injection"><span><strong>Make yourself at home</strong><small>Dependency injection and service integration</small></span><span aria-hidden="true">↗</span></Link>
          <Link to="/docs/performance"><span><strong>Look under the hood</strong><small>How Respire keeps commands moving</small></span><span aria-hidden="true">↗</span></Link>
        </div>
      </div>
    </section>
  );
}

export default function Home() {
  return (
    <Layout title="Modern Redis client for .NET" description="Respire is a fast, modern RESP client for .NET with built-in Redis server-assisted client-side caching.">
      <main className={styles.home}><Hero /><FeatureSection /><CacheSection /><BlockingSection /><FinalCta /></main>
    </Layout>
  );
}
