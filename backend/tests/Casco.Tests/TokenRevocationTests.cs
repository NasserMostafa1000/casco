using System.Text;
using Casco.Api.Domain;
using Casco.Api.Features.Auth;
using Casco.Api.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Casco.Tests;

public class TokenRevocationTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static TokenService Tokens() => new(Options.Create(new JwtOptions { Key = Key, Issuer = "casco", ExpiryDays = 14 }));

    [Fact]
    public async Task Logout_rejects_that_token_and_leaves_the_other_one_valid()
    {
        using var db = new TestDb();
        var tokens = Tokens();
        var user = new User { Email = "a@b.co", Name = "A" };
        db.Db.Users.Add(user);
        await db.Db.SaveChangesAsync();

        var first = tokens.CreateUserToken(user);
        var second = tokens.CreateUserToken(user);
        var loggedOut = await tokens.ReadAppSessionAsync("Bearer " + first);
        var kept = await tokens.ReadAppSessionAsync("Bearer " + second);

        Assert.NotNull(loggedOut);
        Assert.NotNull(kept);
        Assert.NotEqual(loggedOut.Value.Jti, kept.Value.Jti);

        await TokenRevocation.RevokeAsync(db.Db, loggedOut.Value.Jti, loggedOut.Value.ExpiresAt);

        Assert.True(await TokenRevocation.IsRevokedAsync(db.Db, loggedOut.Value.Jti));
        Assert.False(await TokenRevocation.IsRevokedAsync(db.Db, kept.Value.Jti));
    }

    [Fact]
    public async Task Token_without_an_id_is_not_accepted()
    {
        var tokens = Tokens();
        var raw = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "casco",
            Audience = TokenService.AppAudience,
            Claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Sub] = Guid.NewGuid().ToString() },
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256)
        });

        Assert.Null(await tokens.ReadAppSessionAsync("Bearer " + raw));
    }
}
