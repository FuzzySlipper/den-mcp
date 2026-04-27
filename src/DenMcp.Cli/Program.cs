using DenMcp.Cli;
using DenMcp.Cli.Commands;
using DenMcp.Core;

var config = CliConfig.Load();
var router = new CommandRouter(args);

var serverUrl = router.GetFlag("server") ?? config.ServerUrl;
using var client = new DenApiClient(serverUrl);

try
{
    var exitCode = router.Command switch
    {
        "projects" => await ProjectCommands.List(client),
        "project" => await ProjectCommands.Get(client, router),
        "tasks" => await TaskCommands.List(client, router),
        "task" => await TaskCommands.Get(client, router),
        "dispatch" => await DispatchCommands.Run(client, router),
        "next" => await TaskCommands.Next(client, router),
        "create-task" => await TaskCommands.Create(client, router),
        "status" => await TaskCommands.SetStatus(client, router),
        "messages" => await MessageCommands.List(client, router),
        "send" => await MessageCommands.Send(client, router),
        "docs" => await DocumentCommands.List(client, router),
        "doc" => await DocumentCommands.Get(client, router),
        "search" => await DocumentCommands.Search(client, router),
        "guidance" => await AgentGuidanceCommands.Run(client, router),
        "blackboard" => await BlackboardCommands.Run(client, router),
        "dashboard" or "watch" => await DashboardCommand.Run(client, router),
        "--version" or "-v" => ShowVersion(),
        "help" or "--help" or "-h" or null => ShowHelp(),
        _ => ShowUnknown(router.Command!)
    };
    return exitCode;
}
catch (HttpRequestException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Error: Could not connect to den-mcp server at {serverUrl}");
    Console.Error.WriteLine($"  {ex.Message}");
    Console.ResetColor();
    return 1;
}

static int ShowVersion()
{
    Console.WriteLine($"den-mcp {BuildInfo.DisplayVersion}");
    return 0;
}

static int ShowHelp()
{
    Console.WriteLine("""
        den-mcp CLI

        Usage: den <command> [options]

        Commands:
          projects                       List all projects
          project [id]                   Show project details with stats
          tasks                          List tasks (top-level)
          task <id>                      Show task details
          dispatch                       List or act on dispatches
          next                           Get next unblocked task
          create-task --title <title>    Create a new task
          status <id> <status>           Update task status
          messages                       List recent messages [--intent <intent>]
          send --content <text>          Send a message
          docs                           List documents
          doc <slug>                     Show a document
          search <query>                 Full-text search documents
          guidance                       Resolve/list/manage agent guidance
          blackboard                     Shared cross-project Markdown memory
          dashboard                      Live TUI dashboard
                                         Use --legacy-dispatches to inspect legacy dispatches

        Global options:
          --project, -p <id>             Project ID (auto-detected from directory name)
          --server <url>                 Server URL (default: http://localhost:5199)
          --legacy-dispatches            Dashboard only: show legacy/debug dispatch pane
          --help, -h                     Show this help
        """);
    return 0;
}

static int ShowUnknown(string cmd)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Unknown command: {cmd}");
    Console.ResetColor();
    Console.Error.WriteLine("Run 'den --help' for usage.");
    return 1;
}
