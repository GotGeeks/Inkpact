using System.Security.Claims;
using Inkpact.Api.Auth;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Inkpact.Api.Tests.Auth;

public class UserIdExtensionsTests
{
    [Fact]
    public void The_signed_in_users_id_is_read_from_the_claim()
    {
        var id = Guid.NewGuid();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test")),
        };

        http.UserId().ShouldBe(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void No_user_or_an_id_that_is_no_guid_throws_instead_of_giving_an_empty_id(string? claim)
    {
        var identity = new ClaimsIdentity();
        if (claim is not null)
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, claim));
        }

        Should.Throw<InvalidOperationException>(() => new ClaimsPrincipal(identity).UserId());
    }
}
