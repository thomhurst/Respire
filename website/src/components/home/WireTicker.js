import clsx from 'clsx';
import styles from './demos.module.css';

// A plausible RESP3 session with client-side caching turned on.
// `out` frames are sent by Respire; `in` frames come from Redis.
const session = [
  ['out', [['*', '2'], ['$', '5'], ['', 'HELLO'], ['$', '1'], ['', '3']]],
  ['in', [['%', '7'], ['$', '6'], ['', 'server'], ['$', '5'], ['', 'redis']]],
  ['out', [['*', '3'], ['$', '6'], ['', 'CLIENT'], ['$', '8'], ['', 'TRACKING'], ['$', '2'], ['', 'ON']]],
  ['in', [['+', 'OK']]],
  ['out', [['*', '2'], ['$', '3'], ['', 'GET'], ['$', '12'], ['', 'user:42:name']]],
  ['in', [['$', '3'], ['', 'Ada']]],
  ['out', [['*', '2'], ['$', '4'], ['', 'INCR'], ['$', '6'], ['', 'visits']]],
  ['in', [[':', '1024']]],
  ['out', [['*', '3'], ['$', '5'], ['', 'BLPOP'], ['$', '4'], ['', 'jobs'], ['$', '2'], ['', '30']]],
  ['in', [['>', '2'], ['$', '10'], ['', 'invalidate'], ['*', '1'], ['$', '12'], ['', 'user:42:name']]],
  ['out', [['*', '2'], ['$', '7'], ['', 'HGETALL'], ['$', '7'], ['', 'cart:91']]],
  ['in', [['%', '2'], ['$', '3'], ['', 'sku'], ['$', '4'], ['', 'A-17'], ['$', '3'], ['', 'qty'], ['$', '1'], ['', '2']]],
  ['out', [['*', '2'], ['$', '4'], ['', 'INCR'], ['$', '7'], ['', 'user:42']]],
  ['in', [['-', 'WRONGTYPE Operation against a key holding the wrong kind of value']]],
  ['in', [['*', '2'], ['$', '4'], ['', 'jobs'], ['$', '9'], ['', 'resize:42']]],
];

const kindOf = {'+': 'simple', '-': 'error', ':': 'integer', $: 'bulk', '*': 'array', '%': 'array', '>': 'push', '': 'payload'};

function Session() {
  return session.map(([direction, lines], frameIndex) => (
    <span key={frameIndex} className={clsx(styles.tickerFrame, direction === 'in' && styles.tickerIn)}>
      <span className={styles.tickerDirection}>{direction === 'out' ? 'sent' : 'received'}</span>
      {lines.map(([prefix, payload], lineIndex) => (
        <span key={lineIndex} className={styles[kindOf[prefix]]}>
          <b>{prefix}</b>{payload}<i>\r\n</i>
        </span>
      ))}
    </span>
  ));
}

export default function WireTicker() {
  return (
    <div className={styles.ticker} aria-hidden="true">
      <div className={styles.tickerTrack}>
        <div className={styles.tickerRun}><Session /></div>
        <div className={styles.tickerRun}><Session /></div>
      </div>
    </div>
  );
}
