namespace EmbodySense.Core.Common.Governance.Audit;

/// <summary>
/// Defines the versioned audit schema and its canonical vocabulary.
/// </summary>
public static partial class AuditSchema
{
    /// <summary>
    /// Defines canonical audit actor identifiers.
    /// </summary>
    public static class Actors
    {
        /// <summary>
        /// Identifies the CLI audit actor.
        /// </summary>
        public const string Cli = "embodysense.cli";

        /// <summary>
        /// Identifies the web audit actor.
        /// </summary>
        public const string Web = "embodysense.web";

        /// <summary>
        /// Identifies the LLM audit actor.
        /// </summary>
        public const string Llm = "embodysense.llm";

        /// <summary>
        /// Identifies the tool audit actor.
        /// </summary>
        public const string Tool = "embodysense.tool";
    }
}
