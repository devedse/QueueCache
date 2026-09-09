using QueueCache.Cli;
using System.CommandLine;

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("QueueCache requires Windows."); return 1; }
try
{
    if (args.Length > 0 && args[0] is "apply" or "profiles" or "restore") args = ["policy", .. args];
    // Preserve the old `policy <device> <preset>` spelling for deployed scripts.
    if (args.Length >= 3 && args[0] == "policy" &&
        (args[1].StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase) || args[1].EndsWith(':')))
        args = ["policy", "set", .. args.Skip(1)];
    var root = Commands.Create(LegacyCommands.Execute);
    var parsed = root.Parse(args.Length == 0 ? ["--help"] : args);
    if (parsed.Errors.Count != 0)
    {
        foreach (var error in parsed.Errors) Console.Error.WriteLine(error.Message);
        return 2;
    }
    return await parsed.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled. Caching continues; inspect status."); return 130; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
