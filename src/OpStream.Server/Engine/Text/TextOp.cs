using System.Text.Json.Serialization;

namespace OpStream.Server.Engine.Text;

// The 3 base operation types
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Retain), typeDiscriminator: "retain")]
[JsonDerivedType(typeof(Insert), typeDiscriminator: "insert")]
[JsonDerivedType(typeof(Delete), typeDiscriminator: "delete")]
public abstract record TextOpComponent;
public record Retain(int Count) : TextOpComponent;
public record Insert(string Text) : TextOpComponent;
public record Delete(int Count) : TextOpComponent;

/// <summary>
/// Represents a complete mutation on a text document.
/// </summary>
public record TextOp(IReadOnlyList<TextOpComponent> Components)
{
    // Helper for fluent initialization in tests
    public static TextOp Create(params TextOpComponent[] components) => new(components);
}

/// <summary>
/// The state of our text document.
/// </summary>
public record TextDocument(string Content);
