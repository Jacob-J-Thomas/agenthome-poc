using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Validates value-free provider-port requests and structured outcomes.</summary>
public static class CredentialPortContractValidator
{
    /// <summary>Validates a provider create or replace request.</summary>
    public static CredentialContractValidationResult Validate(CredentialProviderMutationRequest? request)
    {
        var identity = ValidateIdentity(request?.WorkspaceId, request?.ReferenceId, request?.ProviderId, request?.OperationId);
        if (!identity.IsValid)
        {
            return identity;
        }

        return request!.ValueByteLength is > 0 and <= CredentialContractLimits.MaxCredentialBytes ? CredentialContractValidationResult.Valid : CredentialContractValidationResult.Rejected(CredentialContractErrorCode.InvalidCredentialLength);
    }

    /// <summary>Validates a provider callback request.</summary>
    public static CredentialContractValidationResult Validate(CredentialProviderUseRequest? request)
    {
        return ValidateIdentity(request?.WorkspaceId, request?.ReferenceId, request?.ProviderId, request?.OperationId);
    }

    /// <summary>Validates a provider delete request.</summary>
    public static CredentialContractValidationResult Validate(CredentialProviderDeleteRequest? request)
    {
        return ValidateIdentity(request?.WorkspaceId, request?.ReferenceId, request?.ProviderId, request?.OperationId);
    }

    /// <summary>Validates that a provider result contains exactly one success or failure posture.</summary>
    public static CredentialContractValidationResult Validate(CredentialProviderResult? result)
    {
        var valid = result is not null && result.Succeeded == (result.Failure is null) && (result.Failure is null || IsFailureValid(result.Failure));
        return valid ? CredentialContractValidationResult.Valid : CredentialContractValidationResult.Rejected(CredentialContractErrorCode.InvalidProviderResult);
    }

    /// <summary>Validates a bounded value-free failure.</summary>
    public static bool IsFailureValid(CredentialFailure? failure)
    {
        return failure is not null && Enum.IsDefined(failure.Code);
    }

    private static CredentialContractValidationResult ValidateIdentity(string? workspaceId, CredentialReferenceId? referenceId, CredentialProviderId? providerId, CredentialContractId? operationId)
    {
        if (referenceId is null || providerId is null || operationId is null)
        {
            return CredentialContractValidationResult.Rejected(CredentialContractErrorCode.InvalidProviderRequestIdentity);
        }

        return CredentialContractValidator.Validate(new CredentialScope(workspaceId, null, null, null, null, null, null, null, null, null, null, null, null));
    }
}
