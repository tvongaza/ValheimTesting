using Valheim.Testing.Game;

// Attaches to an already prepared game over ValheimCLI; see ObserveCheck.cs for the one flow and README.md for each probe.
return ObserveCheck.Run(args, (host, port) => new CliTransport(host, port));
