namespace Garbati.IdentityApi.Contracts.Responses;

// T-031 (garbati-platform). Checked both LedgerApi and Garbati.OrchestratorApi before writing this: as of
// 19 Oct 2026 neither has a GET list endpoint at all, let alone a paged-list envelope, so there is no
// existing platform convention to reuse - this is it. Named generically (not
// "PendingApprovalRequestsPage" or similar) and kept deliberately plain so the next list endpoint on any
// Garbati service (LedgerApi, OrchestratorApi, or here) can reuse the exact same shape rather than
// inventing a slightly different one.
//
// NextPageToken is an opaque string (callers must not parse or construct it) and is null exactly when
// there is no further page - the caller's "am I done?" check is "NextPageToken is null", not "Items is
// empty" (a full last page still has a null NextPageToken; an empty intermediate page should not occur
// with this service's keyset paging, but null-check is still the correct stopping rule either way).
public record PagedResponse<T>(IReadOnlyList<T> Items, string? NextPageToken);
