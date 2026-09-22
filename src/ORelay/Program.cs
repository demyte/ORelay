using ORelay;
using ORelay.Cli;

return await CliApplication.ExecuteAsync(args, commandHandler: ApplicationCommands.ExecuteAsync);
