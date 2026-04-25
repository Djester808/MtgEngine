using FluentAssertions;
using MtgEngine.Api.Services;
using Xunit;

namespace MtgEngine.Rules.Tests;

public sealed class PasswordHasherTests
{
    // ---- Hash ----

    [Fact]
    public void Hash_ReturnsNonEmptyString()
    {
        var hash = PasswordHasher.Hash("password");
        hash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Hash_IncludesSaltSeparator()
    {
        // Stored format is "base64Salt:base64Hash"
        var hash = PasswordHasher.Hash("password");
        hash.Should().Contain(":");
    }

    [Fact]
    public void Hash_ProducesDifferentOutputEachCall()
    {
        // Random salt means two hashes of the same password must differ
        var h1 = PasswordHasher.Hash("password");
        var h2 = PasswordHasher.Hash("password");
        h1.Should().NotBe(h2);
    }

    // ---- Verify ----

    [Fact]
    public void Verify_ReturnsTrueForCorrectPassword()
    {
        var hash = PasswordHasher.Hash("correct-horse-battery-staple");
        PasswordHasher.Verify("correct-horse-battery-staple", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_ReturnsFalseForWrongPassword()
    {
        var hash = PasswordHasher.Hash("correct");
        PasswordHasher.Verify("wrong", hash).Should().BeFalse();
    }

    [Fact]
    public void Verify_ReturnsFalseForEmptyPassword()
    {
        var hash = PasswordHasher.Hash("password");
        PasswordHasher.Verify("", hash).Should().BeFalse();
    }

    [Fact]
    public void Verify_ReturnsFalseForMalformedHash()
    {
        PasswordHasher.Verify("password", "not-a-valid-hash").Should().BeFalse();
    }

    [Fact]
    public void Verify_ReturnsFalseForEmptyHash()
    {
        PasswordHasher.Verify("password", "").Should().BeFalse();
    }

    [Fact]
    public void Verify_IsCaseSensitive()
    {
        var hash = PasswordHasher.Hash("Password");
        PasswordHasher.Verify("password", hash).Should().BeFalse();
    }
}
