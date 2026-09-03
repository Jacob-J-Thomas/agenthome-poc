using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.E2ETests.Web;

internal sealed record EffectReconciliationBrowserSeed(
    string RunId,
    string CaseId,
    GovernedLoopEffectAttempt Attempt,
    GovernedLoopEffectReconciliationBinding Binding,
    string MarkerPath,
    string MarkerContent);
