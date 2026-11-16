import assert from 'node:assert/strict';
import { test } from 'node:test';
import { locateLaunchTicket } from '../src/lib/api';

const ticket = 'ab'.repeat(24);

test('the path holds a launch ticket, and the address is cleaned', () => {
  const found = locateLaunchTicket(`http://127.0.0.1:8776/s/${ticket}/`);
  assert.equal(found.ticket, ticket);
  assert.equal(found.next, '/');
});

test('a query or hash session value is stripped and is not a ticket', () => {
  const query = locateLaunchTicket(`http://127.0.0.1:8776/?session=${ticket}#/setup`);
  assert.equal(query.ticket, null);
  assert.equal(query.next, '/#/setup');
  const hash = locateLaunchTicket(`http://127.0.0.1:8776/#session=${ticket}`);
  assert.equal(hash.ticket, null);
  assert.equal(hash.next, '/');
});

test('a short path segment is not a ticket', () => {
  assert.equal(locateLaunchTicket('http://127.0.0.1:8776/').ticket, null);
  assert.equal(locateLaunchTicket('http://127.0.0.1:8776/s/abc/').ticket, null);
  assert.equal(locateLaunchTicket('http://127.0.0.1:8776/?session=abc').ticket, null);
});
