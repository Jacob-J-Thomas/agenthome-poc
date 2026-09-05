using Xunit.Abstractions;
using Xunit.Sdk;

namespace EmbodySense.Core.Startup.Tests;

internal sealed class TestCollectionStub : LongLivedMarshalByRefObject, ITestCollection
{
    public TestCollectionStub()
        : this(string.Empty)
    {
    }

    public TestCollectionStub(string displayName)
    {
        DisplayName = displayName;
    }

    public ITypeInfo CollectionDefinition => null!;

    public string DisplayName { get; }

    public ITestAssembly TestAssembly => null!;

    public Guid UniqueID { get; } = Guid.NewGuid();

    public void Deserialize(IXunitSerializationInfo _)
    {
    }

    public void Serialize(IXunitSerializationInfo _)
    {
    }
}
