using ModelContextProtocol.Server;
using System.ComponentModel;

namespace UtgKit.Web.Mcp.Tools;

[McpServerToolType]
public static class HelloTool
{
    [McpServerTool, Description("A simple greeting tool to verify the MCP server is running.")]
    public static string Hello(
        [Description("The name to greet")] string name = "World")
    {
        return $"Hello there, {name}! UTG Kit MCP server is running.";
    }
}
