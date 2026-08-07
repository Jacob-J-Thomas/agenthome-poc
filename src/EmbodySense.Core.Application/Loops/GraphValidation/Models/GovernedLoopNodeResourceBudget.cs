namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Declares the fixed bounded resource envelope of one activation of an exact descriptor version.</summary>
/// <remarks>Graph admission prices every conservatively reachable activation. Inter-component control paths add their entry multiplicity, while a cyclic strongly connected component multiplies each entry by the product of its node-local iteration bounds.</remarks>
/// <param name="Attempts">Maximum attempts consumed by one node activation.</param>
/// <param name="PayloadCharacters">Maximum payload characters admitted by one node activation.</param>
/// <param name="EvidenceItems">Maximum evidence items retained by one node activation.</param>
/// <param name="ResourceUnits">Maximum catalog-defined abstract resource units consumed by one node activation.</param>
public sealed record GovernedLoopNodeResourceBudget(int Attempts, int PayloadCharacters, int EvidenceItems, int ResourceUnits);
