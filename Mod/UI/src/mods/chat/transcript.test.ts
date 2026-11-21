import assert from "node:assert/strict";
import { applyWireEvent, emptyTranscript } from "./transcript.ts";

let merged = emptyTranscript;
merged = applyWireEvent(merged, {
  kind: "tool",
  callId: "call-1",
  tool: "city_get_demand",
  text: "{}",
  status: "Working",
});
merged = applyWireEvent(merged, {
  kind: "tool",
  callId: "call-1",
  text: "{\"limit\":16}",
  status: "Working",
});
merged = applyWireEvent(merged, {
  kind: "tool",
  callId: "call-1",
  text: "{\"limit\":16}",
  status: "Idle",
  result: "{\"residential\":1}",
  output: "{\"output\":true}",
});
assert.equal(merged.lines.length, 1);
const row = merged.lines[0];
assert.equal(row.kind, "tool");
if (row.kind === "tool") {
  assert.equal(row.name, "city_get_demand");
  assert.equal(row.args, "{\"limit\":16}");
  assert.equal(row.result, "{\"residential\":1}");
  assert.equal(row.output, "{\"output\":true}");
  assert.equal(row.state, "done");
}

let builtin = emptyTranscript;
builtin = applyWireEvent(builtin, {
  kind: "tool",
  tool: "list_buildings",
  text: "{\"limit\":1}",
  status: "Idle",
});
builtin = applyWireEvent(builtin, {
  kind: "tool",
  tool: "list_buildings",
  text: "one building",
  status: "Idle",
});
assert.equal(builtin.lines.length, 1);
const closed = builtin.lines[0];
assert.equal(closed.kind, "tool");
if (closed.kind === "tool") {
  assert.equal(closed.name, "list_buildings");
  assert.equal(closed.args, "{\"limit\":1}");
  assert.equal(closed.result, "one building");
  assert.equal(closed.state, "done");
}

let pointed = emptyTranscript;
pointed = applyWireEvent(pointed, {
  kind: "user",
  text: "",
  places: [{ id: "p1", kind: "point", name: "Point 1", x: 455, z: -70, index: 0, version: 0 }],
});
assert.equal(pointed.lines.length, 1);
const pointedLine = pointed.lines[0];
assert.equal(pointedLine.kind, "user");
if (pointedLine.kind === "user") {
  assert.equal(pointedLine.text, "");
  assert.equal(pointedLine.places.length, 1);
  assert.equal(pointedLine.places[0].name, "Point 1");
  assert.equal(pointedLine.places[0].x, 455);
}
