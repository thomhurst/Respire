import {useMemo, useState} from 'react';
import styles from './demos.module.css';

const encoder = new TextEncoder();
const byteLength = (text) => encoder.encode(text).length;

const escapes = {n: '\n', r: '\r', t: '\t', b: '\b', a: '\x07', '"': '"', '\\': '\\'};

// Decodes the escapes redis-cli accepts inside double quotes: \n, \r, \t, \b,
// \a, \", \\ and \xHH. Anything else keeps the escaped character.
function unescape(text) {
  return text.replace(/\\(x[0-9a-fA-F]{2}|.)/g, (_, code) =>
    code.length === 3 ? String.fromCharCode(parseInt(code.slice(1), 16)) : escapes[code] ?? code);
}

// Splits a redis-cli style line into arguments, honouring quotes.
export function tokenize(line) {
  const tokens = [];
  const pattern = /"((?:[^"\\]|\\.)*)"|'([^']*)'|(\S+)/g;
  let match;
  while ((match = pattern.exec(line)) !== null) {
    tokens.push(match[1] !== undefined ? unescape(match[1]) : match[2] ?? match[3]);
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

// JSON string escaping (\n, \", \\, \uXXXX) is also valid C# string syntax.
const quote = (value) => JSON.stringify(value);
const isInteger = (value) => /^\d+$/.test(value);
const isNumber = (value) => value !== '' && Number.isFinite(Number(value));
const seconds = (value) => `TimeSpan.FromSeconds(${value})`;

// Respire calls for common commands. Each entry returns null unless it models
// every argument, so options such as SET ... NX fall back to ExecuteAsync
// instead of showing a call that means something different.
const calls = {
  PING: (args) => (args.length === 0 ? 'TimeSpan roundTrip = await redis.PingAsync();' : null),
  GET: (args) => (args.length === 1 ? `string? value = await redis.GetStringAsync(${quote(args[0])});` : null),
  SET: ([key, value, option, amount, ...rest]) => {
    if (value === undefined || rest.length > 0) {
      return null;
    }
    if (option === undefined) {
      return `await redis.SetAsync(${quote(key)}, ${quote(value)});`;
    }
    return option.toUpperCase() === 'EX' && isInteger(amount)
      ? `await redis.SetAsync(${quote(key)}, ${quote(value)}, expiry: ${seconds(amount)});`
      : null;
  },
  INCR: (args) => (args.length === 1 ? `long value = await redis.IncrementAsync(${quote(args[0])});` : null),
  DECR: (args) => (args.length === 1 ? `long value = await redis.DecrementAsync(${quote(args[0])});` : null),
  DEL: (args) => (args.length > 0 ? `long removed = await redis.DeleteAsync(${args.map(quote).join(', ')});` : null),
  EXPIRE: (args) =>
    args.length === 2 && isInteger(args[1]) ? `await redis.ExpireAsync(${quote(args[0])}, ${seconds(args[1])});` : null,
  HSET: (args) =>
    args.length === 3 ? `await redis.Hashes.SetAsync(${args.map(quote).join(', ')});` : null,
  HGETALL: (args) =>
    args.length === 1 ? `Dictionary<string, string> hash = await redis.Hashes.GetAllAsync(${quote(args[0])});` : null,
  LPUSH: (args) => (args.length === 2 ? `await redis.Lists.LeftPushAsync(${args.map(quote).join(', ')});` : null),
  RPUSH: (args) => (args.length === 2 ? `await redis.Lists.RightPushAsync(${args.map(quote).join(', ')});` : null),
  BLPOP: (args) =>
    args.length === 2 && isInteger(args[1])
      ? `string? item = await redis.Lists.LeftPopAsync(\n    ${quote(args[0])},\n    waitFor: ${seconds(args[1])});`
      : null,
  ZADD: (args) =>
    args.length === 3 && isNumber(args[1])
      ? `await redis.SortedSets.AddAsync(${quote(args[0])}, ${quote(args[2])}, ${Number(args[1])});`
      : null,
  PUBLISH: (args) =>
    args.length === 2 ? `long receivers = await redis.PublishAsync(${args.map(quote).join(', ')});` : null,
};

function toCSharp(tokens) {
  if (tokens.length === 0) {
    return '// Type a command above';
  }
  const [name, ...args] = tokens;
  const typed = calls[name.toUpperCase()]?.(args);
  if (typed) {
    return typed;
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
