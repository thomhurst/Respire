import {useEffect, useMemo, useState} from 'react';
import styles from './demos.module.css';

const utf8 = new TextEncoder();
const strictUtf8 = new TextDecoder('utf-8', {fatal: true});

const escapes = {n: 0x0a, r: 0x0d, t: 0x09, b: 0x08, a: 0x07, '"': 0x22, '\\': 0x5c};

// Decodes a double-quoted redis-cli argument to the bytes redis-cli sends.
// \n, \r, \t, \b, \a, \", \\ and \xHH are escapes; \xHH is one raw byte, so
// "\xff" is a single 0xFF byte rather than the UTF-8 encoding of "ÿ".
function decodeEscapes(text) {
  const bytes = [];
  const pattern = /\\(x[0-9a-fA-F]{2}|[\s\S])|([^\\]+)/g;
  let match;
  while ((match = pattern.exec(text)) !== null) {
    const [, escape, plain] = match;
    if (plain !== undefined) {
      bytes.push(...utf8.encode(plain));
    } else if (escape.length === 3) {
      bytes.push(parseInt(escape.slice(1), 16));
    } else if (escapes[escape] !== undefined) {
      bytes.push(escapes[escape]);
    } else {
      bytes.push(...utf8.encode(escape));
    }
  }
  return Uint8Array.from(bytes);
}

function toText(bytes) {
  try {
    return strictUtf8.decode(bytes);
  } catch {
    return null;
  }
}

// A token is the bytes sent for one argument, plus its text when those bytes
// are valid UTF-8 (null otherwise).
function token(bytes) {
  return {bytes, text: toText(bytes)};
}

// Splits a redis-cli style line into arguments, honouring quotes.
export function tokenize(line) {
  const tokens = [];
  const pattern = /"((?:[^"\\]|\\[\s\S])*)"|'([^']*)'|(\S+)/g;
  let match;
  while ((match = pattern.exec(line)) !== null) {
    tokens.push(match[1] !== undefined ? token(decodeEscapes(match[1])) : token(utf8.encode(match[2] ?? match[3])));
  }
  return tokens;
}

// How a payload is shown: readable text as is, anything else in the same
// escaped form redis-cli prints, for example "\xff" or "a\nb".
function display({bytes, text}) {
  if (text !== null && !/[\x00-\x1f\x7f]/.test(text)) {
    return text;
  }
  const named = {0x0a: '\\n', 0x0d: '\\r', 0x09: '\\t', 0x22: '\\"', 0x5c: '\\\\'};
  const body = [...bytes].map((byte) => named[byte]
    ?? (byte >= 0x20 && byte < 0x7f ? String.fromCharCode(byte) : `\\x${byte.toString(16).padStart(2, '0')}`));
  return `"${body.join('')}"`;
}

// Each frame line is [prefix, shown text, bytes on the wire before CRLF].
export function encode(tokens) {
  const count = String(tokens.length);
  const lines = [['*', count, count.length + 1]];
  for (const item of tokens) {
    const length = String(item.bytes.length);
    lines.push(['$', length, length.length + 1], ['', display(item), item.bytes.length]);
  }
  return lines;
}

const toWire = (frame) => frame.map(([prefix, payload]) => `${prefix}${payload}\\r\\n`).join('');
const byteCount = (frame) => frame.reduce((total, line) => total + line[2] + 2, 0);

// JSON string escaping (\n, \", \\, \uXXXX) is also valid C# string syntax.
const quote = (value) => JSON.stringify(value);
const byteArray = (bytes) => `new byte[] { ${[...bytes].map((byte) => `0x${byte.toString(16).padStart(2, '0').toUpperCase()}`).join(', ')} }`;
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
  // Typed calls take strings, so they only apply when every argument is UTF-8.
  if (tokens.every((item) => item.text !== null)) {
    const [name, ...args] = tokens.map((item) => item.text);
    const typed = calls[name.toUpperCase()]?.(args);
    if (typed) {
      return typed;
    }
  }
  const [name, ...args] = tokens;
  const command = name.text === null ? byteArray(name.bytes) : quote(name.text.toUpperCase());
  const values = args.map((item) => (item.text === null ? byteArray(item.bytes) : quote(item.text)));
  return `using RespireResult result = await redis.ExecuteAsync(\n    ${[command, ...values].join(', ')});`;
}

const kindOf = {'*': 'array', $: 'bulk', '': 'payload'};
const examples = ['SET greeting hello EX 300', 'HSET user:42 name Ada', 'BLPOP jobs 30', 'PUBLISH orders "order #7"', 'ZADD leaderboard 98.5 ada'];

export default function Encoder({onEncode}) {
  const [line, setLine] = useState(examples[0]);
  const tokens = useMemo(() => tokenize(line), [line]);
  const frame = useMemo(() => encode(tokens), [tokens]);
  const wire = toWire(frame);

  useEffect(() => {
    if (tokens.length > 0) {
      onEncode?.(wire);
    }
  }, [wire]);

  return (
    <div className={styles.encoder}>
      <label className={styles.encoderInput}>
        <span>Type a Redis command</span>
        <input
          value={line}
          onChange={(event) => setLine(event.target.value)}
          spellCheck={false}
          autoCapitalize="off"
          autoComplete="off"
        />
      </label>
      <div className={styles.encoderExamples}>
        {examples.map((example) => (
          <button key={example} type="button" onClick={() => setLine(example)} aria-pressed={example === line}>
            {example.split(' ')[0]}
          </button>
        ))}
      </div>
      <div className={styles.encoderOutput}>
        <div>
          <h3>On the wire <span>{byteCount(frame)} bytes</span></h3>
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
