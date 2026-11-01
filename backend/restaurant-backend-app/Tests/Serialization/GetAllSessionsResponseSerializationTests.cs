using APIHandler.Entities;
using APIHandler.Helpers;
using APIHandler.Mappers;
using APIHandler.Models.GuestSessions;
using APIHandler.Models.Orders;
using System.Text.Json;

namespace APIHandler.Tests.Serialization;

/// <summary>
/// The group snapshot returned by DELETE /guest/groups/{groupReservationId}/sessions/{guestSessionId}
/// must be camelCase JSON regardless of which ResponseBuilder method a handler uses.
/// </summary>
public class GetAllSessionsResponseSerializationTests
{
    private static GetAllSessionsResponse CreateResponse(ParticipantType type = ParticipantType.Guest)
    {
        var dish = new Dish { Id = "dish2", Name = "Tom Yum", Price = 220 };
        var item = new OrderItemResponse { Dish = DishMapper.ToDishResponse(dish), Quantity = 1 };
        var participant = new Participant("session2", type, "Olena", [item], 220m, false);

        return new GetAllSessionsResponse([participant], 220m);
    }

    [Fact]
    public void CreateJsonResponse_ProducesCamelCaseContract()
    {
        var response = ResponseBuilder.CreateJsonResponse(200, CreateResponse());

        AssertContract(response.Body, expectedType: "Guest");
    }

    [Fact]
    public void CreateSuccessResponse_ProducesCamelCaseContract()
    {
        var response = ResponseBuilder.CreateSuccessResponse(CreateResponse());

        AssertContract(response.Body, expectedType: "Guest");
    }

    [Fact]
    public void OwnerParticipant_IsSerializedAsOwnerString()
    {
        var response = ResponseBuilder.CreateJsonResponse(200, CreateResponse(ParticipantType.Owner));

        AssertContract(response.Body, expectedType: "Owner");
    }

    private static void AssertContract(string body, string expectedType)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        Assert.False(root.TryGetProperty("Participants", out _), "PascalCase key leaked into the response");
        Assert.Equal(220m, root.GetProperty("total").GetDecimal());

        JsonElement participants = root.GetProperty("participants");
        Assert.Equal(1, participants.GetArrayLength());

        JsonElement participant = participants[0];
        Assert.Equal("session2", participant.GetProperty("id").GetString());
        Assert.Equal(expectedType, participant.GetProperty("type").GetString());
        Assert.Equal("Olena", participant.GetProperty("name").GetString());
        Assert.Equal(220m, participant.GetProperty("total").GetDecimal());
        Assert.False(participant.GetProperty("isSubmitted").GetBoolean());

        JsonElement items = participant.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(1, items[0].GetProperty("quantity").GetInt32());

        JsonElement dish = items[0].GetProperty("dish");
        Assert.Equal("dish2", dish.GetProperty("id").GetString());
        Assert.Equal("Tom Yum", dish.GetProperty("name").GetString());
        Assert.Equal(220m, dish.GetProperty("price").GetDecimal());
    }
}
