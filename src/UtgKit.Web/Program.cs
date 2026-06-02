using UtgKit.Core;
using UtgKit.Web.Components;

// Handle --help / -h before spinning up the host so it works nicely as a dotnet tool.
if (args.Any(a => a is "--help" or "-h" or "-?" or "/?"))
{
    Console.WriteLine("""
        UtgKit.Web - Universal Terminology Governance toolkit (web UI + MCP server)

        Usage:
          utgkit [options]

        Options:
          -r, --repo <path>        Path to the local THO repo clone. Defaults to the
                                   current working directory if not specified.
          --urls <url>             URL(s) the server should listen on
                                   (e.g. http://localhost:5099). Comma-separated.
          --ThoRepo:Path <path>    Canonical configuration form of --repo.
          -h, --help               Show this help text.

        Configuration can also be supplied via environment variables (ThoRepo__Path)
        or appsettings.json. Command-line values take precedence.
        """);
    return;
}

// When packaged as a `dotnet tool`, the current working directory is wherever
// the user invoked `utgkit` from (typically a THO repo clone), NOT where the
// tool's assemblies live. We must pin ContentRoot to the tool's install
// directory so static web assets (wwwroot, Blazor _framework files, css, etc.)
// resolve correctly.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Default ThoRepo:Path to the current working directory so `utgkit` "just works"
// when launched from inside a THO clone. This is added as the lowest-priority
// source so appsettings.json, environment variables, and the command line all
// override it.
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ThoRepo:Path"] = Environment.CurrentDirectory,
});

// Map friendly command-line switches onto configuration keys so the app can
// be launched (e.g. as a dotnet tool) with `--repo <path>` or `-r <path>`
// in addition to the canonical `--ThoRepo:Path <path>` form.
var switchMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["--repo"] = "ThoRepo:Path",
    ["-r"] = "ThoRepo:Path",
};
builder.Configuration.AddCommandLine(args, switchMappings);

// Add Blazor Server services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register UtgKit.Core services
builder.Services.AddUtgKitCore(builder.Configuration);

// Register MCP server
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Map MCP endpoint
app.MapMcp("/mcp");

app.Run();
