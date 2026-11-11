using WebhookRelay.Api.Services;

namespace WebhookRelay.Tests;

public class SignatureValidatorTests
{
    private const string Secret = "my-secret";
    private const string Payload = "{\"action\":\"opened\"}";

    [Fact]
    public void IsValid_ReturnsTrue_ForMatchingSignature()
    {
        string header = SignatureValidator.ComputeHeader(Payload, Secret);

        Assert.True(SignatureValidator.IsValid(Payload, Secret, header));
    }

    [Fact]
    public void IsValid_MatchesValueComputedOutsideThisCode()
    {
        // Worked out separately with Python's hmac module for the same payload and secret.
        // The other tests use ComputeHeader to build the signature, so they would still
        // pass if ComputeHeader and IsValid were both wrong in the same way. This one would not.
        string header = "sha256=6e862694630eef7c97c7d4f30c782ee08d3f4f75e278d2c1f88a0ef4dfd43ebe";

        Assert.True(SignatureValidator.IsValid(Payload, Secret, header));
        Assert.Equal(header, SignatureValidator.ComputeHeader(Payload, Secret));
    }

    [Fact]
    public void IsValid_AcceptsUppercaseHex()
    {
        string header = SignatureValidator.ComputeHeader(Payload, Secret);
        string upper = "sha256=" + header.Substring("sha256=".Length).ToUpperInvariant();

        Assert.True(SignatureValidator.IsValid(Payload, Secret, upper));
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenSecretIsDifferent()
    {
        string header = SignatureValidator.ComputeHeader(Payload, "some-other-secret");

        Assert.False(SignatureValidator.IsValid(Payload, Secret, header));
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenPayloadWasChanged()
    {
        string header = SignatureValidator.ComputeHeader(Payload, Secret);

        Assert.False(SignatureValidator.IsValid(Payload + " ", Secret, header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha256=not-hex")]
    [InlineData("md5=abcdef")]
    [InlineData("abcdef")]
    public void IsValid_ReturnsFalse_ForMissingOrMalformedHeader(string? header)
    {
        Assert.False(SignatureValidator.IsValid(Payload, Secret, header));
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenSecretIsNotConfigured()
    {
        string header = SignatureValidator.ComputeHeader(Payload, "");

        Assert.False(SignatureValidator.IsValid(Payload, "", header));
    }
}
