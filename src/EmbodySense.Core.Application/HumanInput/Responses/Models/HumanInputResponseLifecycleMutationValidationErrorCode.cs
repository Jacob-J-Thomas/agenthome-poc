namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Identifies one deterministic response-command validation failure.</summary>
public enum HumanInputResponseLifecycleMutationValidationErrorCode
{
    /// <summary>No supported validation code was supplied.</summary>
    Unknown = 0,
    /// <summary>A command is required.</summary>
    CommandRequired = 1,
    /// <summary>The command schema version is unsupported.</summary>
    UnsupportedSchemaVersion = 2,
    /// <summary>A stable identity is invalid.</summary>
    InvalidIdentifier = 3,
    /// <summary>The operation kind is unsupported.</summary>
    InvalidOperationKind = 4,
    /// <summary>The expected request lifecycle state is invalid.</summary>
    InvalidExpectedState = 5,
    /// <summary>The exact request reference is malformed or mismatched.</summary>
    InvalidRequestReference = 6,
    /// <summary>The exact request binding is malformed.</summary>
    InvalidBinding = 7,
    /// <summary>The operation carries an impossible field shape.</summary>
    InvalidOperationShape = 8,
    /// <summary>The submitted response value exceeds a structural command-envelope bound.</summary>
    UnboundedResponseValue = 9,
    /// <summary>The canonical exact-intent hash is absent or mismatched.</summary>
    InvalidCommandHash = 10,
}
