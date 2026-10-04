import {useEffect} from 'react';
import clsx from 'clsx';
import {useSeen, useTimeline} from './useDemo';
import styles from './demos.module.css';

const callers = [
  {name: 'A', code: 'GetStringAsync("user:1")', value: '"Ada"'},
  {name: 'B', code: 'IncrementAsync("visits")', value: '1025'},
  {name: 'C', code: 'Hashes.GetAllAsync("cart:9")', value: '2 fields'},
];

// Steps: 1-3 callers queue, 4 write leaves, 5 Redis replies,
// 6 reply arrives, 7-9 callers complete in order.
const timeline = [
  [0, 1], [180, 2], [360, 3], [800, 4], [1600, 5], [1900, 6], [2700, 7], [2850, 8], [3000, 9],
];

export default function PipelineDemo() {
  const [ref, seen] = useSeen();
  const [step, play] = useTimeline(timeline);

  useEffect(() => {
    if (seen) {
      play();
    }
  }, [seen]);

  const packetAtRedis = step >= 4 && step <= 5;
  const packetVisible = step >= 4 && step <= 6;
  const replying = step >= 5;

  return (
    <div ref={ref} className={styles.stage}>
      <div className={styles.pipeStage}>
        <ol className={styles.pipeCallers}>
          {callers.map((caller, index) => {
            const queued = step >= index + 1;
            const done = step >= index + 7;
            return (
              <li key={caller.name} className={clsx(queued && styles.queued, done && styles.done)}>
                <span className={styles.callerName}>{caller.name}</span>
                <code>await redis.{caller.code}</code>
                <span className={styles.callerState}>{done ? caller.value : queued ? 'queued' : 'waiting'}</span>
              </li>
            );
          })}
        </ol>
        <div className={styles.pipeWire}>
          <div className={styles.pipeBuffer}>
            <span>Write buffer</span>
            <div>
              {callers.map((caller, index) => (
                <code key={caller.name} className={clsx(styles.bufferSegment, step >= index + 1 && step < 4 && styles.bufferFilled)}>
                  {caller.name}
                </code>
              ))}
            </div>
          </div>
          <div className={styles.pipeTrack}>
            <div
              className={clsx(styles.packet, replying && styles.packetReply, packetVisible && styles.packetVisible)}
              style={{left: packetAtRedis ? 'calc(100% - 6.5rem)' : '0'}}>
              {replying ? '3 replies' : '1 write'}
            </div>
          </div>
          <div className={clsx(styles.pipeRedis, step === 5 && styles.pipeRedisBusy)}>Redis</div>
        </div>
      </div>
      <div className={styles.stageFooter}>
        <p>
          <strong>{step >= 9 ? '3 commands, 1 socket write' : 'Three concurrent awaits'}</strong>
        </p>
        <button type="button" className={styles.stageButton} onClick={play}>Replay</button>
      </div>
    </div>
  );
}
