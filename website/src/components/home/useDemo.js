import {useEffect, useRef, useState} from 'react';

export function prefersReducedMotion() {
  return typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

// Reports whether the element has scrolled into view at least once.
export function useSeen(threshold = 0.4) {
  const ref = useRef(null);
  const [seen, setSeen] = useState(false);

  useEffect(() => {
    const element = ref.current;
    if (!element || seen) {
      return undefined;
    }
    const observer = new IntersectionObserver(([entry]) => {
      if (entry.isIntersecting) {
        setSeen(true);
        observer.disconnect();
      }
    }, {threshold});
    observer.observe(element);
    return () => observer.disconnect();
  }, [seen, threshold]);

  return [ref, seen];
}

// Steps through a timeline of [delayMs, step] pairs. Calling play() restarts it.
export function useTimeline(timeline) {
  const [step, setStep] = useState(0);
  const timers = useRef([]);

  const clear = () => {
    timers.current.forEach(clearTimeout);
    timers.current = [];
  };

  const play = () => {
    clear();
    if (prefersReducedMotion()) {
      setStep(timeline[timeline.length - 1][1]);
      return;
    }
    setStep(0);
    timers.current = timeline.map(([delay, next]) => setTimeout(() => setStep(next), delay));
  };

  useEffect(() => clear, []);

  return [step, play];
}
