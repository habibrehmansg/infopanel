// node tools/check.mjs
// Checks that types/index.d.ts is what tools/gen-types.mjs generates from schemas/. With TSC set to a TypeScript
// compiler (TSC=path/to/tsc), also type-checks the types and every valid example against them: each scene, document,
// commit and API body under examples/ is assigned to its type as an object literal, so excess or misspelt members fail.
// Examples nested deeper than tsc can compare with the recursive JSON types (the jsonDepth examples) are left to the
// engine's tests.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { generate, pascalCase } from './gen-types.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const schemaDir = join(root, 'schemas');
const typesPath = join(root, 'types', 'index.d.ts');
const examples = join(root, 'examples');

const schemas = readdirSync(schemaDir)
  .filter((f) => f.endsWith('.schema.json'))
  .sort()
  .map((file) => ({ file, json: JSON.parse(readFileSync(join(schemaDir, file), 'utf8')) }));
const generated = generate(schemas);
if (!existsSync(typesPath) || readFileSync(typesPath, 'utf8') !== generated) {
  console.error('types/index.d.ts is out of date: run `npm run gen` in spec/.');
  process.exit(1);
}
console.log('types/index.d.ts is current.');

const tsc = process.env.TSC;
if (!tsc) {
  console.log('TSC not set: skipping the type-check of the examples.');
  process.exit(0);
}

const list = (dir) => readdirSync(join(examples, dir)).filter((f) => f.endsWith('.json')).sort().map((f) => `${dir}/${f}`);
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
const deep = all.filter(([file]) => depth(JSON.parse(readFileSync(join(examples, file), 'utf8'))) > maxTypedDepth);
const typed = all.filter((entry) => !deep.includes(entry));
const names = [...new Set(typed.map(([, t]) => t))].sort();
let source = `import type { ${names.join(', ')} } from './types';\n\n`;
typed.forEach(([file, type], i) => {
  source += `// ${file}\nexport const example${i}: ${type} = ${readFileSync(join(examples, file), 'utf8').trim()};\n\n`;
});

const dir = mkdtempSync(join(tmpdir(), 'spec-check-'));
try {
  writeFileSync(join(dir, 'types.d.ts'), generated);
  writeFileSync(join(dir, 'examples.ts'), source);
  const r = spawnSync(tsc, ['--noEmit', '--strict', '--target', 'ES2022', '--lib', 'ES2022', '--module', 'ESNext',
    '--moduleResolution', 'bundler', 'types.d.ts', 'examples.ts'], { cwd: dir, encoding: 'utf8' });
  if (r.status !== 0) {
    console.error(r.stdout || r.stderr || r.error?.message);
    process.exit(1);
  }
  console.log(`The types compile and ${typed.length} examples type-check.`);
  if (deep.length > 0)
    console.log(`Not type-checked (nested deeper than ${maxTypedDepth} levels): ${deep.map(([file]) => file).join(', ')}.`);
} finally {
  for (const f of readdirSync(dir)) unlinkSync(join(dir, f));
  rmdirSync(dir);
}
