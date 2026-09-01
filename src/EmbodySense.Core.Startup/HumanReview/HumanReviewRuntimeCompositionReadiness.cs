using EmbodySense.Core.Application.HumanReview;

namespace EmbodySense.Core.Startup.HumanReview;

/// <summary>Marks the complete Human Review handler graph as successfully constructed.</summary>
/// <remarks>
/// The marker is intentionally nonmutating. Its constructor captures every handler that the runtime exposes to the
/// recovery runner or facade, so aggregate readiness cannot be created while a required graph edge is still absent.
/// Construction itself is the proof for these handlers; the aggregate probe performs only the persistence and current
/// dependency reads that constructors cannot establish.
/// </remarks>
internal sealed class HumanReviewRuntimeCompositionReadiness
{
    private readonly HumanReviewAdmissionService _admission;
    private readonly HumanReviewContinuationPublicationService _publication;
    private readonly HumanReviewContinuationConsumer _continuationConsumer;
    private readonly HumanReviewContinuationRecoveryCoordinator _continuationRecovery;
    private readonly HumanReviewDecisionActionRecoveryCoordinator _decisionActionRecovery;
    private readonly HumanReviewDecisionService _decisionService;
    private readonly HumanReviewRuntimeFacade _facade;
    private readonly HumanReviewOrderedReleaseService _orderedRelease;

    internal HumanReviewRuntimeCompositionReadiness(
        HumanReviewAdmissionService admission,
        HumanReviewContinuationPublicationService publication,
        HumanReviewContinuationConsumer continuationConsumer,
        HumanReviewContinuationRecoveryCoordinator continuationRecovery,
        HumanReviewDecisionActionRecoveryCoordinator decisionActionRecovery,
        HumanReviewDecisionService decisionService,
        HumanReviewRuntimeFacade facade,
        HumanReviewOrderedReleaseService orderedRelease)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _publication = publication ?? throw new ArgumentNullException(nameof(publication));
        _continuationConsumer = continuationConsumer ?? throw new ArgumentNullException(nameof(continuationConsumer));
        _continuationRecovery = continuationRecovery ?? throw new ArgumentNullException(nameof(continuationRecovery));
        _decisionActionRecovery = decisionActionRecovery ?? throw new ArgumentNullException(nameof(decisionActionRecovery));
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _orderedRelease = orderedRelease ?? throw new ArgumentNullException(nameof(orderedRelease));
    }

    /// <summary>Gets whether every required handler reference remains present.</summary>
    internal bool IsComposed => _admission is not null
        && _publication is not null
        && _continuationConsumer is not null
        && _continuationRecovery is not null
        && _decisionActionRecovery is not null
        && _decisionService is not null
        && _facade is not null
        && _orderedRelease is not null;
}
