import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { DEFAULT_MANIFEST, ManifestError, V1_ITEM_ID, generateV1Items } from './gen-v1-items.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));

const kind = (name, properties, extra = {}) => ({ kind: name, clrType: `${name}Item`, label: name, properties, defaults: {}, ...extra });
const prop = (name, type, extra = {}) => ({ name, type, readOnly: false, ...extra });
const minimal = () => [
  kind('image', [prop('name', 'string'), prop('x', 'int'), prop('type', 'enum', { enumValues: ['FILE', 'URL'] })]),
  kind('group', [prop('hidden', 'bool')]),
  kind('gauge', [prop('id', 'uint'), prop('minValue', 'double')]),
];

test('each kind gets an item and a props definition, and v1Item is a oneOf of the items in manifest order', () => {
  const schema = JSON.parse(generateV1Items(minimal()));
  assert.equal(schema.$id, V1_ITEM_ID);
  assert.deepEqual(Object.keys(schema.$defs), ['v1Item', 'v1ImageItem', 'v1ImageProps', 'v1GroupItem', 'v1GroupProps', 'v1GaugeItem', 'v1GaugeProps']);
  assert.deepEqual(schema.$defs.v1Item.oneOf, [{ $ref: '#/$defs/v1ImageItem' }, { $ref: '#/$defs/v1GroupItem' }, { $ref: '#/$defs/v1GaugeItem' }]);
  assert.match(schema.$defs.v1Item.description, /doesn't know/);
  const image = schema.$defs.v1ImageItem;
  assert.deepEqual(image.required, ['id', 'kind', 'props', 'meta']);
  assert.equal(image.additionalProperties, false);
  assert.deepEqual(image.properties.kind, { const: 'image' });
  assert.deepEqual(image.properties.meta, { $ref: 'urn:infopanel:api:1#/$defs/v1ItemMeta' });
});

test('properties map by type: whole numbers in range, booleans, enums, and nullable strings and numbers', () => {
  const schema = JSON.parse(generateV1Items(minimal()));
  const image = schema.$defs.v1ImageProps;
  assert.deepEqual(image.required, ['name', 'x', 'type']);
  assert.equal(image.additionalProperties, false);
  assert.deepEqual(image.properties.name, { type: ['string', 'null'] });
  assert.deepEqual(image.properties.x, { type: 'integer', minimum: -2147483648, maximum: 2147483647 });
  assert.deepEqual(image.properties.type, { enum: ['FILE', 'URL'] });
  assert.deepEqual(schema.$defs.v1GroupProps.properties.hidden, { type: 'boolean' });
  assert.deepEqual(schema.$defs.v1GaugeProps.properties.id, { type: 'integer', minimum: 0, maximum: 4294967295 });
  assert.deepEqual(schema.$defs.v1GaugeProps.properties.minValue, { type: ['number', 'null'] });
});

test('a group holds its children and a gauge its frames as image items', () => {
  const schema = JSON.parse(generateV1Items(minimal()));
  assert.deepEqual(schema.$defs.v1GroupItem.required, ['id', 'kind', 'props', 'meta', 'children']);
  assert.deepEqual(schema.$defs.v1GroupItem.properties.children.items, { $ref: '#/$defs/v1Item' });
  assert.deepEqual(schema.$defs.v1GaugeItem.required, ['id', 'kind', 'props', 'meta', 'images']);
  assert.deepEqual(schema.$defs.v1GaugeItem.properties.images.items, { $ref: '#/$defs/v1ImageItem' });
  assert.equal(schema.$defs.v1ImageItem.properties.children, undefined);
});

test('a manifest it can\'t translate is refused', () => {
  assert.throws(() => generateV1Items([]), ManifestError);
  assert.throws(() => generateV1Items([...minimal(), kind('image', [])]), /listed twice/);
  assert.throws(() => generateV1Items([kind('image', [prop('a', 'decimal')])]), /unknown property type/);
  assert.throws(() => generateV1Items([kind('image', [prop('a', 'enum')])]), /enumValues/);
  assert.throws(() => generateV1Items([kind('image', [prop('a', 'int'), prop('a', 'bool')])]), /listed twice/);
  assert.throws(() => generateV1Items([kind('group', [])]), /image kind/);
});

test('schemas/v1item.schema.json is what the generator writes from the engine\'s kind manifest', () => {
  const manifest = join(root, DEFAULT_MANIFEST);
  assert.ok(existsSync(manifest), `${manifest} is missing`);
  assert.equal(readFileSync(join(root, 'schemas', 'v1item.schema.json'), 'utf8'), generateV1Items(JSON.parse(readFileSync(manifest, 'utf8'))));
});
