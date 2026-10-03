import {useMemo, useState} from 'react';
import styles from './demos.module.css';

const encoder = new TextEncoder();
const byteLength = (text) => encoder.encode(text).length;

// Splits a redis-cli style line into arguments, honouring quotes.
export function tokenize(line) {
  const tokens = [];
  const pattern = /"((?:[^"\\]|\\.)*)"|'([^']*)'|(\S+)/g;
  let match;
  while ((match = pattern.exec(line)) !== null) {
    tokens.push(match[1] ?? match[2] ?? match[3]);
  }
  return tokens;
}

export function encode(tokens) {
  const lines = [['*', String(tokens.length)]];
  for (const token of tokens) {
    lines.push(['$', String(byteLength(token))], ['', token]);
  }
  return lines;
}

const quote = (value) => JSON.stringify(value);
const seconds = (value) => `TimeSpan.FromSeconds(${Number.isFinite(Number(value)) ? value : 0})`;

// Respire calls for common commands. Anything else falls back to ExecuteAsync.
const calls = {
  PING: () => 'TimeSpan roundTrip = await redis.PingAsync();',
  GET: ([key]) => `string? value = await redis.GetStringAsync(${quote(key)});`,
  SET: ([key, value, option, amount]) =>
    option?.toUpperCase() === 'EX'
      ? `await redis.SetAsync(${quote(key)}, ${quote(value)}, expiry: ${seconds(amount)});`
      : `await redis.SetAsync(${quote(key)}, ${quote(value)});`,
  INCR: ([key]) => `long value = await redis.IncrementAsync(${quote(key)});`,
  DECR: ([key]) => `long value = await redis.DecrementAsync(${quote(key)});`,
  DEL: (keys) => `long removed = await redis.DeleteAsync(${keys.map(quote).join(', ')});`,
  EXPIRE: ([key, amount]) => `await redis.ExpireAsync(${quote(key)}, ${seconds(amount)});`,
  HSET: ([key, field, value]) => `await redis.Hashes.SetAsync(${quote(key)}, ${quote(field)}, ${quote(value)});`,
  HGETALL: ([key]) => `Dictionary<string, string> hash = await redis.Hashes.GetAllAsync(${quote(key)});`,
  LPUSH: ([key, value]) => `await redis.Lists.LeftPushAsync(${quote(key)}, ${quote(value)});`,
  RPUSH: ([key, value]) => `await redis.Lists.RightPushAsync(${quote(key)}, ${quote(value)});`,
  BLPOP: ([key, timeout]) => `string? item = await redis.Lists.LeftPopAsync(\n    ${quote(key)},\n    waitFor: ${seconds(timeout)});`,
  ZADD: ([key, score, member]) => `await redis.SortedSets.AddAsync(${quote(key)}, ${quote(member)}, ${Number(score) || 0});`,
  PUBLISH: ([channel, message]) => `long receivers = await redis.PublishAsync(${quote(channel)}, ${quote(message)});`,
};

function toCSharp(tokens) {
  if (tokens.length === 0) {
    return '// Type a command above';
  }
  const [name, ...args] = tokens;
  const call = calls[name.toUpperCase()];
  if (call && args.every((arg) => arg !== undefined)) {
    return call(args);
  }
  return `using RespireResult result = await redis.ExecuteAsync(\n    ${[name.toUpperCase(), ...args].map(quote).join(', ')});`;
}

const kindOf = {'*': 'array', $: 'bulk', '': 'payload'};
const examples = ['SET greeting hello EX 300', 'HSET user:42 name Ada', 'BLPOP jobs 30', 'PUBLISH orders "order #7"', 'ZADD leaderboard 98.5 ada'];

export default function Encoder({onEncode}) {
  const [line, setLine] = useState(examples[0]);
  const tokens = useMemo(() => tokenize(line), [line]);
  const frame = useMemo(() => encode(tokens), [tokens]);
  const wire = frame.map(([prefix, payload]) => `${prefix}${payload}\\r\\n`).join('');
  const bytes = frame.reduce((total, [prefix, payload]) => total + byteLength(prefix + payload) + 2, 0);

  const update = (next) => {
    setLine(next);
    const nextTokens = tokenize(next);
    if (nextTokens.length > 0) {
      onEncode?.(encode(nextTokens).map(([prefix, payload]) => `${prefix}${payload}\\r\\n`).join(''));
    }
  };

  return (
    <div className={styles.encoder}>
      <label className={styles.encoderInput}>
        <span>Type a Redis command</span>
        <input
          value={line}
          onChange={(event) => update(event.target.value)}
          spellCheck={false}
          autoCapitalize="off"
          autoComplete="off"
        />
      </label>
      <div className={styles.encoderExamples}>
        {examples.map((example) => (
          <button key={example} type="button" onClick={() => update(example)} aria-pressed={example === line}>
            {example.split(' ')[0]}
          </button>
        ))}
      </div>
      <div className={styles.encoderOutput}>
        <div>
          <h3>On the wire <span>{bytes} bytes</span></h3>
          <code className={styles.encoderWire} aria-label={wire}>
            {frame.map(([prefix, payload], index) => (
              <span key={index} className={styles[kindOf[prefix]]}>
                <b>{prefix}</b>{payload}<i>\r\n</i>
              </span>
            ))}
          </code>
        </div>
        <div>
          <h3>In Respire</h3>
          <code className={styles.encoderCode}>{toCSharp(tokens)}</code>
        </div>
      </div>
    </div>
  );
}
