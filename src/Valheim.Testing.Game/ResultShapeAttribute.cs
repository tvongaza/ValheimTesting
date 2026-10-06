namespace Valheim.Testing.Game;

/// <summary>
/// Marks a public type that describes a result and declares no operation (#292): what an operation returns or exposes
/// read-only, and the data types those hold. The public-API list labels it <c>[result shape]</c>; it is versioned with any
/// JSON it is written to, so a change to its shape is a change to that JSON's schema (#135). <c>scripts/api-docs.cs</c>
/// refuses an unlabelled data type an operation returns, a labelled type that declares an operation, and a public data
/// type no operation takes or returns.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
internal sealed class ResultShapeAttribute : Attribute;
