using System.Globalization;
using Valheim.Testing.Game;
using valheim_cli.Testing;
if(args.Length!=10)
{
    Console.Error.WriteLine("TerrainCapture <host> <port> <pins-file> <x> <z> <spacing> <countX> <countZ> <generator|loaded-ground> <new-output.json>");return 2;
}
try
{
    var c=CultureInfo.InvariantCulture;
    var grid=new TerrainGridRequest(float.Parse(args[3],c),float.Parse(args[4],c),float.Parse(args[5],c),int.Parse(args[6],c),int.Parse(args[7],c),args[8]);
    grid.Validate();if(File.Exists(args[9]))throw new IOException("Output already exists.");
    if(!PlanExpectations.TryLoad(args[2],true,out string pins,out string error))throw new InvalidOperationException(error);
    using var actor=new GameActor("capture",new CliTransport(args[0],int.Parse(args[1],c)));
    actor.VerifyEnvironment(pins);
    Valheim.Testing.Game.TerrainCapture.Read(actor,grid,"Pinned ValheimCLI terrain-grid observation; retain environment pins separately.").Save(args[9]);
    Console.WriteLine("Captured exact inputs; not native calibration or an atomic world snapshot.");return 0;
}
catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
