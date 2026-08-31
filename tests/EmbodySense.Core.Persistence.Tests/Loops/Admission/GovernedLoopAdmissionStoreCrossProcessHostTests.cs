using EmbodySense.Core.Persistence.Tests.Verification;
using static EmbodySense.Core.Persistence.Tests.Loops.Admission.GovernedLoopAdmissionStoreTestFixture;

namespace EmbodySense.Core.Persistence.Tests.Loops.Admission;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class GovernedLoopAdmissionStoreCrossProcessHostTests
{
    [Fact]
    public Task Cross_process_admission_store_host() => RunCrossProcessHostAsync();
}
