using System.Reflection;
using Garbati.IdentityApi.Controllers;
using Garbati.IdentityApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Tests.Authorization;

// D-29 (garbati-platform): external feedback flagged a TOCTOU risk - a checker could approve a
// joint-approval request that was silently edited after they reviewed it. Investigated directly:
// PendingApprovalRequest has no edit path today - its own entity comment already states its
// canonical fields are "fixed at creation time so a later approval cannot be tricked into approving
// a changed transaction" (T-018). This test pins that invariant in code: if AuthorizationController
// or AccountsController ever gains a PUT/PATCH action, or IJointApprovalService ever grows a new
// method, this test fails and forces a deliberate decision about whether that addition reopens the
// race, rather than letting the safety property silently erode.
public class JointApprovalNoEditRegressionTests
{
    // The exact, closed set of public methods on IJointApprovalService today (confirmed directly
    // against Services/Interfaces/IJointApprovalService.cs, not copied from elsewhere). None of
    // these mutate a PendingApprovalRequest's canonical transaction fields after creation -
    // ApproveAsync only appends an ApprovalRecord and, at threshold, writes Status/DecisionId (or
    // the separate MandateApprovalPolicy row for a ThresholdChange request). Closed on purpose: a
    // brand new method - not just one literally named "Edit" or "Update" - should also force a
    // deliberate look at whether it reopens this race.
    private static readonly HashSet<string> ExpectedJointApprovalServiceMethods =
    [
        nameof(IJointApprovalService.RequestDebitAsync),
        nameof(IJointApprovalService.ApproveAsync),
        nameof(IJointApprovalService.GetPendingRequestAsync),
        nameof(IJointApprovalService.GetApprovalPolicyAsync),
        nameof(IJointApprovalService.RequestThresholdChangeAsync),
        nameof(IJointApprovalService.ListPendingRequestsAsync),
    ];

    [Fact]
    public void IJointApprovalService_ExposesExactlyTheExpectedClosedMethodSet()
    {
        var actualMethodNames = typeof(IJointApprovalService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToHashSet();

        var added = actualMethodNames.Except(ExpectedJointApprovalServiceMethods).ToList();
        var removed = ExpectedJointApprovalServiceMethods.Except(actualMethodNames).ToList();

        Assert.True(
            added.Count == 0 && removed.Count == 0,
            "IJointApprovalService's method set changed - this is a deliberate tripwire (D-29). "
            + (added.Count > 0 ? $"Added: {string.Join(", ", added)}. " : string.Empty)
            + (removed.Count > 0 ? $"Removed: {string.Join(", ", removed)}. " : string.Empty)
            + "A new method here (not just one named 'Edit' or 'Update') needs a deliberate look at "
            + "whether it introduces a way to mutate a PendingApprovalRequest's canonical fields after "
            + "a checker has already reviewed it - see this file's top-of-file comment and "
            + "PendingApprovalRequest.cs's own doc comment.");
    }

    [Theory]
    [InlineData(typeof(AuthorizationController))]
    [InlineData(typeof(AccountsController))]
    public void JointApprovalControllers_HaveNoPutOrPatchAction(Type controllerType)
    {
        var offendingMethods = controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.GetCustomAttribute<HttpPutAttribute>() is not null
                || m.GetCustomAttribute<HttpPatchAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        Assert.True(
            offendingMethods.Count == 0,
            $"{controllerType.Name} has an HttpPut/HttpPatch action ({string.Join(", ", offendingMethods)}) - "
            + "this is a deliberate tripwire (D-29): a PendingApprovalRequest has no edit path today by "
            + "design, so an edit endpoint appearing here needs a deliberate look at whether it lets a "
            + "request be mutated after a checker has already reviewed it, not a silent merge.");
    }
}
