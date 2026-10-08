import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { tokenize } from './monarch.mjs';
import { id, language } from '../stacktrace.js';

/** One line's coloured pieces — whitespace-only tokens dropped, they carry no colour worth asserting. */
const line = text => tokenize(id, language, text)[0].filter(([t]) => t.trim());

describe('stacktrace: .NET', () => {
  test('the exception: its type, and its message stressed', () => {
    assert.deepEqual(line('System.InvalidOperationException: Order 42 was not found.'), [
      ['System.InvalidOperationException', 'type'], [':', 'delimiter'], [' Order 42 was not found.', 'strong'],
    ]);
  });

  test('an inner exception, after its arrow', () => {
    assert.deepEqual(line(' ---> System.Collections.Generic.KeyNotFoundException: The given key was not present.'), [
      ['--->', 'keyword.flow'], ['System.Collections.Generic.KeyNotFoundException', 'type'],
      [':', 'delimiter'], [' The given key was not present.', 'strong'],
    ]);
  });

  test("an AggregateException's numbered inner exception", () => {
    assert.deepEqual(line(' ---> (Inner Exception #0) System.TimeoutException: Too slow.'), [
      ['--->', 'keyword.flow'], ['(Inner Exception #0) ', 'comment'], ['System.TimeoutException', 'type'],
      [':', 'delimiter'], [' Too slow.', 'strong'],
    ]);
  });

  test("an application frame: method stressed, file and line coloured", () => {
    assert.deepEqual(line('   at Shop.Orders.OrderService.Find(Int32 id) in /src/Shop/OrderService.cs:line 37'), [
      ['at', 'keyword'], [' Shop.Orders.OrderService.', ''], ['Find', 'strong'], ['(Int32 id)', ''],
      [' in ', 'keyword'], ['/src/Shop/OrderService.cs', 'string'], [':line ', 'keyword'], ['37', 'number'],
    ]);
  });

  test('a compiler-generated frame finds its method after the last dot', () => {
    const tokens = line('   at Shop.Api.Endpoint.<>c__DisplayClass0_0.<<Map>b__0>d.MoveNext() in /src/Endpoint.cs:line 18');
    assert.deepEqual(tokens.find(([, type]) => type === 'strong'), ['MoveNext', 'strong']);
  });

  test('a frame without a location', () => {
    assert.deepEqual(line('   at Shop.Orders.Cache.Load()'), [
      ['at', 'keyword'], [' Shop.Orders.Cache.', ''], ['Load', 'strong'], ['()', ''],
    ]);
  });

  test('framework frames are dimmed whole', () => {
    for (const frame of [
      '   at System.Collections.Generic.Dictionary`2.get_Item(TKey key)',
      '   at Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke(HttpContext httpContext)',
    ]) {
      assert.deepEqual(line(frame), [[frame, 'operator.sql']]);
    }
  });

  test("the runtime's markers", () => {
    for (const marker of [
      '   --- End of inner exception stack trace ---',
      '--- End of stack trace from previous location ---',
      '   <---',
    ]) {
      assert.deepEqual(line(marker), [[marker, 'comment']]);
    }
  });
});

describe('stacktrace: Node', () => {
  test('the error and a frame', () => {
    const [error, frame] = tokenize(id, language, "TypeError: Cannot read properties of undefined\n    at handler (/app/src/orders.js:10:5)");
    assert.deepEqual(error, [['TypeError', 'type'], [':', 'delimiter'], [' Cannot read properties of undefined', 'strong']]);
    assert.deepEqual(frame.filter(([t]) => t.trim()), [
      ['at', 'keyword'], ['handler', 'strong'], [' (', ''], ['/app/src/orders.js', 'string'], [':10:5', 'number'], [')', ''],
    ]);
  });

  test('an anonymous frame is its location', () => {
    assert.deepEqual(line('    at /app/src/index.js:3:1'), [['at', 'keyword'], ['/app/src/index.js', 'string'], [':3:1', 'number']]);
  });

  test('runtime and package frames are dimmed whole', () => {
    for (const frame of [
      '    at process.processTicksAndRejections (node:internal/process/task_queues:95:5)',
      '    at Layer.handle (/app/node_modules/express/lib/router/layer.js:95:5)',
    ]) {
      assert.deepEqual(line(frame), [[frame, 'operator.sql']]);
    }
  });

  test('a bare Error', () => {
    assert.deepEqual(line('Error'), [['Error', 'type']]);
  });
});

describe('stacktrace: context and plain text', () => {
  test("the context between Pondhawk.Logging's banners is handed to JSON", () => {
    const [open, context, close, error] = tokenize(id, language, [
      '--- Context -----------------------------------------',
      'System.Exception: shaped like a trace, but inside the context',
      '--- Exception ---------------------------------------',
      'System.Exception: boom',
    ].join('\n'));

    assert.deepEqual(open, [['--- Context -----------------------------------------', 'comment']]);
    assert.ok(context.every(([, type]) => type === ''), 'the JSON tokenizer has it, not the trace grammar');
    assert.deepEqual(close, [['--- Exception ---------------------------------------', 'comment']]);
    assert.equal(error[0][1], 'type', 'the trace resumes after the context');
  });

  test('prose stays plain, even with a colon or an "at"', () => {
    for (const prose of ['The disk is nearly full: 90% used.', 'Note: look at the logs.', 'Retrying at 10:05']) {
      assert.deepEqual(line(prose), [[prose, '']]);
    }
  });
});
