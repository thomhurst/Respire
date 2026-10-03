import {useEffect, useRef, useState} from 'react';
import clsx from 'clsx';
import {prefersReducedMotion, useSeen} from './useDemo';
import styles from './demos.module.css';

const slowdown = 10_000;
const serverNs = 186_500;
const cacheNs = 151.5;

function formatNs(ns) {
  return ns >= 1000 ? `${(ns / 1000).toFixed(1)} μs` : `${ns.toFixed(1)} ns`;
}

// Replays one GET from each path, stretched 10,000 times so the server
// round trip is visible. The cache hit still finishes in under 2 ms.
export default function CacheRace() {
  const [ref, seen] = useSeen();
  const [elapsedNs, setElapsedNs] = useState(0);
  const frame = useRef(0);

  const play = () => {
    cancelAnimationFrame(frame.current);
    if (prefersReducedMotion()) {
      setElapsedNs(serverNs);
      return;
    }
    const start = performance.now();
    const tick = (now) => {
      const ns = Math.min(((now - start) * 1_000_000) / slowdown, serverNs);
      setElapsedNs(ns);
      if (ns < serverNs) {
        frame.current = requestAnimationFrame(tick);
      }
    };
    frame.current = requestAnimationFrame(tick);
  };

  useEffect(() => {
    if (seen) {
      play();
    }
    return () => cancelAnimationFrame(frame.current);
  }, [seen]);

  const progress = elapsedNs / serverNs;
  // The request travels out for the first half and back for the second.
  const position = progress < 0.5 ? progress * 2 : (1 - progress) * 2;
  const serverDone = elapsedNs >= serverNs;
  const cacheDone = elapsedNs >= cacheNs;

  return (
    <div ref={ref} className={styles.stage}>
      <div className={styles.race}>
        <div className={styles.raceLane}>
          <div className={styles.raceHead}>
            <span>Server round trip</span>
            <strong>{formatNs(Math.min(elapsedNs, serverNs))}</strong>
          </div>
          <div className={styles.raceTrack}>
            <span className={styles.raceNode}>App</span>
            <div className={styles.raceWire}>
              <i className={clsx(styles.raceDot, progress >= 0.5 && styles.raceDotBack)} style={{left: `${position * 100}%`}} />
            </div>
            <span className={clsx(styles.raceNode, progress > 0.45 && progress < 0.55 && styles.raceNodeHit)}>Redis</span>
          </div>
          <div className={styles.raceBar}><div style={{width: `${progress * 100}%`}} /></div>
        </div>
        <div className={styles.raceLane}>
          <div className={styles.raceHead}>
            <span>Local cache hit</span>
            <strong className={clsx(cacheDone && styles.raceWinner)}>{formatNs(Math.min(elapsedNs, cacheNs))}</strong>
          </div>
          <div className={styles.raceTrack}>
            <span className={styles.raceNode}>App</span>
            <span className={clsx(styles.raceNode, styles.raceCache, cacheDone && styles.raceNodeHit)}>Cache</span>
            <div className={clsx(styles.raceWire, styles.raceWireIdle)} />
            <span className={clsx(styles.raceNode, styles.raceIdle)}>Redis</span>
          </div>
          <div className={clsx(styles.raceBar, styles.raceBarHit)}><div style={{width: `${Math.min(elapsedNs / cacheNs, 1) * (cacheNs / serverNs) * 100}%`}} /></div>
        </div>
      </div>
      <div className={styles.stageFooter}>
        <p>{serverDone ? 'Same GET, about 1,200 times sooner from the cache.' : `Replaying ${slowdown.toLocaleString('en')} times slower than real time`}</p>
        <button type="button" className={styles.stageButton} onClick={play}>Replay</button>
      </div>
    </div>
  );
}
