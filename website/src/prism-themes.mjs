// Code colours reuse the RESP type palette from custom.css so a string in C#
// and a bulk string on the wire read as the same kind of thing.

function theme({background, plain, comment, keyword, string, number, type, method, punctuation}) {
  return {
    plain: {color: plain, backgroundColor: background},
    styles: [
      {types: ['comment', 'prolog', 'doctype', 'cdata'], style: {color: comment, fontStyle: 'italic'}},
      {types: ['keyword', 'builtin', 'important', 'atrule', 'operator-keyword'], style: {color: keyword}},
      {types: ['string', 'char', 'attr-value', 'regex', 'url', 'interpolation-string'], style: {color: string}},
      {types: ['number', 'boolean', 'constant', 'symbol'], style: {color: number}},
      {types: ['class-name', 'namespace', 'type-expression', 'return-type', 'generic'], style: {color: type}},
      {types: ['function', 'method', 'attr-name', 'property'], style: {color: method}},
      {types: ['punctuation', 'operator'], style: {color: punctuation}},
      {types: ['variable', 'parameter'], style: {color: plain}},
      {types: ['deleted'], style: {color: '#b0493b'}},
      {types: ['inserted'], style: {color: '#3f7d5f'}},
    ],
  };
}

export const respireLight = theme({
  background: '#ffffff',
  plain: '#1a1c24',
  comment: '#8a8d98',
  keyword: '#3f4c9a',
  string: '#3f7d5f',
  number: '#33788a',
  type: '#96701f',
  method: '#9b4b72',
  punctuation: '#6b6f7c',
});

export const respireDark = theme({
  background: '#1b1e26',
  plain: '#e7e8ec',
  comment: '#7f8494',
  keyword: '#a3adf0',
  string: '#93cdb0',
  number: '#8fc6d3',
  type: '#dcc085',
  method: '#dba3c1',
  punctuation: '#9a9eaa',
});
