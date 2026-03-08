using Microsoft.Build.Utilities;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace UtgKit.Cli;

public class Program
{
    public static Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("UTG Kit — CLI tools for Universal Terminology Governance");

        // TODO: Add commands
        // rootCommand.AddCommand(new ValidateCommand());
        // rootCommand.AddCommand(new DiffCommand());

        //var parser = new CommandLineBuilder(rootCommand)
        //    .UseDefaults()
        //    .Build();

        // return parser.InvokeAsync(args);
        return System.Threading.Tasks.Task.FromResult(1);
    }
}
