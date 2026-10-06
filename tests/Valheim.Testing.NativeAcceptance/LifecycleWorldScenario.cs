using MyMod.SystemTests;
using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>
/// <c>lifecycle-world</c>: the dry-site rounds on an owned server with a client that runs MyMod and its adapter, plus the
/// lifecycle events beyond a restart and the world state a mod depends on.
/// <list type="number">
/// <item>Server (MyMod's patches already checked by the runner's session, #30): no marker, the mod marks the dry site and refuses the wet one.</item>
/// <item>First round, beside the marker: the client sees it with MyMod's saved label; a global key the fixture lacks is set
/// on the server and the client lists the server's keys (#23); a vanilla dungeon's saved rooms lie in its location's zone
/// (#24); the player leaves the area until the client unloads the site's zones and comes back, and the marker is there
/// again with its label (#35 zone cycle).</item>
/// <item>Confirmed save, restart of only the owned server: the server still has the marker.</item>
/// <item>After the restart: the client sees the marker and still lists the key; MyMod writes this run's note on the
/// player, the client logs out (the character file must be rewritten) and in again, and the note comes back (#35 logout).</item>
/// </list>
/// A control run (<c>expectFailure</c>) replaces one check by that check's expected failure and ends there:
/// <c>missing-harmony-target</c> after the server census, <c>field-only-state</c> after the zone cycle,
/// <c>suppressed-profile-save</c> at the logout.
/// </summary>
public static class LifecycleWorldScenario
{
    public const string NoteKey = "mymod.note";

    public static void Run(GameSession session, AcceptancePlan plan)
    {
        var report = session.Report; var client = plan.Client!; var control = plan.Control;
        if (control?.Name == ControlPlugins.MissingHarmonyTarget)
        {
            MissingTarget(session, plan, control);
            throw new ControlConcluded(control);
        }
        CampaignSteps.MarkSites(plan, session.Server!.Game, report);

        var timeout = TimeSpan.FromSeconds(client.ArrivalSeconds);
        var zoneCycle = new ZoneCycle
        {
            Capability = Capabilities.Zones, Zones = AcceptancePlan.MarkerZones(plan.DrySite), Away = CampaignSteps.At(plan.Away!), Back = CampaignSteps.At(plan.Arrival),
            StepTimeout = timeout, Interval = session.Interval,
        };
        var logout = new LogoutCycle
        {
            Capability = Capabilities.CustomData, KeyPrefix = "mymod.", Keys = [NoteKey], CharactersDirectory = plan.Logout!.CharactersDirectory,
            WriteTimeout = TimeSpan.FromSeconds(plan.Logout.WriteSeconds), RereadInterval = session.Interval,
        };
        string key = plan.GlobalKey!;
        new ClientRounds
        {
            Client = client, WorldUid = plan.WorldUid, Report = report, Output = session.Output, OwnedServer = session.Server!, Arrival = CampaignSteps.At(plan.Arrival), ArriveStep = "arrive beside the marker",
            Cancellation = session.Cancellation,
        }.Run(session.Server!.Game, () => session.OpenClient(client),
            measure: round =>
            {
                round.Step("the client sees the marker at the dry site", () => DrySiteScenario.RequireClientMarkers(round.Client, plan.DrySite, 1));
                if (round.Index == 0) First(session, plan, round, zoneCycle, key, timeout);
                else AfterRestart(session, plan, round, logout, key, timeout);
            },
            afterRestart: round => round.Step("the server still has one marker at the dry site, none at the wet site",
                () => CampaignSteps.RequireMarkers(round.Server, plan, dry: 1)));
    }

    private static void First(GameSession session, AcceptancePlan plan, ClientRound round, ZoneCycle zoneCycle, string key, TimeSpan timeout)
    {
        var control = plan.Control;
        round.Step("the marker carries MyMod's saved label on the client", () => CampaignSteps.RequireLabelledMarker(round.Client, plan.DrySite));

        // #23: a known progression state, set once on the server and seen on the client.
        round.Step($"the fixture world does not have {key} yet", () =>
        {
            if (GlobalKeyFixture.Read(round.Server, Capabilities.GlobalKeys).Any(line => line == key || line.StartsWith(key + " ", StringComparison.Ordinal)))
                throw new InvalidOperationException($"The fixture world already has {key}: setting it would prove nothing. Use a world without it, or another key.");
        });
        round.Step($"set {key} on the server; the client lists the server's keys", () =>
        {
            var keys = GlobalKeyFixture.Apply(round.Server, round.Client, Capabilities.GlobalKeyChange, Capabilities.GlobalKeys, [key], [], timeout, session.Interval,
                cancellation: session.Cancellation).GetAwaiter().GetResult();
            round.Write("global-keys", new { set = key, keys });
        });

        // #24: a vanilla dungeon near the player, generated by now or soon after.
        round.Step("the dungeon's saved rooms lie in its location's zone", () => Dungeon(session, plan, round, timeout));

        // #35: leave the area and come back; the marker's saved label survives, and a field-only value would not.
        string? fieldValue = null;
        if (control?.Name == ControlPlugins.FieldOnlyState)
            round.Step($"control {control.Name}: keep a value only in a component field on the marker", () =>
            {
                fieldValue = CampaignSteps.RunWord("field");
                session.Report.Provenance["fieldOnlyValue"] = fieldValue;
                var data = round.Client.Invoke(round.Client.RequireCapability(Capabilities.FieldStateSet),
                    CampaignSteps.Number(plan.DrySite.X), CampaignSteps.Number(plan.DrySite.Z), fieldValue);
                if (data.GetProperty("markers").GetInt32() != 1) throw new InvalidOperationException("The control found no single marker to keep its value on: " + data.GetRawText());
            });
        zoneCycle.Run(round, session.Cancellation);
        round.Step("after the zone reload the client has the marker again, with its saved label", () => CampaignSteps.RequireLabelledMarker(round.Client, plan.DrySite));
        if (control?.Name == ControlPlugins.FieldOnlyState)
        {
            ControlPlugins.ExpectFailure(session.Report, control, () =>
            {
                var data = round.Client.ObserveComplete(round.Client.RequireCapability(Capabilities.FieldStateRead), "field-only-state",
                    CampaignSteps.Number(plan.DrySite.X), CampaignSteps.Number(plan.DrySite.Z)).Data;
                round.Write("field-only-state", data);
                var values = data.GetProperty("values").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : null).ToArray();
                if (values.Length != 1 || values[0] != fieldValue)
                    throw new InvalidOperationException($"The field-only value on the marker did not survive the zone reload: it was \"{fieldValue}\", now [{string.Join(", ", values.Select(v => v ?? "none"))}].");
            }, round.Name + ": ");
            throw new ControlConcluded(control);
        }
    }

    private static void AfterRestart(GameSession session, AcceptancePlan plan, ClientRound round, LogoutCycle logout, string key, TimeSpan timeout)
    {
        var control = plan.Control;
        round.Step($"the server kept {key} through the save and restart, and the client lists it", () =>
        {
            var keys = GlobalKeyFixture.WaitForClient(round.Server, round.Client, Capabilities.GlobalKeys, timeout, session.Interval, cancellation: session.Cancellation).GetAwaiter().GetResult();
            if (!keys.Contains(key, StringComparer.Ordinal)) throw new InvalidOperationException($"The server lost {key} across the save and restart: [{string.Join(", ", keys)}].");
        });

        // #35: a value this run writes, so what comes back cannot be left from an earlier session.
        string note = CampaignSteps.RunWord("note");
        session.Report.Provenance["logoutNote"] = note;
        round.Step("MyMod writes this run's note on the player's custom data", () =>
        {
            var reply = round.Client.Execute("mymod_note " + note);
            if (!reply.Output.Contains("OK: note " + note)) throw new InvalidOperationException("MyMod did not confirm the note: " + string.Join(" | ", reply.Output));
        });
        if (control?.Name == ControlPlugins.SuppressedProfileSave)
        {
            // The steps of the cycle stay inside the one expected failure: the client is then at its menu, and the run ends.
            ControlPlugins.ExpectFailure(session.Report, control, () => logout.Run(round.Client, plan.Client!, plan.WorldUid, session.Cancellation), round.Name + ": ");
            throw new ControlConcluded(control);
        }
        var result = logout.Run(round, plan.Client!, plan.WorldUid, session.Cancellation);
        round.Step("the note that came back is this run's", () =>
        {
            if (!result.After.Values.TryGetValue(NoteKey, out var back) || back != note)
                throw new InvalidOperationException($"The character came back with {NoteKey}=\"{back}\", not this run's \"{note}\".");
        });
    }

    // Waits for a dungeon generator near the declared location (the server creates it when it generates the zone, which
    // the player's arrival nearby causes), then checks every one found.
    private static void Dungeon(GameSession session, AcceptancePlan plan, ClientRound round, TimeSpan timeout)
    {
        var site = plan.Dungeon!;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<SavedDungeon> dungeons;
        while ((dungeons = DungeonRooms.Read(round.Server, Capabilities.DungeonRooms, site.X, site.Z, site.Radius)).Count == 0)
        {
            if (clock.Elapsed >= timeout)
                throw new WaitTimeoutException($"a dungeon generator within {site.Radius} m of ({site.X}, {site.Z})", clock.Elapsed,
                    "none: the server has not generated that zone, or no dungeon stands there (find one with cli_world_dump's locations)");
            session.Cancellation.WaitHandle.WaitOne(session.Interval);
            session.Cancellation.ThrowIfCancellationRequested();
        }
        round.Write("dungeon-rooms", dungeons.Select(d => new
        {
            d.Prefab, d.Uid, d.Format, d.X, d.Y, d.Z, d.ZoneX, d.ZoneZ, d.CustomInterior, d.Location, rooms = d.Rooms,
            interiorOffset = d.InteriorOffset is { } offset ? new { x = offset.X, y = offset.Y, z = offset.Z } : null,
        }).ToArray());
        session.Report.Provenance["dungeonInteriorOffset"] = string.Join("; ", dungeons.Select(d => d.Prefab + " " +
            (d.InteriorOffset is { } o ? string.Create(CultureInfo.InvariantCulture, $"({o.X:0.##}, {o.Y:0.##}, {o.Z:0.##})") : "no location")));
        foreach (var dungeon in dungeons) DungeonRooms.RequireWithinZone(dungeon);
    }

    // The control's patch must be missing from the census (#30), and the teardown log scan's defaults must fail on the server's
    // own log (#26). On the 1.0.16 dedicated server (BepInEx 5.4.23.5, HarmonyX 2.9.0) the missing target leaves one
    // accesstools-not-found warning and no error line, and that pattern fails by default. The plan names the control's line as
    // expected, so the teardown scan counts it apart rather than failing the run on it a second time. Whether PatchAll then
    // returned or threw is recorded (controlPatchAllReturned), and fails nothing.
    private static void MissingTarget(GameSession session, AcceptancePlan plan, ControlPlugin control)
    {
        ControlPlugins.ExpectFailure(session.Report, control, () =>
            HarmonyCensus.Read(session.Server!.Game, Capabilities.Harmony, control.Guid).Check(control.Guid, [ControlPlugins.MissingPatch]).RequireApplied());
        ControlPlugins.ExpectFailure(session.Report, control, ControlPlugins.ScanCheck, ControlPlugins.MissingMethodName, "controlScanFailure", () =>
        {
            string log = session.Server!.LiveLog ?? throw new InvalidOperationException("This run cannot read the server's live log; run the control on the server's machine.");
            var scan = LogScanner.Scan(new RunLog("server BepInEx log (this boot, live)", log, Required: true));
            File.WriteAllText(Path.Combine(session.Output, "control-log-scan.json"), JsonSerializer.Serialize(scan, new JsonSerializerOptions { WriteIndented = true }));
            if (scan.Problem != null) throw new InvalidOperationException("The server's log: " + scan.Problem);
            // The scan keeps only each pattern's first line, and another plugin's lookup may come before the control's: read them all.
            var lines = ReadLines(log);
            var notFound = LogScanner.Patterns.Single(pattern => pattern.Name == "accesstools-not-found").Line;
            string? warning = lines.FirstOrDefault(line => notFound.IsMatch(line) && line.Contains(ControlPlugins.MissingMethodName, StringComparison.Ordinal));
            // The control logs a line right after PatchAll: present, PatchAll returned; absent, it threw. Recorded either way.
            session.Report.Provenance["controlPatchAllReturned"] = lines.Any(line => line.Contains(ControlPlugins.PatchAllReturnedLine, StringComparison.Ordinal)) ? "true" : "false";
            if (warning != null) session.Report.Provenance["controlLogLine"] = warning;
            var failures = scan.Counts.Where(count => count.Severity == LogSeverity.Failure && count.Count > 0)
                .Select(count => $"{count.Pattern} x{count.Count}, first at line {count.FirstLine}: {count.First}").ToList();
            if (failures.Count == 0) return;
            // The failure names the control's method only when accesstools-not-found failed and the control's own line is among them.
            bool own = warning != null && scan.Counts.Any(count => count.Pattern == "accesstools-not-found" && count.Severity == LogSeverity.Failure && count.Count > 0);
            throw new InvalidOperationException("The log scan fails: " + string.Join(" | ", failures) + (own ? $"; the control's line: {warning}" : "; none of it is the control's line"));
        });
    }

    // A live log the server is still writing.
    private static List<string> ReadLines(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var lines = new List<string>();
        for (string? line; (line = reader.ReadLine()) != null;) lines.Add(line);
        return lines;
    }
}
