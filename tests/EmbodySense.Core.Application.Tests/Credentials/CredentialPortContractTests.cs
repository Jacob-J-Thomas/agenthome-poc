using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Tests.Credentials;

public sealed class CredentialPortContractTests
{
    [Fact]
    public async Task Secure_fake_provider_uses_callback_only_and_never_returns_material()
    {
        var provider = new SecureFakeProvider();
        var mutation = Mutation();
        var canary = Encoding.UTF8.GetBytes("credential-canary-214");
        var create = await provider.CreateAsync(mutation with { ValueByteLength = canary.Length }, destination => canary.CopyTo(destination), CancellationToken.None);
        var consumer = new RecordingConsumer();
        var use = await provider.UseAsync(Use(), consumer, CancellationToken.None);

        Assert.True(create.Succeeded);
        Assert.True(use.Succeeded);
        Assert.Equal(canary, consumer.Observed);
        Assert.DoesNotContain("credential-canary-214", create.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("credential-canary-214", use.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secure_fake_replace_is_atomic_and_callback_failures_are_value_free()
    {
        var provider = new SecureFakeProvider();
        var original = Encoding.UTF8.GetBytes("original-canary");
        var mutation = Mutation() with { ValueByteLength = original.Length };
        Assert.True((await provider.CreateAsync(mutation, destination => original.CopyTo(destination), CancellationToken.None)).Succeeded);

        var failed = await provider.ReplaceAsync(mutation, destination => throw new InvalidOperationException("hostile-provider-detail"), CancellationToken.None);
        var consumer = new RecordingConsumer();
        Assert.True((await provider.UseAsync(Use(), consumer, CancellationToken.None)).Succeeded);

        Assert.False(failed.Succeeded);
        Assert.Equal(CredentialFailureCode.CallbackFailed, failed.Failure?.Code);
        Assert.DoesNotContain("hostile-provider-detail", failed.ToString(), StringComparison.Ordinal);
        Assert.Equal(original, consumer.Observed);
    }

    [Fact]
    public async Task Secure_fake_fails_closed_for_bounds_missing_values_and_cancellation()
    {
        var provider = new SecureFakeProvider();
        var invalid = await provider.CreateAsync(Mutation() with { ValueByteLength = CredentialContractLimits.MaxCredentialBytes + 1 }, _ => { }, CancellationToken.None);
        var missing = await provider.UseAsync(Use(), new RecordingConsumer(), CancellationToken.None);
        using var source = new CancellationTokenSource();
        source.Cancel();
        var cancelled = await provider.DeleteAsync(new CredentialProviderDeleteRequest("workspace-1", Reference(), Provider(), Id("operation-delete")), source.Token);

        Assert.Equal(CredentialFailureCode.InvalidRequest, invalid.Failure?.Code);
        Assert.Equal(CredentialFailureCode.NotFound, missing.Failure?.Code);
        Assert.Equal(CredentialFailureCode.Unavailable, cancelled.Failure?.Code);
        Assert.True(CredentialPortContractValidator.Validate(CredentialProviderResult.Success()).IsValid);
        Assert.False(CredentialPortContractValidator.Validate((CredentialProviderResult?)null).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => CredentialFailure.FromCode((CredentialFailureCode)99));
        Assert.True(CredentialPortContractValidator.IsFailureValid(CredentialFailure.FromCode(CredentialFailureCode.Unavailable)));
    }

    [Fact]
    public void Public_ports_expose_no_general_read_secret_or_secret_return_type()
    {
        var portTypes = new[] { typeof(ICredentialBroker), typeof(ICredentialReferenceStore), typeof(ICredentialValueProvider), typeof(ICredentialAuthorityProofVerifier), typeof(ICredentialUseEvidenceSink) };
        var forbiddenReturnTypes = new[] { typeof(string), typeof(byte[]), typeof(Memory<byte>), typeof(ReadOnlyMemory<byte>) };

        foreach (var method in portTypes.SelectMany(type => type.GetMethods()))
        {
            Assert.DoesNotContain("ReadSecret", method.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GetSecret", method.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(method.ReturnType, forbiddenReturnTypes);
        }

        var consumerMethod = Assert.Single(typeof(ICredentialTrustedUseConsumer).GetMethods());
        Assert.Equal(typeof(void), consumerMethod.ReturnType);
        Assert.Equal(typeof(ReadOnlySpan<byte>), Assert.Single(consumerMethod.GetParameters()).ParameterType);
    }

    [Fact]
    public void Safe_result_factories_have_unambiguous_posture()
    {
        var failure = CredentialFailure.FromCode(CredentialFailureCode.Unavailable);
        var reference = new CredentialReference(1, Reference(), "api-token", CredentialLifecycleStatus.Active, "user-1", "purpose", Provider(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, new Dictionary<string, string>());
        Assert.True(CredentialReferenceLookupResult.Found(reference).Succeeded);
        Assert.False(CredentialReferenceLookupResult.Failed(failure).Succeeded);
        Assert.True(CredentialAuthorityVerificationResult.Accept().Accepted);
        Assert.False(CredentialAuthorityVerificationResult.Reject(failure).Accepted);
        Assert.True(CredentialEvidenceWriteResult.Success().Succeeded);
        Assert.False(CredentialEvidenceWriteResult.Failed(failure).Succeeded);
        Assert.Equal(CredentialProviderHealthStatus.Unavailable, CredentialProviderHealthResult.Failed(CredentialProviderHealthStatus.Unavailable, failure).Status);
    }

    [Fact]
    public async Task Secure_fake_authority_verifier_rejects_forged_claims_and_authenticators()
    {
        var signingKey = Encoding.UTF8.GetBytes("test-only-authority-key");
        var binding = Binding();
        Assert.True(CredentialContractJson.TryHash(binding, out var bindingHash, out _));
        var placeholder = CredentialContractHash.Compute("placeholder");
        var unsigned = new CredentialAuthorityProof(1, Id("proof-1"), binding.ReferenceId, bindingHash!, binding.Scope, "user-1", Id("run-1"), 7, _credentialNow.AddMinutes(-5), _credentialNow.AddMinutes(5), CredentialProvider("org.embodysense.authority"), placeholder);
        var proof = unsigned with { Authenticator = Sign(unsigned, signingKey) };
        var request = new CredentialUseRequest(binding, bindingHash!, binding.Scope, proof, _credentialNow);
        var verifier = new SecureFakeAuthorityVerifier(signingKey);

        Assert.True((await verifier.VerifyAsync(request, CancellationToken.None)).Accepted);
        Assert.False((await verifier.VerifyAsync(request with { AuthorityProof = proof with { AuthorityRevision = 8 } }, CancellationToken.None)).Accepted);
        Assert.False((await verifier.VerifyAsync(request with { AuthorityProof = proof with { Authenticator = placeholder } }, CancellationToken.None)).Accepted);
    }

    private static CredentialProviderMutationRequest Mutation() => new("workspace-1", Reference(), Provider(), Id("operation-create"), 16);
    private static CredentialProviderUseRequest Use() => new("workspace-1", Reference(), Provider(), Id("operation-use"));
    private static readonly DateTimeOffset _credentialNow = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static CredentialCapabilityBinding Binding()
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/http/call", out var capabilityId, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityDescriptorHash.TryParse("sha256:" + new string('a', 64), out var descriptorHash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var capabilityProvider, out _));
        Assert.True(CapabilitySecretRequirement.TryParse("provider-token", out var requirement, out _));
        var identity = new CapabilityDescriptorIdentity(capabilityId!, version!, descriptorHash!);
        var implementation = new CapabilityImplementationIdentity(capabilityProvider!, "http/call");
        var scope = new CredentialScope("workspace-1", "role-1", "loop-1", 1, "node-1", identity, implementation, "example-api", "api.example.com", "read", "user-1", _credentialNow.AddHours(-1), _credentialNow.AddHours(1));
        return new CredentialCapabilityBinding(1, Reference(), requirement!, identity, implementation, scope);
    }

    private static CredentialContractHash Sign(CredentialAuthorityProof proof, byte[] signingKey)
    {
        Assert.True(CredentialContractJson.TrySerializeAuthorityClaim(proof, out var claim, out _));
        var authenticator = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(claim!));
        Assert.True(CredentialContractHash.TryParse("sha256:" + Convert.ToHexStringLower(authenticator), out var parsed, out _));
        return parsed!;
    }

    private static CredentialReferenceId Reference()
    {
        Assert.True(CredentialReferenceId.TryParse("credential-1", out var value, out _));
        return value!;
    }

    private static CredentialProviderId Provider()
    {
        Assert.True(CredentialProviderId.TryParse("org.embodysense.windows", out var value, out _));
        return value!;
    }

    private static CredentialProviderId CredentialProvider(string value)
    {
        Assert.True(CredentialProviderId.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private static CredentialContractId Id(string text)
    {
        Assert.True(CredentialContractId.TryParse(text, out var value, out _));
        return value!;
    }

    private sealed class RecordingConsumer : ICredentialTrustedUseConsumer
    {
        internal byte[] Observed { get; private set; } = [];

        public void Use(ReadOnlySpan<byte> credential)
        {
            Observed = credential.ToArray();
        }
    }

    private sealed class SecureFakeAuthorityVerifier(byte[] signingKey) : ICredentialAuthorityProofVerifier
    {
        private readonly byte[] _signingKey = signingKey.ToArray();

        public ValueTask<CredentialAuthorityVerificationResult> VerifyAsync(CredentialUseRequest request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || !CredentialContractValidator.Validate(request).IsValid)
            {
                return ValueTask.FromResult(CredentialAuthorityVerificationResult.Reject(CredentialFailure.FromCode(CredentialFailureCode.Unauthorized)));
            }

            var expected = Sign(request.AuthorityProof, _signingKey);
            var result = expected.FixedTimeEquals(request.AuthorityProof.Authenticator) ? CredentialAuthorityVerificationResult.Accept() : CredentialAuthorityVerificationResult.Reject(CredentialFailure.FromCode(CredentialFailureCode.Unauthorized));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SecureFakeProvider : ICredentialValueProvider
    {
        private readonly Dictionary<CredentialReferenceId, byte[]> _values = [];

        public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
        {
            return WriteAsync(request, source, replace: false, cancellationToken);
        }

        public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
        {
            return WriteAsync(request, source, replace: true, cancellationToken);
        }

        public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
            }

            if (!CredentialPortContractValidator.Validate(request).IsValid || trustedConsumer is null)
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.InvalidRequest));
            }

            if (!_values.TryGetValue(request.ReferenceId, out var value))
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.NotFound));
            }

            try
            {
                trustedConsumer.Use(value);
                return ValueTask.FromResult(CredentialProviderResult.Success());
            }
            catch
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.CallbackFailed));
            }
        }

        public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
            }

            if (!CredentialPortContractValidator.Validate(request).IsValid)
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.InvalidRequest));
            }

            if (_values.Remove(request.ReferenceId, out var removed))
            {
                CryptographicOperations.ZeroMemory(removed);
            }

            return ValueTask.FromResult(CredentialProviderResult.Success());
        }

        public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken)
        {
            var status = _values.ContainsKey(request.ReferenceId) ? CredentialProviderHealthStatus.Available : CredentialProviderHealthStatus.Missing;
            return ValueTask.FromResult(status == CredentialProviderHealthStatus.Available ? CredentialProviderHealthResult.Available() : CredentialProviderHealthResult.Missing());
        }

        private ValueTask<CredentialProviderResult> WriteAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, bool replace, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.Unavailable));
            }

            if (!CredentialPortContractValidator.Validate(request).IsValid || source is null || replace != _values.ContainsKey(request.ReferenceId))
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.InvalidRequest));
            }

            var candidate = new byte[request.ValueByteLength];
            try
            {
                source(candidate);
                if (_values.Remove(request.ReferenceId, out var previous))
                {
                    CryptographicOperations.ZeroMemory(previous);
                }

                _values.Add(request.ReferenceId, candidate);
                candidate = [];
                return ValueTask.FromResult(CredentialProviderResult.Success());
            }
            catch
            {
                return ValueTask.FromResult(Failed(CredentialFailureCode.CallbackFailed));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(candidate);
            }
        }

        private static CredentialProviderResult Failed(CredentialFailureCode code) => CredentialProviderResult.Failed(CredentialFailure.FromCode(code));
    }
}
