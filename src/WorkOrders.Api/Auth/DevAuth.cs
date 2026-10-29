using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkOrders.Api.Data;

namespace WorkOrders.Api.Auth;

/// <summary>Development stand-in for an identity provider: the caller names a planner in the X-Planner header (an
/// AdventureWorks BusinessEntityID). Only planners in the planner table who have not been erased are accepted.
/// No IdP container is needed for the scenario, and this is not meant for production.</summary>
public class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, LabDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Dev";
    public const string Header = "X-Planner";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var raw)) return AuthenticateResult.NoResult();
        if (!int.TryParse(raw.ToString(), out var id)) return AuthenticateResult.Fail("Unknown planner");
        var planner = await db.Planners.AsNoTracking().SingleOrDefaultAsync(p => p.BusinessEntityId == id && p.ErasedAt == null);
        if (planner == null) return AuthenticateResult.Fail("Unknown planner");
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, planner.DisplayName ?? "planner " + id)], Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }
}
