using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using valheim_cli.Testing;
if(args.Length!=6){Console.Error.WriteLine("WalkingReview <host> <port> <pins-file> <route.json> <seconds 5..600> <new-output-directory>; read-only, a person drives");return 2;}
var output=Path.GetFullPath(args[5]); bool owns=false;var samples=new List<WalkSample>();
var report=new ScenarioReport("human-driven-walk-recording");
try
{
    var route=JsonSerializer.Deserialize<WalkCheckpoint[]>(File.ReadAllText(args[3]),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new ArgumentException("Empty route.");
    WalkingProbe.Validate(route);
    if(!int.TryParse(args[4],out int seconds)||seconds<5||seconds>600)throw new ArgumentException("Duration must be 5..600 seconds.");
    if(!PlanExpectations.TryLoad(args[2],true,out var pins,out var error))throw new ArgumentException(error);
    if(Path.Exists(output))throw new IOException("Use a new output directory.");
    Directory.CreateDirectory(output);owns=true;
    report.Provenance["routeSha256"]=WorldFixture.Hash(args[3]);report.Provenance["pinsSha256"]=WorldFixture.Hash(args[2]);
    using var actor=new GameActor("human-walk",new RecordingTransport(new CliTransport(args[0],int.Parse(args[1])),Path.Combine(output,"commands.jsonl")));
    actor.CommandTimeout=TimeSpan.FromSeconds(2);
    report.Step("verify environment",()=>actor.VerifyEnvironment(pins));
    string worldUid="";
    report.Step("the client is in a world",()=>worldUid=new SessionControl(actor).Read().WorldUid??throw new InvalidOperationException("The client is not in a world; join the pinned world first."));
    var cap=actor.RequireCapability("valheim.world/player-support");
    report.Provenance["observerInstance"]=cap.Instance;
    report.Step("record human-driven walk",()=>{
        var clock=Stopwatch.StartNew();
        while(clock.Elapsed.TotalSeconds<seconds)
        {
            var observation=actor.Observe(cap);
            samples.Add(WalkingProbe.Read(observation,clock.Elapsed.TotalSeconds));
            File.AppendAllText(Path.Combine(output,"samples.jsonl"),JsonSerializer.Serialize(samples[^1])+Environment.NewLine);
            Thread.Sleep(250);
        }
    });
    var evidence=WalkingProbe.Assess(route,samples);
    string reviewFile=Path.Combine(output,"review.json");
    File.WriteAllText(reviewFile,JsonSerializer.Serialize(new WalkingReview(evidence),new JsonSerializerOptions{WriteIndented=true}));
    report.Attach(new EvidenceReference("walking-review","route",worldUid,reviewFile,WorldFixture.Hash(reviewFile)));
    report.Step("trace qualifies for human review",()=>{if(!evidence.Sufficient)throw new InvalidOperationException(string.Join(", ",evidence.Issues));});
    Console.WriteLine("Recording complete; human verdict is NOT REVIEWED. Exit 0 means qualifying evidence, not usable road.");
}
catch(Exception e){try{report.Step("recording failed",()=>throw new InvalidOperationException(e.Message,e));}catch{}Console.Error.WriteLine(e.Message);}
finally{if(owns)report.Write(output);}
return report.Passed?0:1;
