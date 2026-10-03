import {forwardRef, useEffect, useImperativeHandle, useRef} from 'react';
import {prefersReducedMotion} from './useDemo';

// A faint field of RESP bytes behind the hero that breathes: a brighter band
// expands from the centre on the exhale and contracts on the inhale. pulse(text) writes a
// new frame into the middle rows and sends a faster ripple through the field.

const seed = [
  '*2', '$5', 'HELLO', '$1', '3', '%7', '$6', 'server', '$5', 'redis', '*3', '$6', 'CLIENT', '$8', 'TRACKING', '$2', 'ON',
  '+OK', '*2', '$3', 'GET', '$12', 'user:42:name', '$3', 'Ada', '*2', '$4', 'INCR', '$6', 'visits', ':1024',
  '*3', '$5', 'BLPOP', '$4', 'jobs', '$2', '30', '>2', '$10', 'invalidate', '*1', '$12', 'user:42:name',
  '*2', '$7', 'HGETALL', '$7', 'cart:91', '%2', '$3', 'sku', '$4', 'A-17', '$3', 'qty', '$1', '2',
  '-WRONGTYPE', '*4', '$4', 'HSET', '$7', 'user:42', '$4', 'name', '$3', 'Ada', ':1', '_',
].map((token) => `${token}\\r\\n`).join('');

// Type prefixes take their colour from the theme tokens in custom.css.
const prefixTokens = {
  '+': '--resp-simple',
  '-': '--resp-error',
  ':': '--resp-integer',
  $: '--resp-bulk',
  '*': '--resp-array',
  '%': '--resp-array',
  '>': '--resp-push',
};

function kinds(text) {
  // A character is a type prefix when it starts the text or follows "\r\n".
  return [...text].map((char, index) => {
    const startsLine = index === 0 || text.slice(index - 4, index) === '\\r\\n';
    return startsLine && prefixTokens[char] ? char : '';
  });
}

function toRgb(value) {
  const hex = value.trim().replace('#', '');
  if (hex.length === 6) {
    return [0, 2, 4].map((offset) => parseInt(hex.slice(offset, offset + 2), 16));
  }
  return value.split(',').map((part) => Number(part.trim()));
}

function readTheme(element) {
  const style = getComputedStyle(element);
  const theme = {'': toRgb(style.getPropertyValue('--resp-field') || '26, 28, 36')};
  for (const [prefix, token] of Object.entries(prefixTokens)) {
    theme[prefix] = toRgb(style.getPropertyValue(token));
  }
  return theme;
}

const ByteField = forwardRef(function ByteField({className}, ref) {
  const canvasRef = useRef(null);
  const state = useRef({text: seed, kinds: kinds(seed), pulses: [], pointer: null, theme: null});

  useImperativeHandle(ref, () => ({
    pulse(text) {
      const current = state.current;
      // Splice the new frame into the stream so it appears near the centre.
      const middle = Math.floor(current.text.length / 2);
      const next = current.text.slice(0, middle) + text + current.text.slice(middle + text.length);
      current.text = next;
      current.kinds = kinds(next);
      current.pulses.push(performance.now());
      current.draw?.();
    },
  }), []);

  useEffect(() => {
    const canvas = canvasRef.current;
    const context = canvas.getContext('2d');
    const current = state.current;
    const reduced = prefersReducedMotion();
    let width = 0;
    let height = 0;
    let cellWidth = 0;
    let cellHeight = 0;
    let frame = 0;
    let visible = true;
    let last = 0;

    const resize = () => {
      const ratio = Math.min(window.devicePixelRatio || 1, 2);
      const rect = canvas.getBoundingClientRect();
      width = rect.width;
      height = rect.height;
      canvas.width = Math.round(width * ratio);
      canvas.height = Math.round(height * ratio);
      context.setTransform(ratio, 0, 0, ratio, 0, 0);
      const size = width < 600 ? 11 : 13;
      context.font = `500 ${size}px "IBM Plex Mono", ui-monospace, monospace`;
      context.textBaseline = 'top';
      cellWidth = context.measureText('M').width;
      cellHeight = size * 1.55;
    };

    const draw = (now = performance.now()) => {
      const {text, kinds: prefixes, pulses, pointer, theme} = current;
      const columns = Math.ceil(width / cellWidth);
      const rows = Math.ceil(height / cellHeight);
      const centreX = width * 0.72;
      const centreY = height * 0.5;
      const reach = Math.hypot(width, height) * 0.62;
      // Six seconds per breath: out for three, in for three.
      const breath = reduced ? 0.55 : (Math.sin((now / 6000) * Math.PI * 2 - Math.PI / 2) + 1) / 2;
      const ring = 60 + breath * reach;
      const band = 90 + breath * 60;
      current.pulses = pulses.filter((start) => now - start < 1600);

      context.clearRect(0, 0, width, height);
      for (let row = 0; row < rows; row += 1) {
        const offset = (row * 37) % text.length;
        for (let column = 0; column < columns; column += 1) {
          const index = (offset + row * columns + column) % text.length;
          const x = column * cellWidth;
          const y = row * cellHeight;
          const distance = Math.hypot(x - centreX, y - centreY);
          let glow = Math.exp(-((distance - ring) ** 2) / (2 * band * band));
          for (const start of current.pulses) {
            const age = (now - start) / 1600;
            const radius = age * reach * 1.2;
            glow += (1 - age) * Math.exp(-((distance - radius) ** 2) / 1800);
          }
          if (pointer) {
            glow += 0.8 * Math.exp(-(((x - pointer.x) ** 2) + ((y - pointer.y) ** 2)) / 9000);
          }
          // Quieter on the left, where the wordmark and copy sit.
          const calm = 0.2 + 0.8 * Math.min(Math.max((x / width - 0.25) / 0.45, 0), 1);
          const prefix = prefixes[index];
          const alpha = Math.min((0.045 + glow * 0.22) * calm * (prefix ? 1.6 : 1), 0.6);
          const [r, g, b] = theme[prefix];
          context.fillStyle = `rgba(${r},${g},${b},${alpha.toFixed(2)})`;
          context.fillText(text[index], x, y);
        }
      }
    };
    current.draw = draw;

    // Frames are only scheduled while the field is on screen and the tab is
    // visible; start() resumes the loop when either changes back.
    const loop = (now) => {
      if (!visible || document.hidden) {
        frame = 0;
        return;
      }
      frame = requestAnimationFrame(loop);
      if (now - last < 33) {
        return;
      }
      last = now;
      draw(now);
    };
    const start = () => {
      if (!reduced && !frame && visible && !document.hidden) {
        frame = requestAnimationFrame(loop);
      }
    };

    current.theme = readTheme(canvas);
    resize();
    draw();

    // Re-read colours when the reader switches between light and dark.
    const themeObserver = new MutationObserver(() => {
      current.theme = readTheme(canvas);
      draw();
    });
    themeObserver.observe(document.documentElement, {attributes: true, attributeFilter: ['data-theme']});

    const observer = new IntersectionObserver(([entry]) => {
      visible = entry.isIntersecting;
      start();
    });
    observer.observe(canvas);
    document.addEventListener('visibilitychange', start);
    start();

    const host = canvas.parentElement;
    const move = (event) => {
      const rect = canvas.getBoundingClientRect();
      current.pointer = {x: event.clientX - rect.left, y: event.clientY - rect.top};
    };
    const leave = () => {
      current.pointer = null;
    };
    host.addEventListener('pointermove', move);
    host.addEventListener('pointerleave', leave);

    const onResize = () => {
      resize();
      draw();
    };
    window.addEventListener('resize', onResize);
    document.fonts?.ready.then(onResize);

    return () => {
      cancelAnimationFrame(frame);
      observer.disconnect();
      themeObserver.disconnect();
      document.removeEventListener('visibilitychange', start);
      host.removeEventListener('pointermove', move);
      host.removeEventListener('pointerleave', leave);
      window.removeEventListener('resize', onResize);
    };
  }, []);

  return <canvas ref={canvasRef} className={className} aria-hidden="true" />;
});

export default ByteField;
