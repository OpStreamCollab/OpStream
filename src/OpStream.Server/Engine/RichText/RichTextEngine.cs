using OpStream.Shared.Messages;
using System.Text.Json;

namespace OpStream.Server.Engine.RichText
{
    /// <summary>
    /// Implements the Operational Transformation (OT) engine for rich text documents,
    /// supporting formatting attributes along with text insertions and deletions.
    /// </summary>
    public class RichTextEngine : IOpEngine<RichTextDocument, RichTextOp>
    {
        /// <summary>
        /// Applies a rich text operation to the given document state.
        /// </summary>
        /// <param name="state">The current rich text document state.</param>
        /// <param name="op">The operation to apply.</param>
        /// <returns>A new rich text document state after the operation is applied.</returns>
        public RichTextDocument Apply(RichTextDocument state, RichTextOp op)
        {
            var iterator = new DocumentIterator(state.Content);
            var builder = new DocumentBuilder();

            foreach (var component in op.Components)
            {
                if (component is Insert insert)
                {
                    // New insertions are added as is, but cleaning up null attributes
                    var cleanAttrs = MergeAttributes(null, insert.Attributes);
                    builder.Push(insert.Text, cleanAttrs);
                }
                else if (component is Retain retain)
                {
                    int length = retain.Count;
                    while (length > 0 && iterator.HasNext())
                    {
                        // Extract a "chunk" from the original document
                        var chunk = iterator.Next(length);

                        // Merge original attributes with new ones from the Retain
                        var mergedAttributes = MergeAttributes(chunk.Attributes, retain.Attributes);

                        builder.Push(chunk.Text, mergedAttributes);
                        length -= chunk.Text.Length;
                    }
                }
                else if (component is Delete delete)
                {
                    int length = delete.Count;
                    while (length > 0 && iterator.HasNext())
                    {
                        // By calling "Next", we advance the iterator but DO NOT send it to the builder.
                        // Effectively, this deletes the text.
                        var chunk = iterator.Next(length);
                        length -= chunk.Text.Length;
                    }
                }
            }

            // After the operation is finished, copy any remaining text from the document
            while (iterator.HasNext())
            {
                var chunk = iterator.Next(int.MaxValue);
                builder.Push(chunk.Text, chunk.Attributes);
            }

            return new RichTextDocument(builder.Build());
            }

            /// <summary>
            /// Generates the inverse operation for the given rich text operation.
            /// </summary>
            /// <param name="op">The operation to invert.</param>
            /// <param name="preState">The state of the document before the operation was applied.</param>
            /// <returns>The inverse operation.</returns>
            public RichTextOp Invert(RichTextOp op, RichTextDocument preState)
            {
            var invertedComponents = new List<RichTextComponent>();

            // Iterate through the original document to know what we are deleting or rewriting
            var baseIter = new DocumentIterator(preState.Content);

            foreach (var component in op.Components)
            {
                if (component is Insert insert)
                {
                    // The inverse of Insert is Delete exactly the same length.
                    // (Insert doesn't advance the base document iterator because that text didn't exist before)
                    invertedComponents.Add(new Delete(insert.Text.Length));
                }
                else if (component is Retain retain)
                {
                    // If Retain has NO attributes, it simply advances the cursor (nothing changes).
                    // Therefore, its inverse is another harmless Retain.
                    if (retain.Attributes == null)
                    {
                        invertedComponents.Add(new Retain(retain.Count));

                        // But we must advance the base iterator to synchronize
                        var _ = baseIter.Next(retain.Count);
                        continue;
                    }

                    // If Retain DOES have attributes (e.g., {"bold": true}), we must invert the format.
                    // We need to see what format the document originally had.
                    int lengthToInvert = retain.Count;
                    while (lengthToInvert > 0 && baseIter.HasNext())
                    {
                        var baseChunk = baseIter.Next(lengthToInvert);
                        lengthToInvert -= baseChunk.Text.Length;

                        // Calculate inverse attributes
                        var invertedAttrs = InvertAttributes(retain.Attributes, baseChunk.Attributes);
                        invertedComponents.Add(new Retain(baseChunk.Text.Length, invertedAttrs));
                    }
                    if (lengthToInvert > 0)
                    {
                        var invertedAttrs = InvertAttributes(retain.Attributes, null);
                        invertedComponents.Add(new Retain(lengthToInvert, invertedAttrs));
                    }
                }
                else if (component is Delete delete)
                {
                    // The inverse of Delete is to Re-Insert EXACTLY what was there.
                    int lengthToRestore = delete.Count;
                    while (lengthToRestore > 0 && baseIter.HasNext())
                    {
                        var baseChunk = baseIter.Next(lengthToRestore);
                        lengthToRestore -= baseChunk.Text.Length;

                        // Re-insert text with its original attributes intact
                        invertedComponents.Add(new Insert(baseChunk.Text, baseChunk.Attributes));
                    }
                }
            }

            return Compact(invertedComponents) ?? new RichTextOp(new List<RichTextComponent>());
            }


            /// <summary>
            /// Calculates exactly what format to send to return a block of text to its original state.
            /// </summary>
            private static TextAttributes? InvertAttributes(TextAttributes appliedAttrs, TextAttributes? originalAttrs)
            {
            var inverted = new TextAttributes();

            foreach (var key in appliedAttrs.Keys)
            {
                // If the original document had this format (e.g., it was already italic)
                // the inverse must restore that original value.
                if (originalAttrs != null && originalAttrs.TryGetValue(key, out var originalValue))
                {
                    inverted[key] = originalValue;
                }
                else
                {
                    // If the original document DID NOT have this format,
                    // the inverse must remove it (set to null).
                    inverted[key] = null;
                }
            }

            return inverted.Count > 0 ? inverted : null;
            }

            /// <summary>
            /// Transforms an incoming operation against an existing operation to ensure convergence.
            /// </summary>
            /// <param name="incoming">The operation coming from a client.</param>
            /// <param name="existing">The operation already applied on the server.</param>
            /// <param name="priority">Specifies which operation takes priority in case of conflict.</param>
            /// <returns>The transformed operation, or null if it becomes a no-op.</returns>
            public RichTextOp? Transform(RichTextOp incoming, RichTextOp existing, TransformPriority priority)
            {
            var incomingIter = new OpIterator(incoming.Components);
            var existingIter = new OpIterator(existing.Components);
            var result = new List<RichTextComponent>();

            while (incomingIter.HasNext() || existingIter.HasNext())
            {
                bool incomingIsInsert = incomingIter.PeekType() == typeof(Insert);
                bool existingIsInsert = existingIter.PeekType() == typeof(Insert);

                if (incomingIsInsert && existingIsInsert)
                {
                    if (priority == TransformPriority.IncomingWins)
                    {
                        result.Add(incomingIter.Next());
                    }
                    else
                    {
                        var existingInsert = (Insert)existingIter.Next();
                        result.Add(new Retain(existingInsert.Text.Length));
                    }
                    continue;
                }

                if (existingIsInsert)
                {
                    var existingInsert = (Insert)existingIter.Next();
                    result.Add(new Retain(existingInsert.Text.Length));
                    continue;
                }

                if (incomingIsInsert)
                {
                    result.Add(incomingIter.Next());
                    continue;
                }

                // From here on, both are Retain or Delete
                if (!incomingIter.HasNext())
                {
                    break;
                }
                if (!existingIter.HasNext())
                {
                    result.Add(incomingIter.Next());
                    continue;
                }

                // Calculate the size of the fragment we can process at once
                var incomingLen = incomingIter.PeekLength();
                var existingLen = existingIter.PeekLength();
                var length = Math.Min(incomingLen, existingLen);

                var incChunk = incomingIter.Next(length);
                var exChunk = existingIter.Next(length);

                // Resolution matrix
                if (incChunk is Delete)
                {
                    // If Alice deleted, keep the Delete...
                    // ...EXCEPT if Bob also deleted it (Delete against Delete = already deleted).
                    if (exChunk is not Delete)
                    {
                        result.Add(new Delete(length));
                    }
                }
                else if (incChunk is Retain incRetain)
                {
                    // If Bob deleted, Alice's Retain disappears (cannot retain what no longer exists)
                    if (exChunk is Delete)
                    {
                        // Do nothing (equivalent to ignoring)
                    }
                    else if (exChunk is Retain exRetain)
                    {
                        // FORMAT CONFLICT: Alice Retain vs Bob Retain
                        // If Alice sent attributes, evaluate them against Bob's
                        if (incRetain.Attributes != null)
                        {
                            var finalAttrs = TransformAttributes(incRetain.Attributes, exRetain.Attributes, priority);
                            result.Add(new Retain(length, finalAttrs));
                        }
                        else
                        {
                            result.Add(new Retain(length));
                        }
                    }
                }
            }

            return Compact(result);
            }
        /// <summary>
        /// Transforms attributes in case of concurrent formatting updates.
        /// </summary>
        private static TextAttributes? TransformAttributes(TextAttributes? incoming, TextAttributes? existing, TransformPriority priority)
        {
            if (incoming == null) return null;
            if (existing == null) return incoming;

            // Alice and Bob both changed formatting. 
            // If priority == ExistingWins, Bob wins in case of direct conflict.
            var result = new TextAttributes();
            foreach (var kvp in incoming)
            {
                if (existing.ContainsKey(kvp.Key) && priority == TransformPriority.ExistingWins)
                {
                    // Bob also modified this formatting. Bob wins, ignore Alice.
                    continue;
                }
                result[kvp.Key] = kvp.Value;
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// Composes two operations into a single operation.
        /// </summary>
        /// <param name="a">The first operation.</param>
        /// <param name="b">The second operation.</param>
        /// <returns>A composed operation that has the same effect as applying a then b.</returns>
        public RichTextOp? Compose(RichTextOp a, RichTextOp b)
        {
            var iterA = new OpIterator(a.Components);
            var iterB = new OpIterator(b.Components);
            var result = new List<RichTextComponent>();

            while (iterA.HasNext() || iterB.HasNext())
            {
                if (iterB.PeekType() == typeof(Insert))
                {
                    result.Add(iterB.Next());
                }
                else if (iterA.PeekType() == typeof(Delete))
                {
                    result.Add(iterA.Next());
                }
                else if (!iterA.HasNext())
                {
                    result.Add(iterB.Next());
                }
                else if (!iterB.HasNext())
                {
                    result.Add(iterA.Next());
                }
                else
                {
                    int lenA = iterA.PeekLength();
                    int lenB = iterB.PeekLength();

                    if (lenA == 0 && iterA.PeekType() == typeof(Insert))
                    {
                        result.Add(iterA.Next());
                        continue;
                    }

                    int length = Math.Min(lenA, lenB);
                    if (length <= 0)
                    {
                        if (lenA == 0) iterA.Next();
                        if (lenB == 0) iterB.Next();
                        continue;
                    }

                    var opA = iterA.Next(length);
                    var opB = iterB.Next(length);

                    if (opA is Insert insA)
                    {
                        if (opB is Retain retB)
                        {
                            result.Add(new Insert(insA.Text, MergeAttributes(insA.Attributes, retB.Attributes)));
                        }
                        // Alice inserts and Bob deletes -> they cancel out.
                    }
                    else if (opA is Retain retA)
                    {
                        if (opB is Retain retB)
                        {
                            result.Add(new Retain(length, MergeAttributes(retA.Attributes, retB.Attributes)));
                        }
                        else if (opB is Delete)
                        {
                            result.Add(new Delete(length));
                        }
                    }
                }
            }

            return Compact(result);
        }

        /// <summary>
        /// Compacts sequential components of the same type into a single component.
        /// </summary>
        private static RichTextOp? Compact(List<RichTextComponent> components)
        {
            var compacted = new List<RichTextComponent>();

            foreach (var component in components)
            {
                if (compacted.Count == 0)
                {
                    compacted.Add(component);
                    continue;
                }

                var last = compacted[^1];

                if (last is Insert lastIns && component is Insert currIns && AttributesEqual(lastIns.Attributes, currIns.Attributes))
                {
                    compacted[^1] = new Insert(lastIns.Text + currIns.Text, lastIns.Attributes);
                }
                else if (last is Retain lastRet && component is Retain currRet && AttributesEqual(lastRet.Attributes, currRet.Attributes))
                {
                    compacted[^1] = new Retain(lastRet.Count + currRet.Count, lastRet.Attributes);
                }
                else if (last is Delete lastDel && component is Delete currDel)
                {
                    compacted[^1] = new Delete(lastDel.Count + currDel.Count);
                }
                else
                {
                    compacted.Add(component);
                }
            }

            return compacted.Count > 0 ? new RichTextOp(compacted) : null;
        }

        /// <summary>
        /// Compares two sets of attributes for equality.
        /// </summary>
        internal static bool AttributesEqual(TextAttributes? a, TextAttributes? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;

            foreach (var kvp in a)
            {
                if (!b.TryGetValue(kvp.Key, out var bValue)) return false;

                if (!object.Equals(kvp.Value, bValue))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Determines whether an operation is a no-op (has no effect).
        /// </summary>
        public bool IsNoOp(RichTextOp op) => op.Components.Count == 0 || op.Components.All(c => 
            (c is Retain r && r.Attributes == null) ||
            (c is Insert i && i.Text.Length == 0) ||
            (c is Delete d && d.Count == 0));

        /// <summary>
        /// Merges a base set of attributes with a new set of attributes.
        /// </summary>
        /// <param name="baseAttrs">The base attributes.</param>
        /// <param name="newAttrs">The new attributes to apply. Null values in newAttrs indicate removal of the attribute.</param>
        /// <returns>A new set of merged attributes, or null if none remain.</returns>
        public static TextAttributes? MergeAttributes(TextAttributes? baseAttrs, TextAttributes? newAttrs)
        {
            if (baseAttrs == null && newAttrs == null) return null;

            var result = new TextAttributes();

            if (baseAttrs != null)
            {
                foreach (var kvp in baseAttrs)
                {
                    if (kvp.Value != null)
                        result[kvp.Key] = kvp.Value;
                }
            }

            if (newAttrs != null)
            {
                foreach (var kvp in newAttrs)
                {
                    if (kvp.Value == null) // null means "remove this formatting"
                        result.Remove(kvp.Key);
                    else
                        result[kvp.Key] = kvp.Value;
                }
            }

            return result.Count > 0 ? result : null;
        }
    }

    /// <summary>
    /// Helper class to iterate over rich text document content.
    /// </summary>
    internal class DocumentIterator
    {
        private readonly IReadOnlyList<Insert> _ops;
        private int _index = 0;
        private int _offset = 0;

        public DocumentIterator(IReadOnlyList<Insert> ops) => _ops = ops;

        public bool HasNext() => _index < _ops.Count;

        public Insert Next(int length)
        {
            if (!HasNext()) return new Insert("");

            var currentOp = _ops[_index];
            int remainingInOp = currentOp.Text.Length - _offset;

            // If we ask for less text than there is in the current block, we split it
            if (length < remainingInOp)
            {
                string chunk = currentOp.Text.Substring(_offset, length);
                _offset += length;
                return new Insert(chunk, currentOp.Attributes);
            }

            // If we ask for exactly (or more) than there is, return the rest of the block and advance to the next
            string fullChunk = currentOp.Text.Substring(_offset);
            _index++;
            _offset = 0;
            return new Insert(fullChunk, currentOp.Attributes);
        }
    }

    /// <summary>
    /// Helper class to build a new rich text document state.
    /// Prevents fragmentation by merging sequential insertions with the same attributes.
    /// </summary>
    internal class DocumentBuilder
    {
        private readonly List<Insert> _ops = new();

        public void Push(string text, TextAttributes? attributes)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (_ops.Count > 0)
            {
                var lastOp = _ops[^1];
                // If the formatting is exactly the same, concatenate them
                if (RichTextEngine.AttributesEqual(lastOp.Attributes, attributes))
                {
                    _ops[^1] = new Insert(lastOp.Text + text, lastOp.Attributes);
                    return;
                }
            }
            _ops.Add(new Insert(text, attributes));
        }

        public List<Insert> Build() => _ops;
    }

    /// <summary>
    /// Helper class to iterate over rich text operation components.
    /// </summary>
    internal class OpIterator
    {
        private readonly IReadOnlyList<RichTextComponent> _ops;
        private int _index = 0;
        private int _offset = 0;

        public OpIterator(IReadOnlyList<RichTextComponent> ops) => _ops = ops;

        public bool HasNext() => _index < _ops.Count;

        public Type? PeekType() => HasNext() ? _ops[_index].GetType() : null;

        public int PeekLength()
        {
            if (!HasNext()) return 0;
            var op = _ops[_index];
            return op switch
            {
                Insert i => i.Text.Length - _offset,
                Retain r => r.Count - _offset,
                Delete d => d.Count - _offset,
                _ => 0
            };
        }

        public RichTextComponent Next(int? length = null)
        {
            if (!HasNext()) throw new InvalidOperationException("No more ops.");

            var op = _ops[_index];
            int opLength = PeekLength();
            int take = length.HasValue ? Math.Min(length.Value, opLength) : opLength;

            int currentOffset = _offset;
            _offset += take;

            int totalLen = op switch
            {
                Insert i => i.Text.Length,
                Retain r => r.Count,
                Delete d => d.Count,
                _ => 0
            };
            bool isDone = _offset >= totalLen;

            if (isDone)
            {
                _index++;
                _offset = 0;
            }

            return op switch
            {
                Insert i => new Insert(i.Text.Substring(currentOffset, take), i.Attributes),
                Retain r => new Retain(take, r.Attributes),
                Delete _ => new Delete(take),
                _ => throw new InvalidOperationException()
            };
        }
    }
}
