using System.Text.Json;
using Garbati.IdentityApi.Contracts.Outbox;
using Garbati.IdentityApi.Entities;

namespace Garbati.IdentityApi.Tests.Services;

// DESIGN.md Section 2.1/2.5, decision #14: the Closed/Deceased cascade will eventually enqueue an
// OutboxMessage shaped like AccountFreezeRequestedPayload once T-017 adds Mandates. T-016 builds that
// shape but never instantiates it on a real cascade path (CustomerService has no mandates to look up
// yet) - this test proves the shape itself is sound (serializes and round-trips) without faking data.
public class OutboxMessageShapeTests
{
    [Fact]
    public void AccountFreezeRequestedPayload_RoundTripsThroughJson()
    {
        var payload = new AccountFreezeRequestedPayload(
            LedgerAccountId: "1000000001",
            CustomerId: "CUS-ABCDEFGHJKLMNPQ",
            Reason: "Customer closed",
            RequestedAt: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<AccountFreezeRequestedPayload>(json);

        Assert.Equal(payload, roundTripped);
    }

    [Fact]
    public void AnOutboxMessage_CanBeBuiltFromThePayloadShape()
    {
        var payload = new AccountFreezeRequestedPayload("1000000001", "CUS-ABCDEFGHJKLMNPQ", "Customer deceased", DateTimeOffset.UtcNow);

        var message = new OutboxMessage
        {
            MessageType = OutboxMessageTypes.AccountFreezeRequested,
            Payload = JsonSerializer.Serialize(payload),
            Status = OutboxMessageStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal(OutboxMessageTypes.AccountFreezeRequested, message.MessageType);
        var deserialized = JsonSerializer.Deserialize<AccountFreezeRequestedPayload>(message.Payload);
        Assert.Equal(payload, deserialized);
    }
}
