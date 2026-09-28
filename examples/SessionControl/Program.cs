using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;
using valheim_cli.Testing;

if (args.Length < 4)
{
    Console.Error.WriteLine("SessionControl <host> <port> <pins-file> state|save <worldUID>|join <address> <character> [password-env]|leave");
    return 2;
}
try
{
    if (!PlanExpectations.TryLoad(args[2], true, out string pins, out string error)) throw new InvalidOperationException(error);
    using var actor = new GameActor("session", new CliTransport(args[0], int.Parse(args[1], CultureInfo.InvariantCulture)));
    actor.VerifyEnvironment(pins);
    var control = new Valheim.Testing.Game.SessionControl(actor);
    switch (args[3])
    {
        case "state" when args.Length == 4: Console.WriteLine(JsonSerializer.Serialize(control.Read())); break;
        case "save" when args.Length == 5: Console.WriteLine("Confirmed save number " + control.Save(args[4], TimeSpan.FromSeconds(120))); break;
        case "join" when args.Length is 6 or 7:
            control.Join(args[4], args[5], args.Length == 7 ? args[6] : null);
            Console.WriteLine("Joined. Verify the destination world pins before further actions."); break;
        case "leave" when args.Length == 4:
            control.Leave(); Console.WriteLine("Returned to menu. World-save success must be checked on the server separately."); break;
        default: throw new ArgumentException("Invalid session action or arguments.");
    }
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
