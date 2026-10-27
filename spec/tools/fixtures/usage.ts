import type {
  ApiOperations, CommitRequest, Item, JsonValue, Labelled, NumberExpr, Op, PinnedText, Range, Scene, Severity, TaggedList,
} from './generated';

// Valid values compile.
export const expr: NumberExpr = { kind: 'add', args: [1, { kind: 'add', args: [2, 3] }] };
export const plain: NumberExpr = 4.5;
export const range: Range = [0, 10];
export const rangeWithUnit: Range = [0, 10, 'px'];
export const tagged: TaggedList = ['a', 1, 2, 3];
export const severity: Severity = 'warn';
export const place: Op = { op: 'setPlacement', item: 'i1', placement: { at: { x: 1, y: 2 } } };
export const remove: Op = { op: 'removeItem', item: 'i1' };
export const text: Item = { kind: 'text', text: 'hello', size: expr };
export const pinned: PinnedText = { kind: 'text', text: 'hi', pinned: true };
export const labelled: Labelled = { kind: 'image', src: 'a.png', label: 'x' };
export const scene: Scene = { id: 'main', items: [text, { kind: 'image', src: 'a.png' }], labels: { a: 'b' } };
export const request: CommitRequest = {
  id: 'abc',
  ops: [place, remove],
  'content-type': 'application/json',
  'x.y': 3,
  values: ['a', 1],
  meta: { anything: [1, null, { deep: true }] },
  extra: { free: 'form' },
  window: { size: { w: 1, h: 2 }, origin: [1, 2] },
};
export const json: JsonValue = { a: [1, 'b', null] };

// The types bite: each of these must be rejected.
// @ts-expect-error missing a required property
export const noPlacement: Op = { op: 'setPlacement', item: 'i1' };
// @ts-expect-error wrong discriminant
export const wrongOp: Op = { op: 'explode', item: 'i1' };
// @ts-expect-error a string is not a number expression
export const badExpr: NumberExpr = 'four';
// @ts-expect-error required tuple element missing
export const shortRange: Range = [0];
// @ts-expect-error rest elements must be numbers
export const badTagged: TaggedList = ['a', 'b'];
// @ts-expect-error unknown severity
export const badSeverity: Severity = 'fatal';
// @ts-expect-error ops is required
export const noOps: CommitRequest = { id: 'abc' };
// @ts-expect-error pinned must be true
export const notPinned: PinnedText = { kind: 'text', text: 'hi', pinned: false };

// The request index (ApiOperations).
type GetScene = ApiOperations['GET /scenes/{id}'];
export const sceneParams: GetScene['params'] = { id: 'main' };
export const sceneQuery: GetScene['query'] = { w: 320 };
export const noQuery: GetScene['query'] = {};
export const found: GetScene['responses'][200] = scene;
export const notModified: GetScene['responses'][304] = null;
export const commitBody: ApiOperations['POST /scenes/{id}/commit']['request'] = { id: 'abc', ops: [place] };
export const applied: ApiOperations['POST /scenes/{id}/commit']['responses'][200] = [place, remove];
export const conflict: ApiOperations['POST /scenes/{id}/commit']['responses'][409] = { kind: 'image', src: 'a.png' };
export const png: ApiOperations['GET /scenes/{id}/png']['responses'][200] = 'image/png';
export const drawn: ApiOperations['GET /scenes/{id}/png']['responses'][200] = scene;
export const bytes: ApiOperations['PUT /blobs']['request'] = 'application/octet-stream';
export const noBytes: ApiOperations['PUT /blobs']['request'] = undefined;
export type Keys = keyof ApiOperations;
export const everyKey: Keys[] = ['GET /scenes/{id}', 'HEAD /scenes/{id}', 'POST /scenes/{id}/commit', 'GET /scenes/{id}/png', 'PUT /blobs', 'GET /socket'];

// ...and bites: each of these must be rejected.
// @ts-expect-error an operation the index doesn't have
export type NoSuchOperation = ApiOperations['DELETE /scenes/{id}'];
// @ts-expect-error a status the operation doesn't answer
export type NoSuchStatus = GetScene['responses'][500];
// @ts-expect-error a path parameter is required
export const noId: GetScene['params'] = {};
// @ts-expect-error a query parameter the operation doesn't take
export const badQuery: GetScene['query'] = { h: 1 };
// @ts-expect-error a request body where the operation takes none
export const noBody: GetScene['request'] = { id: 'abc', ops: [] };
// @ts-expect-error the body is a Scene, not a string
export const wrongBody: GetScene['responses'][200] = 'scene';
// @ts-expect-error a media type the response doesn't have
export const wrongMedia: ApiOperations['GET /scenes/{id}/png']['responses'][200] = 'image/jpeg';
