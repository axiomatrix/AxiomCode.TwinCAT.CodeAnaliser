using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AxiomCode.TwinCAT.CodeAnalyser.Services;

// ──────────────────────────────────────────────────────────────
// Command-line handling
// ──────────────────────────────────────────────────────────────

if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "/?"))
{
    Console.WriteLine("AxiomCode.TwinCAT.CodeAnalyser — TwinCAT 3 Code Analysis MCP Server");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  AxiomCode.TwinCAT.CodeAnalyser.exe               Run as MCP server");
    Console.WriteLine("  AxiomCode.TwinCAT.CodeAnalyser.exe --help        Show this help message");
    Console.WriteLine("  AxiomCode.TwinCAT.CodeAnalyser.exe --test <path> Run test analysis on a project");
    Console.WriteLine();
    Console.WriteLine("MCP Tools:");
    Console.WriteLine("  twincat_analyze          Full project analysis");
    Console.WriteLine("  twincat_generate_html    Generate interactive HTML viewer");
    Console.WriteLine("  twincat_alarm_list       Extract alarm list");
    Console.WriteLine("  twincat_state_machines   Extract state machines");
    Console.WriteLine("  twincat_module_info      Get module details");
    Console.WriteLine("  twincat_io_map           Extract IO mappings");
    return 0;
}

// ──────────────────────────────────────────────────────────────
// Test mode: --test <project_path> [output.html]
// ──────────────────────────────────────────────────────────────

if (args.Length >= 2 && args[0] == "--test")
{
    var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<AnalyzerService>();
    var analyzer = new AnalyzerService(logger);

    try
    {
        var project = analyzer.AnalyzeProject(args[1]);

        Console.WriteLine($"\n{"═".PadRight(80, '═')}");
        Console.WriteLine($"PROJECT: {project.Name}");
        Console.WriteLine($"{"═".PadRight(80, '═')}");
        Console.WriteLine($"  POUs:            {project.Summary.PouCount}");
        Console.WriteLine($"  DUTs:            {project.Summary.DutCount}");
        Console.WriteLine($"  GVLs:            {project.Summary.GvlCount}");
        Console.WriteLine($"  Alarms:          {project.Summary.TotalAlarms}");
        Console.WriteLine($"    Critical:      {project.Summary.CriticalAlarms}");
        Console.WriteLine($"    Process:       {project.Summary.ProcessAlarms}");
        Console.WriteLine($"    Advisory:      {project.Summary.AdvisoryAlarms}");
        Console.WriteLine($"    Information:   {project.Summary.InformationAlarms}");
        Console.WriteLine($"    Unresolved:    {project.Summary.UnresolvedAlarms}");
        Console.WriteLine($"  State Machines:  {project.Summary.StateMachineCount}");
        Console.WriteLine($"  IO Points:       {project.Summary.IoPointCount}");
        Console.WriteLine($"  Unresolved Types:{project.Summary.UnresolvedTypeCount}");
        Console.WriteLine($"  Tree Depth:      {project.Summary.TreeDepth}");

        Console.WriteLine($"\nRoot Objects:");
        foreach (var node in project.ObjectTree)
            Console.WriteLine($"  {node.InstanceName} : {node.TypeName} [{node.Layer}]");

        // Debug: show GVL contents
        Console.WriteLine($"\nGVLs:");
        foreach (var gvl in project.GVLs.Values)
        {
            Console.WriteLine($"  {gvl.Name}: {gvl.Variables.Count} variables");
            foreach (var v in gvl.Variables.Take(5))
                Console.WriteLine($"    {v.Name} : {v.DataType} (ref={v.IsReference}, AT={v.AtBinding})");
        }

        // Debug: show first few POU names
        Console.WriteLine($"\nFirst 10 POUs:");
        foreach (var p in project.POUs.Values.Take(10))
            Console.WriteLine($"  {p.Name} [{p.PouType}] extends={p.ExtendsType} vars={p.Variables.Count}");

        if (args.Length >= 3)
        {
            HtmlGenerator.Generate(project, args[2]);
            Console.WriteLine($"\nHTML generated: {args[2]}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
        return 1;
    }

    return 0;
}

// ──────────────────────────────────────────────────────────────
// MCP Server setup
// ──────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<AnalyzerService>();

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "MCP Server terminated with error");
}

return 0;
