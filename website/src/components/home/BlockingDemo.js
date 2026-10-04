import {useEffect, useRef, useState} from 'react';
import clsx from 'clsx';
import {prefersReducedMotion, useSeen} from './useDemo';
import styles from './demos.module.css';

const traffic = ['GET', 'SET', 'INCR', 'HGET', 'GET', 'EXPIRE', 'ZADD', 'GET'];

export default function BlockingDemo() {
  const [ref, seen] = useSeen(0.2);
  const [waited, setWaited] = useState(0);
  const [job, setJob] = useState(null);
  const jobCount = useRef(41);
  const reset = useRef(0);

  // The wait counter is motion, so it stays still for reduced-motion readers.
  useEffect(() => {
    if (!seen || job || prefersReducedMotion()) {
      return undefined;
    }
    const timer = setInterval(() => setWaited((seconds) => (seconds >= 30 ? 0 : seconds + 1)), 1000);
    return () => clearInterval(timer);
  }, [seen, job]);

  useEffect(() => () => clearTimeout(reset.current), []);

  const push = () => {
    jobCount.current += 1;
    setJob(`resize:${jobCount.current}`);
    clearTimeout(reset.current);
    reset.current = setTimeout(() => {
      setJob(null);
      setWaited(0);
    }, 2600);
  };

  return (
    <div ref={ref} className={clsx(styles.stage, seen && styles.flowing)}>
      <div className={styles.lanes}>
        <div className={styles.lane}>
          <span className={styles.laneName}>Shared connection</span>
          <div className={styles.laneTrack} aria-hidden="true">
            {traffic.map((command, index) => (
              <code key={index} className={styles.laneCommand} style={{'--i': index}}>{command}</code>
            ))}
          </div>
        </div>
        <div className={styles.lane}>
          <span className={styles.laneName}>Blocking pool</span>
          <div className={clsx(styles.laneTrack, styles.laneBlocking)}>
            <code className={clsx(styles.blpop, job && styles.blpopDone)}>
              {job
                ? `BLPOP jobs returned "${job}"`
                : waited > 0 ? `BLPOP jobs waiting ${waited}s of 30s` : 'BLPOP jobs waiting, up to 30s'}
            </code>
            {/* Announce only the result, not every tick of the wait counter. */}
            <span className={styles.visuallyHidden} aria-live="polite">{job ? `BLPOP returned ${job}` : ''}</span>
          </div>
        </div>
      </div>
      <div className={styles.stageFooter}>
        <p>Regular commands keep moving while BLPOP waits.</p>
        <button type="button" className={styles.stageButton} onClick={push} disabled={Boolean(job)}>Push a job</button>
      </div>
    </div>
  );
}
