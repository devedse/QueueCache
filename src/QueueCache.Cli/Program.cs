using System.CommandLine;
using QueueCache.Cli;

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("QueueCache requires Windows."); return 1; }
try
{
    if (args.Length == 1 && args[0] == "--managed-service-host")
        return QueueCache.Operations.ManagedDisks.WindowsManagedBrokerService.Run();
    if (args.Length == 1 && args[0] == "--managed-service-install")
    {
        QueueCache.Operations.ManagedDisks.WindowsManagedBrokerService.Install(Environment.ProcessPath!); return 0;
    }
    if (args.Length == 1 && args[0] == "--managed-service-start")
    {
        QueueCache.Operations.ManagedDisks.WindowsManagedBrokerService.Start(); return 0;
    }
    if (args.Length == 1 && args[0] == "--managed-service-remove")
    {
        await QueueCache.Operations.ManagedDisks.WindowsManagedBrokerService.RequireUpdateReadyAsync();
        QueueCache.Operations.ManagedDisks.WindowsManagedBrokerService.Remove(); return 0;
    }
    if (args.Length == 1 && args[0] == "--managed-update-preflight")
    {
        await QueueCache.Operations.ManagedDisks.WindowsManagedBrokerService.RequireUpdateReadyAsync(); return 0;
    }
    if (args.Length == 2 && args[0] == "--managed-provider-install")
        return QueueCache.Operations.ManagedDisks.WindowsProviderInstallation.Install(args[1]) ? 3010 : 0;
    if (args.Length == 1 && args[0] == "--managed-provider-remove")
    {
        QueueCache.Operations.ManagedDisks.WindowsProviderInstallation.Remove(); return 0;
    }
    // Private transport for the foreground verification coordinator; still the same installed executable.
    if (args.Length == 2 && args[0] == "--verification-worker")
    {
        try
        {
            return await QueueCache.Developer.Verification.VerificationWorker.ExecuteAsync(args[1]);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    if (args.Length > 0 && args[0] is "apply" or "profiles" or "restore")
        args = ["policy", .. args];
    // Preserve the old `policy <device> <preset>` spelling for deployed scripts.
    if (args.Length >= 3 && args[0] == "policy" &&
        (args[1].StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase) || args[1].EndsWith(':')))
        args = ["policy", "set", .. args.Skip(1)];
    var root = Commands.Create(LegacyCommands.Execute);
    var parsed = root.Parse(args.Length == 0 ? ["--help"] : args);
    if (parsed.Errors.Count != 0)
    {
        foreach (var error in parsed.Errors)
            Console.Error.WriteLine(error.Message);
        return 2;
    }
    return await parsed.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled. Refresh status before retrying; completed commits remain effective."); return 130; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
