using System.Reflection;
using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Tests.Authorization;

// Guards new endpoints: every controller action must name exactly one known scope policy.
// The fallback policy would still demand a key, but any key at all is not the intended rule.
public class ControllerAuthorizationTests
{
    // T-020: AuthorizationController.RequestAuthorization is a deliberate, narrow exception to the
    // "exactly one static scope policy" rule below. POST /v1/authorization/requests now serves two
    // operation kinds - ordinary Debit/Credit (identity.authorization.check) and Reversal
    // (identity.ops.reversal, decision #30) - that need two different, mutually exclusive scopes
    // depending on the request BODY, not the route. [Authorize(Policy=...)] is evaluated before the body
    // is even bound, so a single static attribute cannot express that choice; the action checks the right
    // scope explicitly instead (see its own comment) and still uses [Authorize] (no policy) so it is never
    // reachable without SOME valid API key. This is exercised by AuthorizationTests.cs
    // (identity.authorization.check, Debit/Credit) and the T-020 reversal test files
    // (identity.ops.reversal, Reversal) instead of by this structural test.
    private static readonly HashSet<(Type DeclaringType, string Name)> BodyDependentScopeExceptions =
        [(typeof(AuthorizationController), nameof(AuthorizationController.RequestAuthorization))];

    [Fact]
    public void EveryControllerAction_RequiresAKnownScopePolicy()
    {
        var actions = typeof(Program).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ControllerBase)) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName)
            .Where(m => !BodyDependentScopeExceptions.Contains((m.DeclaringType!, m.Name)))
            .ToList();

        var violations = actions
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                || m.GetCustomAttribute<AuthorizeAttribute>()?.Policy is not { } policy
                || !IdentityScopes.All.Contains(policy))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        Assert.True(actions.Count >= 7, $"Expected at least 7 controller actions, found {actions.Count}.");
        Assert.Empty(violations);
    }

    // The exempted action still needs SOME guard - this proves it carries a plain [Authorize] (so the
    // fallback-anonymous risk the test above exists to catch doesn't quietly reappear here instead).
    [Fact]
    public void RequestAuthorization_IsNotAnonymous_EvenThoughItIsExemptFromTheScopePolicyCheck()
    {
        var method = typeof(AuthorizationController).GetMethod(nameof(AuthorizationController.RequestAuthorization))!;

        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(method.GetCustomAttribute<AuthorizeAttribute>());
    }
}
