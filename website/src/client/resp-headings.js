// Tags each doc page title with its UTF-8 byte length so CSS can show it as a
// RESP bulk string header, for example "$15" above "Getting started".
const encoder = typeof TextEncoder === 'undefined' ? null : new TextEncoder();

export function onRouteDidUpdate() {
  const title = document.querySelector('.theme-doc-markdown h1');
  if (title && encoder) {
    title.dataset.respLength = String(encoder.encode(title.textContent.trim()).length);
  }
}
