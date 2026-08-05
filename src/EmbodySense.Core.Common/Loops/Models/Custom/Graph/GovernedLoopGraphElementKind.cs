namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Identifies the exact schema-1 graph element attributed by a normalization or validation error.</summary>
public enum GovernedLoopGraphElementKind
{
    /// <summary>The graph document or one of its scalar properties.</summary>
    Graph = 1,
    /// <summary>A value-schema declaration.</summary>
    ValueSchema,
    /// <summary>A node declaration.</summary>
    Node,
    /// <summary>A node-local port declaration.</summary>
    Port,
    /// <summary>A control-flow edge.</summary>
    ControlEdge,
    /// <summary>A data or context binding.</summary>
    Binding,
    /// <summary>A declared graph output.</summary>
    Output,
    /// <summary>The current node catalog evidence.</summary>
    Catalog,
    /// <summary>The current role-authority evidence.</summary>
    Authority
}
