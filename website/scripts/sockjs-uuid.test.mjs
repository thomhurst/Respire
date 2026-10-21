import assert from 'node:assert/strict';
import {once} from 'node:events';
import {createServer} from 'node:http';
import {createRequire} from 'node:module';
import {test} from 'node:test';

// Resolve through the actual development-server consumer, including nested copies.
const require = createRequire(import.meta.url);
const core = createRequire(require.resolve('@docusaurus/core/package.json'));
const devServer = createRequire(core.resolve('webpack-dev-server/package.json'));
const sockjs = createRequire(devServer.resolve('sockjs/package.json'));
const uuid = sockjs('uuid');

// GHSA-w5hq-g745-h8pq: keep the patched buffer contract when changing the
// SockJS-scoped override. v11 retains the CommonJS named exports SockJS uses.
test('SockJS resolves UUID functions with output-buffer bounds checks', () => {
  const generators = [
    (buffer, offset) => uuid.v3('respire', uuid.v3.DNS, buffer, offset),
    (buffer, offset) => uuid.v5('respire', uuid.v5.DNS, buffer, offset),
    (buffer, offset) => uuid.v6({}, buffer, offset),
  ];
  for (const generate of generators) {
    for (const [length, offset] of [[8, 0], [16, -1], [16, 1]]) {
      const buffer = new Uint8Array(length).fill(0xaa);
      assert.throws(() => generate(buffer, offset), RangeError);
      assert.ok(buffer.every(byte => byte === 0xaa), 'invalid writes must leave the buffer untouched');
    }
    const buffer = new Uint8Array(20).fill(0xaa);
    assert.equal(generate(buffer, 2), buffer);
    assert.ok(uuid.validate(uuid.stringify(buffer, 2)));
    assert.deepEqual([...buffer.slice(0, 2), ...buffer.slice(18)], [0xaa, 0xaa, 0xaa, 0xaa]);
  }
});

test('SockJS creates a v4 connection ID and exchanges WebSocket messages', {timeout: 10_000}, async (t) => {
  const server = createServer();
  const transport = devServer('sockjs').createServer({log() {}});
  let connection;
  transport.on('connection', value => {
    connection = value;
    connection.on('data', data => connection.write(data));
  });
  transport.installHandlers(server, {prefix: '/sockjs'});
  let socket;
  try {
    server.listen(0, '127.0.0.1');
    await once(server, 'listening', {signal: t.signal});
    socket = new WebSocket(`ws://127.0.0.1:${server.address().port}/sockjs/000/test/websocket`);
    const [opened] = await once(socket, 'message', {signal: t.signal});
    assert.equal(opened.data, 'o');
    assert.ok(uuid.validate(connection.id));
    assert.equal(uuid.version(connection.id), 4);
    const received = once(socket, 'message', {signal: t.signal});
    socket.send(JSON.stringify(['Respire live reload']));
    const [message] = await received;
    assert.equal(message.data, 'a["Respire live reload"]');
  } finally {
    if (socket && socket.readyState !== WebSocket.CLOSED) {
      const closed = once(socket, 'close');
      socket.close();
      await closed;
    }
    connection?.close();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  }
});
