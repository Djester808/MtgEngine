using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void HashThenVerify_RoundTrips()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");
        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
        Assert.False(PasswordHasher.Verify("wrong password", hash));
    }

    [Theory]
    [InlineData("SEEDED_NO_LOGIN")]          // the seeder's sentinel — no separator
    [InlineData("not-base64!:also-not!")]    // separator present, payload corrupt
    [InlineData("")]
    [InlineData("a:b:c")]
    public void Verify_CorruptStoredHash_FailsInsteadOfThrowing(string stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
    }

    [Fact]
    public void VerifyDummy_BurnsCostWithoutThrowing()
    {
        // Login calls this for nonexistent accounts so timing does not reveal existence.
        PasswordHasher.VerifyDummy("anything");
    }
}
