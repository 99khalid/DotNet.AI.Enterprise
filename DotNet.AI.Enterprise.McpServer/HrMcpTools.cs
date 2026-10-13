using System.Text.Encodings.Web;
using System.Text.Json;
using DotNet.AI.Enterprise.Application;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DotNet.AI.Enterprise.McpServer;

/// <summary>
/// The MCP tools are the SAME methods the HR agents use (HrTools) — [Description] and JSON schema come from
/// those methods, so the agents and every MCP client always see one definition. The SDK publishes the names in
/// snake_case, the usual MCP convention (GetMyLeaveBalance → get_my_leave_balance).
///
/// Only read-only tools are exposed (least privilege). Submitting leave stays in the leave workflow,
/// where the human approval is enforced by our code, not by whichever host is connected.
/// Identity: HrTools reads ICurrentUser, never an employeeId from the model. Over stdio the server runs
/// on the employee's own machine; an HTTP transport would need a token-based ICurrentUser first.
/// </summary>
public static class HrMcpTools
{
    public static IEnumerable<McpServerTool> Create() =>
    [
        ReadOnly(nameof(HrTools.GetCurrentDate)),
        ReadOnly(nameof(HrTools.GetMyProfile)),
        ReadOnly(nameof(HrTools.GetMyLeaveBalance)),
        ReadOnly(nameof(HrTools.GetPublicHolidays)),
        ReadOnly(nameof(HrTools.SearchPolicies)),
    ];

    private static McpServerTool ReadOnly(string methodName) =>
        McpServerTool.Create(
            typeof(HrTools).GetMethod(methodName)!,
            // A fresh HrTools from DI for every call, with its own dependencies.
            request => request.Services!.GetRequiredService<HrTools>(),
            // Annotations: hints for the host (e.g. no confirmation needed for a read-only tool).
            new McpServerToolCreateOptions
            {
                ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
                SerializerOptions = JsonOptions
            });

    // Results keep Arabic as-is: the default encoder writes "أحمد" as "أحمد" —
    // six times the characters (more tokens) and harder for the model to read.
    private static readonly JsonSerializerOptions JsonOptions = new(McpJsonUtilities.DefaultOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
