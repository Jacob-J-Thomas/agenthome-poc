using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

public interface IToolActuationAuthorityRevalidator
{
    Task<ToolActuationAuthorityRevalidation> RevalidateAsync(ToolRequest request, CancellationToken cancellationToken = default);
}
