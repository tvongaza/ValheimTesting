using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using valheim_cli.Testing;
if(args.Length!=5){Console.Error.WriteLine("PaintCheck <host> <port> <pins-file> <plan.json> <new-output-directory>; read-only loaded paint, arrange arrival separately");return 2;}
var output=Path.GetFullPath(args[4]);bool owns=false;var report=new ScenarioReport("loaded-paint-check");
try
{
    var plan=JsonSerializer.Deserialize<Plan>(File.ReadAllText(args[3]),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new ArgumentException("Empty plan.");
    PaintProbe.Validate(plan.ExpectedFrom,plan.Samples,plan.Tolerance);
    if(!PlanExpectations.TryLoad(args[2],true,out var pins,out var error))throw new ArgumentException(error);
    if(Path.Exists(output))throw new IOException("Use a new output directory.");
    Directory.CreateDirectory(output);owns=true;
    report.Provenance["planSha256"]=WorldFixture.Hash(args[3]);report.Provenance["pinsSha256"]=WorldFixture.Hash(args[2]);
    report.Provenance["expectedFrom"]=plan.ExpectedFrom;
    using var actor=new GameActor("paint-observer",new RecordingTransport(new CliTransport(args[0],int.Parse(args[1])),Path.Combine(output,"commands.jsonl")));
    report.Step("verify environment",()=>actor.VerifyEnvironment(pins));
    report.Step("loaded paint channels",()=>{
        var readings=PaintProbe.Compare(actor,plan.ExpectedFrom,plan.Samples,plan.Tolerance);
        File.WriteAllText(Path.Combine(output,"paint.json"),JsonSerializer.Serialize(readings,new JsonSerializerOptions{WriteIndented=true}));
        if(readings.Any(r=>!r.Passed))throw new InvalidOperationException("Paint mismatch; see channel residuals.");
    });
}
catch(Exception e){try{report.Step("paint check failed",()=>throw new InvalidOperationException(e.Message,e));}catch{}Console.Error.WriteLine(e.Message);}
finally{if(owns)report.Write(output);}
return report.Passed?0:1;
public sealed class Plan
{
    public string ExpectedFrom{get;set;}="";
    public float Tolerance{get;set;}=.01f;
    public List<PaintExpectation> Samples{get;set;}=[];
}
