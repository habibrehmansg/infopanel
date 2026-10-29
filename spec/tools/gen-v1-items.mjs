#!/usr/bin/env node
// Turns the 1.x kind manifest (the item JSON of the 14 1.x kinds, InfoPanel.Core.Tests/Snapshots/kinds.json) into the
// schema of the 1.x items a 1.x import answers with (schemas/v1item.schema.json, docs/import.md#items). Dependency-free:
// Node >= 22, built-ins only.
//
//   node tools/gen-v1-items.mjs --manifest <kinds.json> --out <v1item.schema.json> [--check]
//
// Exit codes: 0 ok, 1 --check found a difference, 2 bad arguments or a manifest that can't be translated.

import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export const V1_ITEM_ID = 'urn:infopanel:v1item:1';

/** The default manifest, relative to spec/: the engine's kind manifest snapshot. */
export const DEFAULT_MANIFEST = '../InfoPanel.Core.Tests/Snapshots/kinds.json';

/** A manifest the generator can't translate. */
export class ManifestError extends Error {
  constructor(text) {
    super(text);
    this.name = 'ManifestError';
  }
}

const isObj = (v) => typeof v === 'object' && v !== null && !Array.isArray(v);
const NAME = /^[a-z][A-Za-z0-9]*$/;

/** `sensorImage` -> `SensorImage`. */
const upper = (s) => s[0].toUpperCase() + s.slice(1);

/**
 * The schema of one property, by its manifest type, as the engine writes the item JSON: an int and a uint as whole
 * numbers in their range, a bool, an enum by its value names, and a string or a number that may be null (a string the
 * 1.x file left null, a number that isn't finite).
 */
function propertySchema(kind, p) {
  switch (p.type) {
    case 'int':
      return { type: 'integer', minimum: -2147483648, maximum: 2147483647 };
    case 'uint':
      return { type: 'integer', minimum: 0, maximum: 4294967295 };
    case 'float':
    case 'double':
      return { type: ['number', 'null'] };
    case 'bool':
      return { type: 'boolean' };
    case 'string':
      return { type: ['string', 'null'] };
    case 'enum':
      if (!Array.isArray(p.enumValues) || p.enumValues.length === 0 || !p.enumValues.every((v) => typeof v === 'string')) {
        throw new ManifestError(`${kind}.${p.name}: an enum needs enumValues, a non-empty list of names`);
      }
      return { enum: [...p.enumValues] };
    default:
      throw new ManifestError(`${kind}.${p.name}: unknown property type ${JSON.stringify(p.type)}`);
  }
}

/**
 * The schema (as text, ending in a newline) for `manifest`, the parsed kind manifest: `v1Item`, a oneOf of one item
 * definition per kind (`v1{Kind}Item`), each with its props (`v1{Kind}Props`, closed, every property required, in the
 * manifest's order). A `group` item holds its `children`, a `gauge` its frames as `images` (image items), as the 1.x item
 * JSON nests them; `meta` is the API schema's `v1ItemMeta`.
 */
export function generateV1Items(manifest) {
  if (!Array.isArray(manifest) || manifest.length === 0) throw new ManifestError('the manifest must be a non-empty array of kinds');
  const defs = {};
  const kinds = [];
  for (const k of manifest) {
    if (!isObj(k) || typeof k.kind !== 'string' || !NAME.test(k.kind)) throw new ManifestError(`a kind needs a camelCase "kind": ${JSON.stringify(k?.kind)}`);
    if (kinds.includes(k.kind)) throw new ManifestError(`the kind ${k.kind} is listed twice`);
    if (!Array.isArray(k.properties)) throw new ManifestError(`${k.kind}: "properties" must be an array`);
    kinds.push(k.kind);
    const props = {};
    for (const p of k.properties) {
      if (!isObj(p) || typeof p.name !== 'string' || !NAME.test(p.name)) throw new ManifestError(`${k.kind}: a property needs a camelCase "name": ${JSON.stringify(p?.name)}`);
      if (p.name in props) throw new ManifestError(`${k.kind}.${p.name} is listed twice`);
      props[p.name] = propertySchema(k.kind, p);
    }

    const Kind = upper(k.kind);
    const label = typeof k.label === 'string' ? `, "${k.label}"` : '';
    const clr = typeof k.clrType === 'string' ? ` (1.x's ${k.clrType}${label})` : '';
    const item = {
      description: `An item of the 1.x kind \`${k.kind}\`${clr}.`,
      type: 'object',
      required: ['id', 'kind', 'props', 'meta'],
      additionalProperties: false,
      properties: {
        id: {
          description: "The 1.x item's Guid as stored, in lower case.",
          type: 'string',
          pattern: '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',
        },
        kind: { const: k.kind },
        props: { $ref: `#/$defs/v1${Kind}Props` },
        meta: { $ref: 'urn:infopanel:api:1#/$defs/v1ItemMeta' },
      },
    };
    if (k.kind === 'group') {
      item.required.push('children');
      item.properties.children = { description: "The group's items, in 1.x order, the first at the back.", type: 'array', items: { $ref: '#/$defs/v1Item' } };
    }
    if (k.kind === 'gauge') {
      item.required.push('images');
      item.properties.images = { description: "The gauge's frames, in 1.x order: image items.", type: 'array', items: { $ref: '#/$defs/v1ImageItem' } };
    }
    defs[`v1${Kind}Item`] = item;
    defs[`v1${Kind}Props`] = {
      description: `The properties of a \`${k.kind}\` item, every one of the 1.x model's, by its camelCase name.`,
      type: 'object',
      required: Object.keys(props),
      additionalProperties: false,
      properties: props,
    };
  }
  if (!kinds.includes('image')) throw new ManifestError('the manifest has no image kind, which gauge frames are');

  const schema = {
    $schema: 'https://json-schema.org/draft/2020-12/schema',
    $id: V1_ITEM_ID,
    description:
      'Generated by tools/gen-v1-items.mjs from the 1.x kind manifest (InfoPanel.Core.Tests/Snapshots/kinds.json). Do not edit; run `npm run gen`. ' +
      "The 1.x items of a 1.x import's answer (docs/import.md#items): one item definition per 1.x kind and one for its props, as the 1.x item JSON " +
      'writes them. The 1.x models are frozen, so these change only with the manifest.',
    $defs: {
      v1Item: {
        description:
          "A 1.x item, by its kind. What the engine sends is read tolerantly: a client skips an item whose kind it doesn't know, " +
          'reporting it as `import.itemType` (docs/import.md#items).',
        oneOf: kinds.map((kind) => ({ $ref: `#/$defs/v1${upper(kind)}Item` })),
      },
      ...defs,
    },
  };
  return format(schema, '') + '\n';
}

/** JSON with two-space indents, each value that fits on one line of 120 characters written on one line, as the hand-written schemas are. */
function format(value, indent) {
  const line = oneLine(value);
  if (value === null || typeof value !== 'object' || indent.length + line.length <= 120) return line;
  const inner = indent + '  ';
  if (Array.isArray(value)) return `[\n${value.map((v) => inner + format(v, inner)).join(',\n')}\n${indent}]`;
  return `{\n${Object.entries(value).map(([k, v]) => `${inner}${JSON.stringify(k)}: ${format(v, inner)}`).join(',\n')}\n${indent}}`;
}

/** A value on one line: `{ "a": 1, "b": [1, 2] }`. */
function oneLine(value) {
  if (value === null || typeof value !== 'object') return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(oneLine).join(', ')}]`;
  const members = Object.entries(value).map(([k, v]) => `${JSON.stringify(k)}: ${oneLine(v)}`);
  return members.length === 0 ? '{}' : `{ ${members.join(', ')} }`;
}

function parseArgs(argv) {
  const opts = { check: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--check') opts.check = true;
    else if ((a === '--manifest' || a === '--out') && i + 1 < argv.length) opts[a.slice(2)] = argv[++i];
    else throw new Error(`unknown argument ${a}`);
  }
  if (!opts.manifest) throw new Error('--manifest <kinds.json> is required');
  if (!opts.out) throw new Error('--out <v1item.schema.json> is required');
  return opts;
}

export function main(argv) {
  const usage = 'usage: node tools/gen-v1-items.mjs --manifest <kinds.json> --out <v1item.schema.json> [--check]';
  let opts;
  try {
    opts = parseArgs(argv);
  } catch (e) {
    console.error(`gen-v1-items: ${e.message}\n${usage}`);
    return 2;
  }
  let text;
  try {
    text = generateV1Items(JSON.parse(readFileSync(opts.manifest, 'utf8')));
  } catch (e) {
    if (e instanceof ManifestError || e instanceof SyntaxError || e?.code === 'ENOENT') {
      console.error(`gen-v1-items: ${opts.manifest}: ${e.message}`);
      return 2;
    }
    throw e;
  }
  if (opts.check) {
    if (!existsSync(opts.out) || readFileSync(opts.out, 'utf8') !== text) {
      console.error(`gen-v1-items: ${opts.out} is out of date: run \`npm run gen\``);
      return 1;
    }
    return 0;
  }
  writeFileSync(opts.out, text);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = main(process.argv.slice(2));
}
