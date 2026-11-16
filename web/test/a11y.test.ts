import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { tabDomIds, tabIndexAfter, tabMove } from '../src/lib/a11y';
import { ConfirmDialog, Tabs } from '../src/components/ui';

test('arrow keys move inside a tab set and Tab leaves it', () => {
  assert.equal(tabMove('ArrowRight'), 'next');
  assert.equal(tabMove('ArrowDown'), 'next');
  assert.equal(tabMove('ArrowLeft'), 'prev');
  assert.equal(tabMove('ArrowUp'), 'prev');
  assert.equal(tabMove('Home'), 'first');
  assert.equal(tabMove('End'), 'last');
  assert.equal(tabMove('Tab'), null);
  assert.equal(tabMove('Enter'), null);
  assert.equal(tabIndexAfter('next', 2, 3), 0);
  assert.equal(tabIndexAfter('prev', 0, 3), 2);
  assert.equal(tabIndexAfter('first', 2, 3), 0);
  assert.equal(tabIndexAfter('last', 0, 3), 2);
});

test('each tab controls its own panel', () => {
  assert.deepEqual(tabDomIds('tools', 'claude-code'), { tab: 'tools-tab-claude-code', panel: 'tools-panel-claude-code' });
  assert.notEqual(tabDomIds('tools', 'a').tab, tabDomIds('tools', 'b').tab);
  assert.notEqual(tabDomIds('tools', 'a/b').panel, tabDomIds('other', 'a/b').panel);
});

test('tabs render a roving tabindex and a labelled panel', () => {
  const html = renderToStaticMarkup(createElement(Tabs, {
    id: 'tools',
    label: 'Tool setup',
    value: 'one',
    onChange: () => {},
    items: [{ value: 'one', label: 'One' }, { value: 'two', label: 'Two' }],
    children: 'Panel one',
  }));
  assert.match(html, /role="tablist"/);
  assert.match(html, /aria-orientation="horizontal"/);
  assert.match(html, /aria-label="Tool setup"/);
  assert.match(html, /id="tools-tab-one"[^>]*role="tab"[^>]*aria-selected="true"[^>]*aria-controls="tools-panel-one"[^>]*tabindex="0"/);
  assert.match(html, /id="tools-tab-two"[^>]*role="tab"[^>]*aria-selected="false"[^>]*aria-controls="tools-panel-two"[^>]*tabindex="-1"/);
  assert.match(html, /role="tabpanel"[^>]*id="tools-panel-one"[^>]*aria-labelledby="tools-tab-one"/);
  assert.match(html, /Panel one/);
});

test('a confirmation dialog is named by its heading', () => {
  const html = renderToStaticMarkup(createElement(ConfirmDialog, {
    open: false,
    title: 'Revoke ci-bot?',
    confirmLabel: 'Revoke key',
    onConfirm: () => {},
    onCancel: () => {},
    children: 'Tools using this key get 401.',
  }));
  const labelled = html.match(/aria-labelledby="([^"]+)"/);
  assert.ok(labelled, html);
  const id = labelled[1];
  assert.match(html, new RegExp(`<h2 id="${id}">Revoke ci-bot\\?</h2>`));
  assert.match(html, /<dialog /);
  assert.doesNotMatch(html, /role="dialog"/);
});
