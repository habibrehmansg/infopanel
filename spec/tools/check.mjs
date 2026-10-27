// node tools/check.mjs
// Checks that types/index.d.ts is what tools/gen-types.mjs generates from schemas/ and openapi.json (which checks both
// against their subsets, every $ref and the operation ids); that every definition of the API schema is used by a request
// or the preview socket in openapi.json; and that docs/api.md's request table lists exactly openapi.json's operations,
// one row each. With TSC set to a TypeScript compiler (TSC=path/to/tsc), also type-checks the types, every valid example
// against them (each scene, document, commit and API body under examples/ is assigned to its type as an object
// literal, so excess or misspelt members fail), and ApiOperations: its keys are the operations, and every example of a
// definition an operation names is assigned to that operation's request or response. Examples nested deeper than tsc
// can compare with the recursive JSON types (the jsonDepth examples) are left to the engine's tests.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { GenError, generate, isJsonMedia, pascalCase, readOperations } from './gen-types.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const schemaDir = join(root, 'schemas');
const typesPath = join(root, 'types', 'index.d.ts');
const openapiPath = join(root, 'openapi.json');
const apiDocPath = join(root, 'docs', 'api.md');
const examples = join(root, 'examples');
const API_ID = 'urn:infopanel:api:1';

const fail = (message) => {
  console.error(message);
  process.exit(1);
};

const schemas = readdirSync(schemaDir)
  .filter((f) => f.endsWith('.schema.json'))
  .sort()
  .map((file) => ({ file, json: JSON.parse(readFileSync(join(schemaDir, file), 'utf8')) }));
const openapi = { file: 'openapi.json', json: JSON.parse(readFileSync(openapiPath, 'utf8')) };
let generated;
let operations;
try {
  generated = generate(schemas, openapi);
  operations = readOperations(openapi, schemas);
} catch (e) {
  if (e instanceof GenError) fail(`gen-types: ${e.message}`);
  throw e;
}
if (!existsSync(typesPath) || readFileSync(typesPath, 'utf8') !== generated) {
  fail('types/index.d.ts is out of date: run `npm run gen` in spec/.');
}
console.log('types/index.d.ts is current.');

// ---- every API definition is used by a request or the socket ----

const byId = new Map(schemas.map((s) => [s.json.$id, s.json]));
/** Every `$ref` under `value`, resolved against `base` (a schema's $id), as [id, def name or null]. */
function refs(value, base, out = []) {
  if (Array.isArray(value)) value.forEach((v) => refs(v, base, out));
  else if (value !== null && typeof value === 'object') {
    for (const [k, v] of Object.entries(value)) {
      if (k === '$ref' && typeof v === 'string') {
        const hash = v.indexOf('#');
        const id = hash < 0 ? v : v.slice(0, hash) || base;
        const m = /^\/\$defs\/([^/]+)$/.exec(hash < 0 ? '' : v.slice(hash + 1));
        if (id) out.push([id, m ? m[1] : null]);
      } else refs(v, base, out);
    }
  }
  return out;
}
const reached = new Set();
const pending = refs(openapi.json, null);
while (pending.length > 0) {
  const [id, def] = pending.pop();
  const key = `${id}#${def ?? ''}`;
  if (reached.has(key) || !byId.has(id)) continue;
  reached.add(key);
  const schema = byId.get(id);
  refs(def === null ? schema : schema.$defs?.[def], id, pending);
}
const apiSchema = byId.get(API_ID);
const unused = Object.keys(apiSchema.$defs).filter((def) => !reached.has(`${API_ID}#${def}`));
if (unused.length > 0) {
  fail(`schemas/api.schema.json: ${unused.map((d) => `$defs/${d}`).join(', ')} ${unused.length === 1 ? 'is' : 'are'} used by no request or socket message in openapi.json.`);
}

// ---- docs/api.md's request table lists exactly the operations ----

const docLines = readFileSync(apiDocPath, 'utf8').split('\n');
const header = docLines.findIndex((l) => /^\|\s*Request\s*\|\s*Body\s*\|\s*Answer\s*\|\s*$/.test(l));
if (header < 0) fail('docs/api.md: no request table (a "| Request | Body | Answer |" header).');
const rows = new Map();
const problems = [];
for (let i = header + 2; i < docLines.length && docLines[i].startsWith('|'); i++) {
  const cell = docLines[i].split('|')[1].trim();
  const m = /^`(GET|HEAD|POST|PUT|PATCH|DELETE|OPTIONS|TRACE) (\/[^`\s]*)`$/.exec(cell);
  if (!m) problems.push(`docs/api.md:${i + 1}: a request table row names one request, as \`METHOD /path\`: ${cell}`);
  else if (rows.has(`${m[1]} ${m[2]}`)) problems.push(`docs/api.md:${i + 1}: \`${m[1]} ${m[2]}\` has a row already (line ${rows.get(`${m[1]} ${m[2]}`)})`);
  else rows.set(`${m[1]} ${m[2]}`, i + 1);
}
const keys = new Set(operations.map((op) => op.key));
for (const [key, line] of rows) if (!keys.has(key)) problems.push(`docs/api.md:${line}: \`${key}\` is not an operation in openapi.json`);
for (const op of operations) if (!rows.has(op.key)) problems.push(`openapi.json: \`${op.key}\` (${op.operationId}) has no row in docs/api.md's request table`);
if (problems.length > 0) fail(problems.join('\n'));
console.log(`openapi.json: ${operations.length} operations, every api definition used; docs/api.md's request table lists them all.`);

const tsc = process.env.TSC;
if (!tsc) {
  console.log('TSC not set: skipping the type-check of the examples and ApiOperations.');
  process.exit(0);
}

const list = (dir) => (existsSync(join(examples, dir)) ? readdirSync(join(examples, dir)).filter((f) => f.endsWith('.json')).sort().map((f) => `${dir}/${f}`) : []);
const all = [
  ...list('scene/valid').map((f) => [f, 'Scene']),
  ...list('document/valid').map((f) => [f, 'Document']),
  ...list('commit/valid').map((f) => [f, 'CommitRequest']),
  ...readdirSync(join(examples, 'api')).sort().flatMap((def) => list(`api/${def}`).map((f) => [f, pascalCase(def)])),
];
// Nesting as jsonDepth counts it ([] is 1), without recursion.
const depth = (value) => {
  let max = 0;
  const pending = [[value, 1]];
  while (pending.length > 0) {
    const [v, d] = pending.pop();
    if (v === null || typeof v !== 'object') continue;
    max = Math.max(max, d);
    for (const child of Array.isArray(v) ? v : Object.values(v)) pending.push([child, d + 1]);
  }
  return max;
};
const maxTypedDepth = 32;
const text = (file) => readFileSync(join(examples, file), 'utf8').trim();
const deep = all.filter(([file]) => depth(JSON.parse(text(file))) > maxTypedDepth);
const typed = all.filter((entry) => !deep.includes(entry));
const names = [...new Set(typed.map(([, t]) => t))].sort();
let source = `import type { ApiOperations, ${names.join(', ')} } from './types';\n\n`;
typed.forEach(([file, type], i) => {
  source += `// ${file}\nexport const example${i}: ${type} = ${text(file)};\n\n`;
});

// ApiOperations: exactly the operations, and the examples of each body an operation names, as that body.
const keyUnion = operations.map((op) => JSON.stringify(op.key)).join(' | ');
source += `type Operation = ${keyUnion};\n`;
source += 'export const keysAreTheOperations: [Exclude<keyof ApiOperations, Operation>, Exclude<Operation, keyof ApiOperations>] extends [never, never] ? true : false = true;\n\n';
const apiExamples = (def) => list(`api/${def}`).filter((f) => !deep.some(([d]) => d === f)).map(text);
/** Example values of a body schema: a definition's examples, an array of one, or each branch's of a oneOf. */
const bodyExamples = (schema) => {
  if (schema === null || typeof schema !== 'object') return [];
  const m = typeof schema.$ref === 'string' ? /^urn:infopanel:api:1#\/\$defs\/([^/]+)$/.exec(schema.$ref) : null;
  if (m) return apiExamples(m[1]);
  if (schema.type === 'array' && schema.items) return bodyExamples(schema.items).map((e) => `[${e}]`);
  if (Array.isArray(schema.oneOf)) return schema.oneOf.flatMap(bodyExamples);
  return [];
};
let bodies = 0;
for (const op of operations) {
  const at = `ApiOperations[${JSON.stringify(op.key)}]`;
  for (const { media, schema } of op.request?.content ?? []) {
    if (!isJsonMedia(media)) continue;
    for (const e of bodyExamples(schema)) source += `export const body${bodies++}: NonNullable<${at}['request']> = ${e};\n`;
  }
  for (const r of op.responses) {
    for (const { media, schema } of r.content) {
      if (!isJsonMedia(media)) continue;
      for (const e of bodyExamples(schema)) source += `export const body${bodies++}: ${at}['responses'][${r.status}] = ${e};\n`;
    }
  }
}

const dir = mkdtempSync(join(tmpdir(), 'spec-check-'));
try {
  writeFileSync(join(dir, 'types.d.ts'), generated);
  writeFileSync(join(dir, 'examples.ts'), source);
  const r = spawnSync(tsc, ['--noEmit', '--strict', '--target', 'ES2022', '--lib', 'ES2022', '--module', 'ESNext',
    '--moduleResolution', 'bundler', 'types.d.ts', 'examples.ts'], { cwd: dir, encoding: 'utf8' });
  if (r.status !== 0) fail(r.stdout || r.stderr || r.error?.message);
  console.log(`The types compile and ${typed.length} examples type-check.`);
  console.log(`ApiOperations type-checks: its keys are the ${operations.length} operations, and ${bodies} examples type-check as their requests' and responses' bodies.`);
  if (deep.length > 0)
    console.log(`Not type-checked (nested deeper than ${maxTypedDepth} levels): ${deep.map(([file]) => file).join(', ')}.`);
} finally {
  for (const f of readdirSync(dir)) unlinkSync(join(dir, f));
  rmdirSync(dir);
}
