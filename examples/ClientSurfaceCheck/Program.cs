using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using valheim_cli.Testing;
if(args.Length!=5){Console.Error.WriteLine("ClientSurfaceCheck <host> <port> <pins-file> <plan.json> <new-output-directory> (read-only; arrange client arrival separately)");return 2;}
var report=new ScenarioReport("client-surface-check");string output=Path.GetFullPath(args[4]);bool owns=false;
try
{
    if(Path.Exists(output))throw new IOException("Use a new output directory.");
    var plan=SurfacePlan.Read(args[3]);
    if(!PlanExpectations.TryLoad(args[2],true,out var pins,out var error))throw new ArgumentException(error);
    Directory.CreateDirectory(output);owns=true;
    report.Provenance["planSha256"]=WorldFixture.Hash(args[3]);report.Provenance["pinsSha256"]=WorldFixture.Hash(args[2]);
    using var actor=new GameActor("client-surface",new RecordingTransport(new CliTransport(args[0],int.Parse(args[1])),Path.Combine(output,"commands.jsonl")));
    report.Step("verify world and exact client plugins",()=>actor.VerifyEnvironment(pins));
    report.Step("native heightmap and collider",()=>{
        var readings=SurfaceProbe.Compare(actor,plan.ExpectedFrom,plan.Samples,plan.Tolerance);
        File.WriteAllText(Path.Combine(output,"surfaces.json"),JsonSerializer.Serialize(readings,new JsonSerializerOptions{WriteIndented=true}));
        if(readings.Any(r=>!r.Passed))throw new InvalidOperationException("Heightmap or collider mismatch; see every residual.");
    });
    // Without a declared dry support point, grounding is not checked; the report says so rather than passing it.
    report.Provenance["grounding"]=plan.Support==null?"not checked: the plan declares no support point":"checked";
    if(plan.Support is not { } support){report.Write(output);owns=false;return report.Passed?0:1;}
    report.Step("three stationary grounded observations",()=>{
        IReadOnlyList<JsonElement> states;
        try{states=PlayerPlacement.RequireSupported(actor,support);}
        catch(SupportException e){states=e.Readings;File.WriteAllText(Path.Combine(output,"support.json"),JsonSerializer.Serialize(states,new JsonSerializerOptions{WriteIndented=true}));throw new InvalidOperationException("Player is not settled on the declared ground; observer does not move the player.",e);}
        File.WriteAllText(Path.Combine(output,"support.json"),JsonSerializer.Serialize(states,new JsonSerializerOptions{WriteIndented=true}));
    });
}
catch(Exception e){try{report.Step("client check failed",()=>throw new InvalidOperationException(e.Message,e));}catch{}Console.Error.WriteLine(e.Message);}
finally{if(owns)report.Write(output);}
return report.Passed?0:1;
