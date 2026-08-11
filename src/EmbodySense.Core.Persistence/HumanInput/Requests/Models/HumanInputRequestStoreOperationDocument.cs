using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Requests.Models;

/// <summary>Retains exactly one typed operation in the workspace-global Human Input chronology.</summary>
internal sealed partial record HumanInputRequestStoreOperationDocument(
    int SchemaVersion,
    string OperationId,
    HumanInputRequestStoreOperationFamily Family,
    HumanInputRequestLifecycleOperationEvidence? RequestLifecycle,
    HumanInputResponseOperationEvidence? ResponseLifecycle)
{
    internal const int CurrentSchemaVersion = 1;
}
