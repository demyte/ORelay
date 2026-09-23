using ORelay;
using ORelay.Cli;
using ORelay.Updating;

if (args.Length > 0 && string.Equals(args[0], "__auto-update", StringComparison.Ordinal))
{
    if (args.Length != 3) return 3;
    return await new ServiceAutoUpdateWorker().RunAsync(args[1], args[2]);
}

return await CliApplication.ExecuteAsync(args, commandHandler: ApplicationCommands.ExecuteAsync);
