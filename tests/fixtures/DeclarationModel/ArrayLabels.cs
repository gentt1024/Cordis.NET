namespace IndependentDeclarations;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ArrayLabelsAttribute(string name, string[]? labels) : Attribute
{
    public string Name
    {
        get;
    } = name;

    public string[]? Labels
    {
        get;
    } = labels;

    public Type[]? Types
    {
        get;
        set;
    }
}
