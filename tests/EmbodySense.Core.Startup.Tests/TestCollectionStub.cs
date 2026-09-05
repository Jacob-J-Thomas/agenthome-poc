using Xunit.Abstractions;
using Xunit.Sdk;

namespace EmbodySense.Core.Startup.Tests;

internal sealed class TestCollectionStub : LongLivedMarshalByRefObject, ITestCollection
{
    public TestCollectionStub()
        : this(string.Empty, Guid.Empty)
    {
    }

    public TestCollectionStub(string displayName, Guid uniqueID)
    {
        DisplayName = displayName;
        UniqueID = uniqueID;
    }

    public ITypeInfo CollectionDefinition => null!;

    public string DisplayName { get; }

    public ITestAssembly TestAssembly => null!;

    public Guid UniqueID { get; }

    public void Deserialize(IXunitSerializationInfo _)
    {
    }

    public void Serialize(IXunitSerializationInfo _)
    {
    }
}
