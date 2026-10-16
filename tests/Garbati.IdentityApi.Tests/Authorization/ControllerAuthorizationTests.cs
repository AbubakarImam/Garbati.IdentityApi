using System.Reflection;
using Garbati.IdentityApi.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Tests.Authorization;

// Guards new endpoints: every controller action must name exactly one known scope policy.
// The fallback policy would still demand a key, but any key at all is not the intended rule.
public class ControllerAuthorizationTests
{
    [Fact]
    public void EveryControllerAction_RequiresAKnownScopePolicy()
    {
        var actions = typeof(Program).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ControllerBase)) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName)
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
}
