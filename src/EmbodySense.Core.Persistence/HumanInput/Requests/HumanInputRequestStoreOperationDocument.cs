using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Requests.Models;

internal sealed partial record HumanInputRequestStoreOperationDocument
{
    internal static HumanInputRequestStoreOperationDocument From(HumanInputRequestLifecycleOperationEvidence evidence)
        => new(CurrentSchemaVersion, evidence.OperationId, HumanInputRequestStoreOperationFamily.RequestLifecycle, evidence, null);

    internal static HumanInputRequestStoreOperationDocument From(HumanInputResponseOperationEvidence evidence)
        => new(CurrentSchemaVersion, evidence.OperationId, HumanInputRequestStoreOperationFamily.ResponseLifecycle, null, evidence);
}
