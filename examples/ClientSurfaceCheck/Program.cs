using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using valheim_cli.Testing;
if(args.Length!=5){Console.Error.WriteLine("ClientSurfaceCheck <host> <port> <pins-file> <plan.json> <new-output-directory> (read-only; arrange client arrival separately)");return 2;}
var report=new ScenarioReport("client-surface-check");string output=Path.GetFullPath(args[4]);bool owns=false;
try
{
    if(Path.Exists(output))throw new IOException("Use a new output directory.");
    var plan=JsonSerializer.Deserialize<Plan>(File.ReadAllText(args[3]),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new ArgumentException("Empty plan.");
    TerrainProbe.Validate("loaded-ground",plan.ExpectedFrom,plan.Samples,plan.Tolerance);
    TerrainProbe.Validate("loaded-ground",plan.ExpectedFrom,new[]{plan.Support},.3f);
    if(plan.Samples.Any(s=>s.X!=MathF.Round(s.X)||s.Z!=MathF.Round(s.Z)))throw new ArgumentException("Surface samples must be grid vertices.");
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
    var cap=actor.RequireCapability("valheim.world/player-support");
    var states=new List<JsonElement>();
    report.Step("three stationary grounded observations",()=>{
        for(int i=0;i<3;i++){
            if(i>0)Thread.Sleep(500);
            var state=actor.Observe(cap);states.Add(state.Data);
            File.WriteAllText(Path.Combine(output,"support.json"),JsonSerializer.Serialize(states,new JsonSerializerOptions{WriteIndented=true}));
            if(!SurfaceProbe.Supported(state,plan.Support))throw new InvalidOperationException("Player is not settled on the declared ground; observer does not move the player.");
        }
    });
}
catch(Exception e){try{report.Step("client check failed",()=>throw new InvalidOperationException(e.Message,e));}catch{}Console.Error.WriteLine(e.Message);}
finally{if(owns)report.Write(output);}
return report.Passed?0:1;
public sealed class Plan
{
    public string ExpectedFrom{get;set;}="";
    public float Tolerance{get;set;}=.05f;
    public List<HeightExpectation> Samples{get;set;}=[];
    public HeightExpectation Support{get;set;}=new(float.NaN,float.NaN,float.NaN);
}
