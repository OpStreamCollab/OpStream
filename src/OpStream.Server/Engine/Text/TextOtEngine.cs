using OpStream.Shared.Messages;

namespace OpStream.Server.Engine.Text;

/// <summary>
/// Implements the Operational Transformation (OT) engine for text documents.
/// </summary>
public class TextOtEngine : IOpEngine<TextDocument, TextOp>
{
    /// <summary>
    /// Applies a text operation to the given document state.
    /// </summary>
    /// <param name="state">The current text document state.</param>
    /// <param name="op">The operation to apply.</param>
    /// <returns>A new text document state after the operation is applied.</returns>
    public TextDocument Apply(TextDocument state, TextOp op)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (op == null) throw new ArgumentNullException(nameof(op));
        if (op.Components == null) throw new ArgumentException("TextOp.Components cannot be null. Check JSON serialization casing.", nameof(op));

        var result = new System.Text.StringBuilder();
        var index = 0;

        foreach (var component in op.Components)
        {
            switch (component)
            {
                case Retain r:
                    if (index < state.Content.Length)
                    {
                        int retainCount = Math.Min(r.Count, state.Content.Length - index);
                        result.Append(state.Content.AsSpan(index, retainCount));
                    }
                    index += r.Count;
                    break;
                case Insert i:
                    result.Append(i.Text);
                    break;
                case Delete d:
                    index += d.Count;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown component type: {component.GetType()}");
            }
        }

        // Add any remaining text from the original document that wasn't explicitly covered by the operation
        if (index < state.Content.Length)
        {
            result.Append(state.Content.AsSpan(index));
        }

        return new TextDocument(result.ToString());
    }

    /// <summary>
    /// Transforms an incoming operation against an existing operation to ensure convergence.
    /// </summary>
    /// <param name="incoming">The operation coming from a client.</param>
    /// <param name="existing">The operation already applied on the server.</param>
    /// <param name="priority">Specifies which operation takes priority in case of conflict.</param>
    /// <returns>The transformed operation, or null if it becomes a no-op.</returns>
    public TextOp? Transform(TextOp incoming, TextOp existing, TransformPriority priority)
    {
        var result = new List<TextOpComponent>();
        int i = 0, j = 0;

        TextOpComponent? currentInc = incoming.Components.Count > 0 ? incoming.Components[0] : null;
        TextOpComponent? currentExt = existing.Components.Count > 0 ? existing.Components[0] : null;

        while (currentInc != null || currentExt != null)
        {
            if (currentInc is Insert incIns && currentExt is Insert extIns)
            {
                if (priority == TransformPriority.IncomingWins)
                {
                    result.Add(incIns);
                    i++;
                    currentInc = i < incoming.Components.Count ? incoming.Components[i] : null;
                }
                else
                {
                    result.Add(new Retain(extIns.Text.Length));
                    j++;
                    currentExt = j < existing.Components.Count ? existing.Components[j] : null;
                }
                continue;
            }

            if (currentExt is Insert extInsert)
            {
                result.Add(new Retain(extInsert.Text.Length));
                j++;
                currentExt = j < existing.Components.Count ? existing.Components[j] : null;
                continue;
            }

            if (currentInc is Insert incInsert)
            {
                result.Add(incInsert);
                i++;
                currentInc = i < incoming.Components.Count ? incoming.Components[i] : null;
                continue;
            }

            if (currentInc == null)
            {
                break; // Finished incoming
            }

            if (currentExt == null)
            {
                result.Add(currentInc);
                i++;
                currentInc = i < incoming.Components.Count ? incoming.Components[i] : null;
                continue;
            }

            // Both are Retain or Delete
            int lengthInc = GetLength(currentInc);
            int lengthExt = GetLength(currentExt);
            int length = Math.Min(lengthInc, lengthExt);

            if (currentInc is Retain && currentExt is Retain)
            {
                result.Add(new Retain(length));
            }
            else if (currentInc is Delete && currentExt is Retain)
            {
                result.Add(new Delete(length));
            }
            // If currentExt is Delete, it deletes the characters that currentInc is trying to Retain or Delete.
            // So we don't add anything to the result for currentInc's operation over this range.

            currentInc = SliceComponent(currentInc, length, ref i, incoming.Components);
            currentExt = SliceComponent(currentExt, length, ref j, existing.Components);
        }

        return Optimize(new TextOp(result));
    }


    /// <summary>
    /// Generates the inverse operation for the given operation.
    /// </summary>
    /// <param name="op">The operation to invert.</param>
    /// <param name="preState">The state of the document before the operation was applied.</param>
    /// <returns>The inverse operation.</returns>
    public TextOp Invert(TextOp op, TextDocument preState)
    {
        var invertedComponents = new List<TextOpComponent>();
        int currentOffset = 0; // Index in the original document (preState)

        foreach (var component in op.Components)
        {
            if (component is Retain retain)
            {
                invertedComponents.Add(new Retain(retain.Count));
                currentOffset += retain.Count;
            }
            else if (component is Insert insert)
            {
                // To undo an insertion, we simply delete the same amount of characters
                invertedComponents.Add(new Delete(insert.Text.Length));

                // Note: Insert does NOT advance the cursor in the original document, 
                // because the inserted text did not exist in preState.
            }
            else if (component is Delete delete)
            {
                // To undo a deletion, we must recover the text that was deleted.
                // This is why we need 'preState'.
                int availableToDelete = Math.Max(0, preState.Content.Length - currentOffset);
                int deleteCount = Math.Min(delete.Count, availableToDelete);
                if (deleteCount > 0)
                {
                    string deletedText = preState.Content.Substring(currentOffset, deleteCount);
                    invertedComponents.Add(new Insert(deletedText));
                }

                currentOffset += delete.Count;
            }
        }

        return Compact(new TextOp(invertedComponents));
    }

    /// <summary>
    /// Compacts sequential components of the same type into a single component.
    /// </summary>
    /// <param name="op">The operation to compact.</param>
    /// <returns>A compacted version of the operation.</returns>
    private TextOp Compact(TextOp op)
    {
        var result = new List<TextOpComponent>();
        foreach (var component in op.Components)
        {
            int length = GetLength(component);
            if (length == 0) continue;

            if (result.Count > 0)
            {
                var last = result[^1];
                if (last is Retain r1 && component is Retain r2)
                {
                    result[^1] = new Retain(r1.Count + r2.Count);
                    continue;
                }
                if (last is Delete d1 && component is Delete d2)
                {
                    result[^1] = new Delete(d1.Count + d2.Count);
                    continue;
                }
                if (last is Insert i1 && component is Insert i2)
                {
                    result[^1] = new Insert(i1.Text + i2.Text);
                    continue;
                }
            }
            result.Add(component);
        }
        return new TextOp(result);
    }

    /// <summary>
    /// Gets the length (count or text length) of an operation component.
    /// </summary>
    /// <param name="component">The component to measure.</param>
    /// <returns>The length of the component.</returns>
    private int GetLength(TextOpComponent component) => component switch
    {
        Retain r => r.Count,
        Insert i => i.Text.Length,
        Delete d => d.Count,
        _ => throw new InvalidOperationException()
    };

    /// <summary>
    /// Slices a component by the specified count and returns the remaining part or the next component in the list.
    /// </summary>
    private TextOpComponent? SliceComponent(TextOpComponent component, int count, ref int index, IReadOnlyList<TextOpComponent> list)
    {
        int length = GetLength(component);
        if (length == count)
        {
            index++;
            return index < list.Count ? list[index] : null;
        }

        return component switch
        {
            Retain r => new Retain(r.Count - count),
            Delete d => new Delete(d.Count - count),
            Insert i => new Insert(i.Text.Substring(count)),
            _ => throw new InvalidOperationException()
        };
    }

    /// <summary>
    /// Optimizes the operation by compacting it.
    /// </summary>
    /// <param name="op">The operation to optimize.</param>
    /// <returns>An optimized version of the operation.</returns>
    private TextOp Optimize(TextOp op) => Compact(op);

    /// <summary>
    /// Composes two operations into a single operation.
    /// </summary>
    /// <param name="a">The first operation.</param>
    /// <param name="b">The second operation.</param>
    /// <returns>A composed operation that has the same effect as applying a then b.</returns>
    public TextOp? Compose(TextOp a, TextOp b)
    {
        if (a == null) return b;
        if (b == null) return a;

        var result = new List<TextOpComponent>();
        var iterA = new OpIterator(a.Components);
        var iterB = new OpIterator(b.Components);

        while (iterA.HasNext() || iterB.HasNext())
        {
            if (iterA.PeekType() == typeof(Delete))
            {
                result.Add(iterA.Next());
                continue;
            }

            if (iterB.PeekType() == typeof(Insert))
            {
                result.Add(iterB.Next());
                continue;
            }

            if (!iterA.HasNext())
            {
                result.Add(iterB.Next());
                continue;
            }

            if (!iterB.HasNext())
            {
                result.Add(iterA.Next());
                continue;
            }

            var lenA = iterA.PeekLength();
            var lenB = iterB.PeekLength();
            var len = Math.Min(lenA, lenB);

            var opA = iterA.Next(len);
            var opB = iterB.Next(len);

            if (opA is Retain && opB is Retain)
            {
                result.Add(new Retain(len));
            }
            else if (opA is Insert i && opB is Retain)
            {
                result.Add(new Insert(i.Text));
            }
            else if (opA is Insert && opB is Delete)
            {
                // They cancel out
            }
            else if (opA is Retain && opB is Delete)
            {
                result.Add(new Delete(len));
            }
        }

        var composed = Compact(new TextOp(result));
        return IsNoOp(composed) ? null : composed;
    }

    /// <summary>
    /// Determines whether an operation is a no-op (has no effect).
    /// </summary>
    /// <param name="op">The operation to check.</param>
    /// <returns>True if the operation has no effect, false otherwise.</returns>
    public bool IsNoOp(TextOp op) 
    {
        return op == null || op.Components.Count == 0 || op.Components.All(c => c is Retain);
    }
}

/// <summary>
/// Helper class to iterate over text operation components and slice them as needed.
/// </summary>
internal class OpIterator
{
    private readonly IReadOnlyList<TextOpComponent> _ops;
    private int _index = 0;
    private int _offset = 0;

    public OpIterator(IReadOnlyList<TextOpComponent> ops) => _ops = ops;

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

    public TextOpComponent Next(int? length = null)
    {
        if (!HasNext()) throw new InvalidOperationException("No more ops.");

        var op = _ops[_index];
        int opLength = PeekLength();
        int take = length.HasValue ? Math.Min(length.Value, opLength) : opLength;

        int currentOffset = _offset;
        _offset += take;

        int totalLength = op switch
        {
            Insert i => i.Text.Length,
            Retain r => r.Count,
            Delete d => d.Count,
            _ => 0
        };

        if (_offset >= totalLength)
        {
            _index++;
            _offset = 0;
        }

        return op switch
        {
            Insert i => new Insert(i.Text.Substring(currentOffset, take)),
            Retain r => new Retain(take),
            Delete d => new Delete(take),
            _ => throw new InvalidOperationException()
        };
    }
}
