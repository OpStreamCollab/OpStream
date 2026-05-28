using System.Text.Json.Serialization;

namespace OpStream.Server.Engine.RichText
{
    /// <summary>
    /// Represents formatting attributes for a block of text. 
    /// Null values signify the removal of the corresponding format.
    /// </summary>
    public class TextAttributes : SortedDictionary<string, object?>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TextAttributes"/> class.
        /// </summary>
        public TextAttributes() : base() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="TextAttributes"/> class with existing attributes.
        /// </summary>
        public TextAttributes(IDictionary<string, object?> dictionary) : base(dictionary) { }
    }

    /// <summary>
    /// Base class for rich text operation components.
    /// </summary>
    [JsonDerivedType(typeof(Insert), "insert")]
    [JsonDerivedType(typeof(Retain), "retain")]
    [JsonDerivedType(typeof(Delete), "delete")]
    public abstract record RichTextComponent;

    /// <summary>
    /// Inserts text with optional formatting.
    /// </summary>
    public record Insert(string Text, TextAttributes? Attributes = null) : RichTextComponent;

    /// <summary>
    /// Retains a specified number of characters, optionally applying new formatting.
    /// </summary>
    public record Retain(int Count, TextAttributes? Attributes = null) : RichTextComponent;

    /// <summary>
    /// Deletes a specified number of characters.
    /// </summary>
    public record Delete(int Count) : RichTextComponent;

    /// <summary>
    /// Represents a complete mutation on a rich text document.
    /// </summary>
    public record RichTextOp(IReadOnlyList<RichTextComponent> Components)
    {
        /// <summary>
        /// Helper for fluent initialization of a rich text operation.
        /// </summary>
        public static RichTextOp Create(params RichTextComponent[] components) => new(components);
    }

    /// <summary>
    /// Represents the state of a rich text document as a sequence of formatted text segments.
    /// </summary>
    public record RichTextDocument(IReadOnlyList<Insert> Content)
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RichTextDocument"/> class with empty content.
        /// </summary>
        public RichTextDocument() : this(new List<Insert>()) { }
    }
}
