using PalPeek.Core;

namespace PalPeek.Core.Tests;

public sealed class WebInvitePasswordTests
{
    [Fact]
    public void HashUsesVersionedPbkdf2ParametersAndVerifiesPassword()
    {
        const string password = "correct-horse-2026";
        var (salt, hash) = WebInvitePassword.Hash(password);
        var invite = new WebInvite
        {
            Id = WebInvitePassword.CreateInviteId(),
            Name = "朋友",
            PasswordSalt = salt,
            PasswordHash = hash,
            PasswordIterations = WebInvitePassword.Iterations
        };

        Assert.True(WebInvitePassword.Verify(invite, password));
        Assert.False(WebInvitePassword.Verify(invite, "wrong-password-2026"));
        Assert.Equal(600_000, invite.PasswordIterations);
        Assert.DoesNotContain(password, invite.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public void InviteIdsContainAtLeast128BitsOfRandomData()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => WebInvitePassword.CreateInviteId()).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id.Length >= 22));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public void InvalidPasswordLengthIsRejected(string password)
    {
        Assert.Throws<ArgumentException>(() => WebInvitePassword.Hash(password));
    }
}
