// Tags each doc page title with its UTF-8 byte length so CSS can show it as a
// RESP bulk string header, for example "$15" above "Getting started".
const encoder = typeof TextEncoder === 'undefined' ? null : new TextEncoder();

function tagTitle() {
  const title = document.querySelector('.theme-doc-markdown h1');
  if (title && encoder) {
    title.dataset.respLength = String(encoder.encode(title.textContent.trim()).length);
  }
}

export function onRouteDidUpdate() {
  // On client-side navigation the route can update before the new page has
  // rendered, so tag again over the next few frames. Tagging is idempotent.
  let frames = 4;
  const run = () => {
    tagTitle();
    frames -= 1;
    if (frames > 0) {
      requestAnimationFrame(run);
    }
  };
  run();
}
