using System.Text.Json.Serialization;

namespace DotNet.AI.Enterprise.Domain;

// Serialized as a string so the model picks from fixed names ("Annual", "Sick", ...) instead of free text.
[JsonConverter(typeof(JsonStringEnumConverter<LeaveType>))]
public enum LeaveType
{
    Annual,
    Sick,
    Unpaid,
    Other
}
