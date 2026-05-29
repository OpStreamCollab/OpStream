// ot-text.js
// A faithful JavaScript port of OpStream's server-side TextOtEngine / TextOp
// (src/OpStream.Server/Engine/Text/*). It MUST stay byte-for-byte compatible
// with the server so the client can transform its own un-acknowledged edits
// and still converge with the authoritative server state.
//
// Op shape on the wire (camelCase, polymorphic discriminator "type"):
//   { components: [ { type:"retain", count:N },
//                   { type:"insert", text:"..." },
//                   { type:"delete", count:N } ] }
//
// Length units are UTF-16 code units (JS string .length == .NET string.Length),
// which also matches Monaco's offset model.

export const retain = (count) => ({ type: "retain", count });
export const insert = (text)  => ({ type: "insert", text });
export const del    = (count) => ({ type: "delete", count });

export function isNoOp(op) {
  return !op || op.components.length === 0 || op.components.every(c => c.type === "retain");
}

function compLen(c) {
  return c.type === "insert" ? c.text.length : c.count;
}

// Merge adjacent same-type components and drop zero-length ones.
// Mirrors TextOtEngine.Compact.
export function compact(op) {
  const out = [];
  for (const c of op.components) {
    if (compLen(c) === 0) continue;
    const last = out[out.length - 1];
    if (last && last.type === c.type) {
      if (c.type === "insert") { out[out.length - 1] = insert(last.text + c.text); continue; }
      out[out.length - 1] = { type: c.type, count: last.count + c.count };
      continue;
    }
    out.push(c);
  }
  return { components: out };
}

// Apply an op to a string. Mirrors TextOtEngine.Apply, including the implicit
// "retain the tail" behaviour: any text not covered by the op is kept.
export function apply(text, op) {
  let result = "";
  let index = 0;
  for (const c of op.components) {
    if (c.type === "retain") {
      if (index < text.length) {
        const n = Math.min(c.count, text.length - index);
        result += text.substr(index, n);
      }
      index += c.count;
    } else if (c.type === "insert") {
      result += c.text;
    } else { // delete
      index += c.count;
    }
  }
  if (index < text.length) result += text.substr(index);
  return result;
}

// Shrink a (possibly already-sliced) component by `count`, returning the
// remainder, or null when fully consumed.
function remainder(c, count) {
  if (compLen(c) === count) return null;
  if (c.type === "insert") return insert(c.text.substring(count));
  return { type: c.type, count: c.count - count };
}

// Transform `incoming` so it can be applied after `existing`.
// Mirrors TextOtEngine.Transform exactly, including tie-breaking.
//   priority = "existingWins"  -> on insert/insert ties, existing goes first
//   priority = "incomingWins"  -> on insert/insert ties, incoming goes first
export function transform(incoming, existing, priority) {
  const result = [];
  const inc = incoming.components, ext = existing.components;
  let i = 0, j = 0;
  let curInc = inc.length ? inc[0] : null;
  let curExt = ext.length ? ext[0] : null;

  const nextInc = () => { i++; curInc = i < inc.length ? inc[i] : null; };
  const nextExt = () => { j++; curExt = j < ext.length ? ext[j] : null; };

  while (curInc !== null || curExt !== null) {
    // Both sides insert at the same position: priority decides ordering.
    if (curInc && curInc.type === "insert" && curExt && curExt.type === "insert") {
      if (priority === "incomingWins") {
        result.push(curInc);
        nextInc();
      } else {
        result.push(retain(curExt.text.length));
        nextExt();
      }
      continue;
    }
    // Existing inserted text we did not author: retain over it.
    if (curExt && curExt.type === "insert") {
      result.push(retain(curExt.text.length));
      nextExt();
      continue;
    }
    // Our own insert survives as-is.
    if (curInc && curInc.type === "insert") {
      result.push(curInc);
      nextInc();
      continue;
    }
    if (curInc === null) break;          // finished incoming
    if (curExt === null) {               // existing exhausted: keep remaining incoming
      result.push(curInc);
      nextInc();
      continue;
    }

    // Both are retain or delete: consume the overlap.
    const lenInc = compLen(curInc);
    const lenExt = compLen(curExt);
    const len = Math.min(lenInc, lenExt);

    if (curInc.type === "retain" && curExt.type === "retain") {
      result.push(retain(len));
    } else if (curInc.type === "delete" && curExt.type === "retain") {
      result.push(del(len));
    }
    // If existing is a delete, it already removed this range -> emit nothing.

    if (lenInc === len) nextInc(); else curInc = remainder(curInc, len);
    if (lenExt === len) nextExt(); else curExt = remainder(curExt, len);
  }

  return compact({ components: result });
}

// Simple iterator that can yield a component sliced to a requested length.
// Mirrors the internal OpIterator used by TextOtEngine.Compose.
class OpIterator {
  constructor(ops) { this.ops = ops; this.index = 0; this.offset = 0; }
  hasNext() { return this.index < this.ops.length; }
  peekType() { return this.hasNext() ? this.ops[this.index].type : null; }
  peekLength() {
    if (!this.hasNext()) return 0;
    return compLen(this.ops[this.index]) - this.offset;
  }
  next(length) {
    if (!this.hasNext()) throw new Error("No more ops.");
    const op = this.ops[this.index];
    const opLen = this.peekLength();
    const take = length != null ? Math.min(length, opLen) : opLen;
    const start = this.offset;
    this.offset += take;
    if (this.offset >= compLen(op)) { this.index++; this.offset = 0; }
    if (op.type === "insert") return insert(op.text.substr(start, take));
    return { type: op.type, count: take };
  }
}

// Compose two ops so that apply(apply(s, a), b) == apply(s, compose(a, b)).
// Mirrors TextOtEngine.Compose. Returns null if the result is a no-op.
export function compose(a, b) {
  if (!a) return b;
  if (!b) return a;

  const result = [];
  const ia = new OpIterator(a.components);
  const ib = new OpIterator(b.components);

  while (ia.hasNext() || ib.hasNext()) {
    if (ia.peekType() === "delete") { result.push(ia.next()); continue; }
    if (ib.peekType() === "insert") { result.push(ib.next()); continue; }
    if (!ia.hasNext()) { result.push(ib.next()); continue; }
    if (!ib.hasNext()) { result.push(ia.next()); continue; }

    const len = Math.min(ia.peekLength(), ib.peekLength());
    const opA = ia.next(len);
    const opB = ib.next(len);

    if (opA.type === "retain" && opB.type === "retain") result.push(retain(len));
    else if (opA.type === "insert" && opB.type === "retain") result.push(insert(opA.text));
    else if (opA.type === "insert" && opB.type === "delete") { /* cancel out */ }
    else if (opA.type === "retain" && opB.type === "delete") result.push(del(len));
  }

  const composed = compact({ components: result });
  return isNoOp(composed) ? null : composed;
}

// Transform a single caret/anchor offset through an op (for cursor survival).
export function transformOffset(offset, op) {
  if (!op) return offset;
  let pos = 0;
  let out = offset;
  for (const c of op.components) {
    if (c.type === "retain") {
      pos += c.count;
    } else if (c.type === "insert") {
      if (pos <= out) out += c.text.length;
      // inserts do not advance the source position
    } else { // delete
      if (pos < out) out -= Math.min(c.count, out - pos);
      pos += c.count;
    }
  }
  return Math.max(0, out);
}

// Build a single TextOp from a Monaco IModelContentChangedEvent.changes array.
// Each change = { rangeOffset, rangeLength, text }. Monaco delivers them sorted
// descending by offset; we sweep ascending and rely on the implicit tail-retain.
export function fromMonacoChanges(changes) {
  const sorted = [...changes].sort((a, b) => a.rangeOffset - b.rangeOffset);
  const comps = [];
  let pos = 0;
  for (const c of sorted) {
    if (c.rangeOffset > pos) comps.push(retain(c.rangeOffset - pos));
    if (c.rangeLength > 0)   comps.push(del(c.rangeLength));
    if (c.text.length > 0)   comps.push(insert(c.text));
    pos = c.rangeOffset + c.rangeLength;
  }
  return compact({ components: comps });
}
