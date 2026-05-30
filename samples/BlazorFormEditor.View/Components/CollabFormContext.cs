namespace BlazorFormEditor.Components;

/// <summary>
/// Non-generic surface of <c>CollabForm&lt;TModel&gt;</c> cascaded to each
/// <c>CollabField</c>. Keeps the fields decoupled from the form's model type.
/// </summary>
public interface ICollabFormContext
{
    /// <summary>True once the document handshake has completed.</summary>
    bool Joined { get; }

    /// <summary>The remote peer currently editing <paramref name="fieldName"/>, or null.</summary>
    FieldEditor? EditorOf(string fieldName);

    /// <summary>Announce that the local user focused a field (acquires the soft lock).</summary>
    Task NotifyFocusAsync(string fieldName);

    /// <summary>Announce that the local user left a field (releases the soft lock).</summary>
    Task NotifyBlurAsync(string fieldName);

    /// <summary>Raised when awareness/locks change so fields can re-render.</summary>
    event Action? StateChanged;
}

/// <summary>Identity of the peer holding a field's soft lock.</summary>
public sealed record FieldEditor(string Name, string Color);
