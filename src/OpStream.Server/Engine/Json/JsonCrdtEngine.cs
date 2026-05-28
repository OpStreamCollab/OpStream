using OpStream.Server.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpStream.Server.Engine.Json
{
    /// <summary>
    /// Implements a JSON document engine based on CRDT concepts using Last-Writer-Wins (LWW) per property.
    /// Supports hierarchical modification semantics and deterministic conflict resolution.
    /// </summary>
    public class JsonCrdtEngine : IOpEngine<Json_Document, JsonOpBatch>
    {
        /// <summary>
        /// Applies a batch of JSON operations to the given document state.
        /// </summary>
        /// <param name="state">The current JSON document state.</param>
        /// <param name="opBatch">The batch of operations to apply.</param>
        /// <returns>A new JSON document state after the operations are applied.</returns>
        public Json_Document Apply(Json_Document state, JsonOpBatch opBatch)
        {
            var internalBatch = Compose(JsonOpBatch.Create(), opBatch);
            if (internalBatch == null) return state;

            var newRegisters = new Dictionary<string, CrdtRegister>(state.Registers);
            
            // Collect all paths that this compacted batch will explicitly modify.
            // If a child is here, it's because in the original intent of the batch it happened AFTER the parent,
            // so we must protect it from being implicitly deleted by the parent.
            var pathsInBatch = new HashSet<string>();
            foreach (var op in internalBatch.Operations)
            {
                pathsInBatch.Add(op switch { SetPropertyOp s => s.Path, DeletePropertyOp d => d.Path, _ => "" });
            }

            foreach (var op in internalBatch.Operations)
            {
                ApplySingleOp(newRegisters, op, pathsInBatch);
            }

            return new Json_Document(newRegisters);
        }

        /// <summary>
        /// Applies a single JSON operation to the registers.
        /// </summary>
        private void ApplySingleOp(Dictionary<string, CrdtRegister> registers, JsonOp op, HashSet<string> pathsInBatch)
        {
            string path = op switch { SetPropertyOp s => s.Path, DeletePropertyOp d => d.Path, _ => "" };
            long ts = op switch { SetPropertyOp s => s.Timestamp, DeletePropertyOp d => d.Timestamp, _ => 0 };
            string peer = op switch { SetPropertyOp s => s.PeerId, DeletePropertyOp d => d.PeerId, _ => "" };

            if (registers.TryGetValue(path, out var existing))
            {
                if (!WinsLWW(ts, peer, op, existing.Timestamp, existing.PeerId, existing.Value, existing.IsDeleted, false))
                {
                    return;
                }
            }

            if (op is SetPropertyOp setOp)
            {
                registers[path] = new CrdtRegister(setOp.Value, ts, peer, false);
            }
            else if (op is DeletePropertyOp)
            {
                registers[path] = new CrdtRegister(CreateJsonNull(), ts, peer, true);
            }

            // HIERARCHICAL SEMANTICS: If we modify (Set or Delete) a parent object, 
            // we apply the tombstone recursively to its children if they lost in the LWW against the parent.
            string prefix = path + ".";
            foreach (var childKey in registers.Keys.Where(k => k.StartsWith(prefix)).ToList())
            {
                // If the child is part of the explicit operations of this compacted batch,
                // it means the user's intent was for it to survive the parent. We respect it.
                if (pathsInBatch.Contains(childKey)) continue;

                var child = registers[childKey];
                if (WinsLWW(ts, peer, op, child.Timestamp, child.PeerId, child.Value, child.IsDeleted, false))
                {
                    registers[childKey] = new CrdtRegister(CreateJsonNull(), ts, peer, true);
                }
            }
        }

        /// <summary>
        /// Determines if a new operation wins over an existing one using Last-Writer-Wins (LWW) rules.
        /// </summary>
        private bool WinsLWW(long newTs, string newPeer, JsonOp newOp, long exTs, string exPeer, JsonElement exVal, bool exIsDeleted, bool isCompactingSingleBatch)
        {
            if (newTs > exTs) return true;
            if (newTs < exTs) return false;

            int peerCmp = string.CompareOrdinal(newPeer, exPeer);
            if (peerCmp > 0) return true;
            if (peerCmp < 0) return false;

            // SAME PEER, SAME TS
            if (isCompactingSingleBatch) return true; // We respect sequential order within the same batch

            // DIFFERENT BATCHES: Deterministic tie-breaker to guarantee Strong Eventual Consistency (SEC)
            string newValStr = newOp is SetPropertyOp s ? s.Value.GetRawText() : "null";
            string exValStr = exIsDeleted ? "null" : exVal.GetRawText();
            return string.CompareOrdinal(newValStr, exValStr) > 0;
        }

        /// <summary>
        /// Transforms an incoming operation batch against an existing one.
        /// For LWW-based CRDTs, transformation is usually an identity function as operations commute.
        /// </summary>
        public JsonOpBatch? Transform(JsonOpBatch incoming, JsonOpBatch existing, TransformPriority priority)
        {
            return incoming;
        }

        /// <summary>
        /// Composes two operation batches into a single compacted batch.
        /// </summary>
        /// <param name="a">The first operation batch.</param>
        /// <param name="b">The second operation batch.</param>
        /// <returns>A single compacted operation batch.</returns>
        public JsonOpBatch? Compose(JsonOpBatch a, JsonOpBatch b)
        {
            bool isCompacting = a.Operations.Count == 0;
            var allOps = a.Operations.Concat(b.Operations);
            var compacted = new Dictionary<string, JsonOp>();

            foreach (var op in allOps)
            {
                string path = op switch { SetPropertyOp s => s.Path, DeletePropertyOp d => d.Path, _ => "" };
                long ts = op switch { SetPropertyOp s => s.Timestamp, DeletePropertyOp d => d.Timestamp, _ => 0 };
                string peer = op switch { SetPropertyOp s => s.PeerId, DeletePropertyOp d => d.PeerId, _ => "" };

                if (compacted.TryGetValue(path, out var existing))
                {
                    long exTs = existing switch { SetPropertyOp s => s.Timestamp, DeletePropertyOp d => d.Timestamp, _ => 0 };
                    string exPeer = existing switch { SetPropertyOp s => s.PeerId, DeletePropertyOp d => d.PeerId, _ => "" };
                    JsonElement exVal = existing is SetPropertyOp s2 ? s2.Value : CreateJsonNull();
                    bool exIsDeleted = existing is DeletePropertyOp;

                    if (!WinsLWW(ts, peer, op, exTs, exPeer, exVal, exIsDeleted, isCompacting))
                    {
                        continue;
                    }
                }
                
                compacted[path] = op;

                // HIERARCHICAL SEMANTICS: If we modify (Set or Delete) a parent object, 
                // we remove pending children that have lost in the LWW.
                string prefix = path + ".";
                var childrenPaths = compacted.Keys.Where(k => k.StartsWith(prefix)).ToList();
                foreach (var childPath in childrenPaths)
                {
                    var childOp = compacted[childPath];
                    long cTs = childOp switch { SetPropertyOp s => s.Timestamp, DeletePropertyOp d => d.Timestamp, _ => 0 };
                    string cPeer = childOp switch { SetPropertyOp s => s.PeerId, DeletePropertyOp d => d.PeerId, _ => "" };
                    JsonElement cVal = childOp is SetPropertyOp s3 ? s3.Value : CreateJsonNull();
                    bool cIsDel = childOp is DeletePropertyOp;
                    
                    if (WinsLWW(ts, peer, op, cTs, cPeer, cVal, cIsDel, isCompacting))
                    {
                        // BUG FIX: Protection for explicit children in same-ts/same-peer batch.
                        // This ensures sequential user intent (e.g. set parent then set child) survives compaction.
                        if (isCompacting && ts == cTs && peer == cPeer) continue;

                        compacted.Remove(childPath);
                    }
                }
            }

            return new JsonOpBatch(compacted.Values.ToList());
        }

        /// <summary>
        /// Generates an inverse operation batch for the given operation batch.
        /// </summary>
        /// <param name="opBatch">The operation batch to invert.</param>
        /// <param name="preState">The state of the document before the operations were applied.</param>
        /// <returns>The inverse operation batch.</returns>
        public JsonOpBatch Invert(JsonOpBatch opBatch, Json_Document preState)
        {
            var effectiveBatch = Compose(JsonOpBatch.Create(), opBatch);
            var invertedDict = new Dictionary<string, JsonOp>();

            foreach (var op in effectiveBatch!.Operations)
            {
                string path = op switch { SetPropertyOp s => s.Path, DeletePropertyOp d => d.Path, _ => throw new NotSupportedException() };
                long opTs = op switch { SetPropertyOp s => s.Timestamp, DeletePropertyOp d => d.Timestamp, _ => 0 };
                string peerId = op switch { SetPropertyOp s => s.PeerId, DeletePropertyOp d => d.PeerId, _ => "system-undo" };

                if (preState.Registers.TryGetValue(path, out var previousRegister))
                {
                    if (!WinsLWW(opTs, peerId, op, previousRegister.Timestamp, previousRegister.PeerId, previousRegister.Value, previousRegister.IsDeleted, false))
                    {
                        continue;
                    }

                    // BUG FIX: Don't jump to UtcNow here. Use the minimum timestamp required to win 
                    // over the operation being inverted. This ensures hierarchical consistency
                    // without killing concurrent writes that happened after the original operation.
                    long safeTimestamp = Math.Max(previousRegister.Timestamp, opTs) + 1;

                    if (previousRegister.IsDeleted)
                        invertedDict[path] = new DeletePropertyOp(path, safeTimestamp, peerId);
                    else
                        invertedDict[path] = new SetPropertyOp(path, previousRegister.Value, safeTimestamp, peerId);
                }
                else
                {
                    long safeTimestamp = opTs + 1;
                    invertedDict[path] = new DeletePropertyOp(path, safeTimestamp, peerId);
                }

                // HIERARCHICAL SEMANTICS: If the operation modified (Set or Delete) a parent object,
                // we must check in the previous state if the operation implicitly killed any children.
                // If so, we must add an inverse operation to resurrect the child with its previous value.
                string prefix = path + ".";
                foreach (var childKvp in preState.Registers.Where(k => k.Key.StartsWith(prefix) && !k.Value.IsDeleted))
                {
                    string childPath = childKvp.Key;
                    var childReg = childKvp.Value;
                    
                    if (WinsLWW(opTs, peerId, op, childReg.Timestamp, childReg.PeerId, childReg.Value, childReg.IsDeleted, false))
                    {
                        if (!invertedDict.ContainsKey(childPath))
                        {
                            long childSafeTs = Math.Max(childReg.Timestamp, opTs) + 1;
                            invertedDict[childPath] = new SetPropertyOp(childPath, childReg.Value, childSafeTs, peerId);
                        }
                    }
                }
            }

            return new JsonOpBatch(invertedDict.Values.ToList());
        }

        /// <summary>
        /// Determines whether an operation batch is a no-op (contains no operations).
        /// </summary>
        public bool IsNoOp(JsonOpBatch op) => op.Operations.Count == 0;

        /// <summary>
        /// Rewrites every op's <c>Timestamp</c> to <c>max(maxRegisterTimestamp, now) + 1</c>
        /// so a cached inverse beats any concurrent LWW winner that landed since record time.
        /// Required for <c>UndoRedoEngine</c> to produce visible undoes under heavy concurrency.
        /// </summary>
        public JsonOpBatch RestampToWin(JsonOpBatch op, Json_Document currentState)
        {
            if (op.Operations.Count == 0) return op;

            long maxExisting = 0;
            foreach (var reg in currentState.Registers.Values)
            {
                if (reg.Timestamp > maxExisting) maxExisting = reg.Timestamp;
            }

            // Also consider timestamps already present in the operation batch to avoid demoting them
            foreach (var jop in op.Operations)
            {
                long opTs = jop switch { SetPropertyOp s => s.Timestamp, DeletePropertyOp d => d.Timestamp, _ => 0 };
                if (opTs > maxExisting) maxExisting = opTs;
            }

            long newTs = Math.Max(maxExisting, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + 1;

            var rewritten = new List<JsonOp>(op.Operations.Count);
            foreach (var jop in op.Operations)
            {
                rewritten.Add(jop switch
                {
                    SetPropertyOp s => s with { Timestamp = newTs },
                    DeletePropertyOp d => d with { Timestamp = newTs },
                    _ => jop
                });
            }
            return new JsonOpBatch(rewritten);
        }

        /// <summary>
        /// Creates a <see cref="JsonElement"/> representing null.
        /// </summary>
        private static JsonElement CreateJsonNull()
        {
            using var doc = JsonDocument.Parse("null");
            return doc.RootElement.Clone();
        }
    }
}
