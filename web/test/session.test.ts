import assert from 'node:assert/strict';
import { test } from 'node:test';
import { locateSession } from '../src/lib/api';

const token = 'ab'.repeat(24);

test('a path token survives a dropped query string, and the address is cleaned', () => {
  const found = locateSession(`http://127.0.0.1:8776/s/${token}/`);
  assert.equal(found.token, token);
  assert.equal(found.next, '/');
});

test('query and hash tokens still count, and a route hash is kept', () => {
  const query = locateSession(`http://127.0.0.1:8776/?session=${token}#/setup`);
  assert.equal(query.token, token);
  assert.equal(query.next, '/#/setup');
  const hash = locateSession(`http://127.0.0.1:8776/#session=${token}`);
  assert.equal(hash.token, token);
  assert.equal(hash.next, '/');
});

test('a short or missing token is not invented from the path', () => {
  assert.equal(locateSession('http://127.0.0.1:8776/').token, null);
  assert.equal(locateSession('http://127.0.0.1:8776/s/abc/').token, null);
  assert.equal(locateSession('http://127.0.0.1:8776/?session=abc').token, 'abc');
});
