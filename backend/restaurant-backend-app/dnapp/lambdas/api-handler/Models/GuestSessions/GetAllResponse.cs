using APIHandler.Models.Orders;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIHandler.Models.GuestSessions;

// Explicit JsonPropertyName on every member: positional records are otherwise serialized as PascalCase
// unless the response is built with ResponseBuilder.CreateJsonResponse (camelCase policy).
public record GetAllSessionsResponse(
    [property: JsonPropertyName("participants")] IEnumerable<Participant> Participants,
    [property: JsonPropertyName("total")] decimal Total);

public record Participant(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type"), JsonConverter(typeof(JsonStringEnumConverter<ParticipantType>))] ParticipantType Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("items")] IEnumerable<OrderItemResponse> Items,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("isSubmitted")] bool IsSubmitted);

public enum ParticipantType
{
    Owner,
    Guest
}
