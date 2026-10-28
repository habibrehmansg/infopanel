import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { generate } from './gen-types.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const schemaFiles = readdirSync(join(root, 'schemas')).filter((f) => f.endsWith('.schema.json')).sort();

/**
 * Runs tools/check.mjs on a copy of this folder whose openapi.json is `openapi` (its types generated from it), in a
 * scratch folder whose files are removed afterwards. TSC is unset: the type-check of the examples isn't run.
 */
function checkWith(openapi) {
  const dir = mkdtempSync(join(tmpdir(), 'spec-check-'));
  const written = [];
  const folders = [];
  const folder = (name) => {
    mkdirSync(join(dir, name));
    folders.push(join(dir, name));
  };
  const write = (path, text) => {
    writeFileSync(join(dir, path), text);
    written.push(join(dir, path));
  };
  const copy = (path) => {
    copyFileSync(join(root, path), join(dir, path));
    written.push(join(dir, path));
  };
  try {
    for (const f of ['schemas', 'types', 'docs', 'tools']) folder(f);
    for (const f of schemaFiles) copy(`schemas/${f}`);
    for (const f of ['docs/api.md', 'tools/check.mjs', 'tools/gen-types.mjs']) copy(f);
    write('openapi.json', JSON.stringify(openapi, null, 2));
    const schemas = schemaFiles.map((file) => ({ file, json: JSON.parse(readFileSync(join(root, 'schemas', file), 'utf8')) }));
    write('types/index.d.ts', generate(schemas, { file: 'openapi.json', json: openapi }));
    const env = { ...process.env };
    delete env.TSC;
    return spawnSync(process.execPath, [join(dir, 'tools', 'check.mjs')], { encoding: 'utf8', env });
  } finally {
    for (const f of written) unlinkSync(f);
    for (const f of folders.reverse()) rmdirSync(f);
    rmdirSync(dir);
  }
}

const openapi = () => JSON.parse(readFileSync(join(root, 'openapi.json'), 'utf8'));

/** openapi.json with GET /api/fonts answering an array of an inline string enumeration described by `description`. */
function withInlineEnum(description) {
  const json = openapi();
  const schema = json.paths['/api/fonts'].get.responses['200'].content['application/json'].schema;
  assert.equal(schema.type, 'array');
  schema.items = { type: 'string', enum: ['a', 'b'], description };
  return json;
}

test('the spec as it is passes', () => {
  const r = checkWith(openapi());
  assert.equal(r.status, 0, r.stdout + r.stderr);
});

test('an enumeration inline in a response, with no fallback, fails the check where it is', () => {
  const r = checkWith(withInlineEnum('A family.'));
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.match(r.stderr, /doesn't say what a client does with a value it doesn't know/);
  assert.match(r.stderr, /^openapi\.json#\/paths\/~1api~1fonts\/get\/responses\/200\/content\/application~1json\/schema\/items$/m);
});

test('an enumeration inline in a response, with a fallback, passes', () => {
  const r = checkWith(withInlineEnum("A family; a client shows a value it doesn't know as it is."));
  assert.equal(r.status, 0, r.stdout + r.stderr);
});

/** openapi.json with GET /api/fonts answering a header whose schema is `schema`. */
function withHeader(schema) {
  const json = openapi();
  json.paths['/api/fonts'].get.responses['200'].headers = { 'X-Meta': { schema } };
  return json;
}

test("a schema's data (default, examples) is not read as a schema, even when it holds an enum member", () => {
  const r = checkWith(withHeader({ type: 'object', default: { enum: ['a'] }, examples: [{ enum: ['b'] }] }));
  assert.equal(r.status, 0, r.stdout + r.stderr);
});

test('an enumeration in a response header, with no fallback, fails the check where it is', () => {
  const r = checkWith(withHeader({ type: 'string', enum: ['a'] }));
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.match(r.stderr, /^openapi\.json#\/paths\/~1api~1fonts\/get\/responses\/200\/headers\/X-Meta\/schema$/m);
});
