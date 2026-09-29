using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// Saved dungeon rooms against hand-built roomData bytes and scripted replies shaped like the adapter's DungeonRooms. The
// location stands at (100, 30, -40): zone floor((100 + 32) / 64) = 2, floor((-40 + 32) / 64) = -1, whose centre is
// (128, -64), so its rooms must lie within x 96..160 and z -96..-32.
public class DungeonRoomsTests
{
    private const string Path = "mymod.testing/dungeon-rooms", Corridor = "sunkencrypt_corridor", Hall = "sunkencrypt_hall";
    private static readonly Dictionary<string, RoomSize> Sizes = new() { [Corridor] = new(8, 4, 8), [Hall] = new(4, 6, 20) };

    private static byte[] RoomData(params (string Name, float X, float Z, float Yaw)[] rooms)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(rooms.Length);
            foreach (var room in rooms)
            {
                writer.Write(StableHash.Of(room.Name));
                writer.Write(room.X); writer.Write(5030f); writer.Write(room.Z);
                writer.Write(0f); writer.Write(room.Yaw); writer.Write(0f);
            }
        }
        return stream.ToArray();
    }

    private static object Location => new { prefab = "SunkenCrypt4", x = 100f, y = 30f, z = -40f, zoneX = 2, zoneZ = -1 };
    private static object Dungeon(byte[]? roomData, float x = 100f, int zoneX = 2, object? location = null, string format = "roomData", object[]? legacy = null, int? rooms = null) => new
    {
        prefab = "DG_SunkenCrypt", uid = "123:45", x, y = 5030f, z = -40f, zoneX, zoneZ = -1, customInterior = false, format,
        roomData = roomData == null ? null : Convert.ToBase64String(roomData), rooms, legacyRooms = legacy ?? Array.Empty<object>(),
        location = location ?? Location,
    };
    private static object Reply(params object[] dungeons) => new { source = "dungeon-rooms", complete = true, x = 100f, z = -40f, radius = 64f, zoneSize = 64f, dungeons };
    private static SavedDungeon One(object dungeon) => Assert.Single(DungeonRooms.Parse(Json(Reply(dungeon))));

    [Fact] public void RoomDataDecodesAsTheGameWritesIt()
    {
        var rooms = DungeonRooms.ParseRoomData(RoomData((Corridor, 128, -64, 0), (Hall, 150.5f, -70, 90)));
        Assert.Equal(2, rooms.Count);
        Assert.Equal(new DungeonRoom(1, StableHash.Of(Hall), 150.5f, 5030, -70, 0, 90, 0), rooms[1]);
        Assert.Empty(DungeonRooms.ParseRoomData(RoomData()));
    }

    [Fact] public void MalformedRoomDataIsRefused()
    {
        byte[] good = RoomData((Corridor, 128, -64, 0));
        Assert.Throws<InvalidDataException>(() => DungeonRooms.ParseRoomData(good[..^1]));
        Assert.Throws<InvalidDataException>(() => DungeonRooms.ParseRoomData([.. good, 0]));
        Assert.Throws<InvalidDataException>(() => DungeonRooms.ParseRoomData([0, 0]));
        Assert.Throws<InvalidDataException>(() => DungeonRooms.ParseRoomData(BitConverter.GetBytes(-1)));
    }

    [Fact] public void RoomsInsideTheLocationsZonePassAndTheInteriorOffsetIsReported()
    {
        var transport = new ScriptedTransport().Extension("mymod.testing", "dungeon-rooms", args =>
        {
            Assert.Equal(new[] { "100", "-40", "32" }, args);
            return Reply(Dungeon(RoomData((Corridor, 128, -64, 0), (Corridor, 156, -36, 0), (Hall, 150, -64, 0))));
        });
        using var server = transport.Actor("server");
        var dungeon = Assert.Single(DungeonRooms.Read(server, Path, 100, -40, 32));
        Assert.Equal(("DG_SunkenCrypt", "roomData", 3), (dungeon.Prefab, dungeon.Format, dungeon.Rooms.Count));
        Assert.Equal((0f, 5000f, 0f), dungeon.InteriorOffset);
        Assert.Empty(DungeonRooms.OutsideZone(dungeon, Sizes));
        DungeonRooms.RequireWithinZone(dungeon, Sizes);
    }

    [Fact] public void ARoomOutsideTheZoneFailsNamingIt()
    {
        // Negative control: a corridor centred 2 m inside the edge whose 8 m footprint reaches 2 m past it.
        var dungeon = One(Dungeon(RoomData((Corridor, 128, -64, 0), (Corridor, 158, -64, 0))));
        var problem = Assert.Single(DungeonRooms.OutsideZone(dungeon, Sizes));
        Assert.Equal((1, StableHash.Of(Corridor)), (problem.Room, problem.Hash));
        var error = Assert.Throws<InvalidOperationException>(() => DungeonRooms.RequireWithinZone(dungeon, Sizes));
        Assert.Contains($"room 1 ({StableHash.Of(Corridor)}): {Corridor} at (158, -64) reaches 2 m past zone (2, -1) [x 96..160, z -96..-32]", error.Message);
        // Without its size only the centre is checked, which cannot see it.
        Assert.Empty(DungeonRooms.OutsideZone(dungeon));
        Assert.Contains("centre only", Assert.Single(DungeonRooms.OutsideZone(One(Dungeon(RoomData((Corridor, 161, -64, 0))))).Reason));
    }

    [Fact] public void TheFootprintTurnsWithTheRoomsYaw()
    {
        // A 4 x 20 m hall centred 8 m inside the x edge: lengthwise along z it fits, turned 90 degrees it reaches 2 m past.
        Assert.Empty(DungeonRooms.OutsideZone(One(Dungeon(RoomData((Hall, 152, -64, 0)))), Sizes));
        var turned = Assert.Single(DungeonRooms.OutsideZone(One(Dungeon(RoomData((Hall, 152, -64, 90)))), Sizes));
        Assert.Contains("reaches 2 m past", turned.Reason);
        Assert.Empty(DungeonRooms.OutsideZone(One(Dungeon(RoomData((Hall, 152, -64, 180)))), Sizes));
    }

    [Fact] public void ADungeonWithoutALocationRoomsOrItsOwnZoneFails()
    {
        var moved = One(Dungeon(RoomData((Corridor, 190, -64, 0)), x: 190, zoneX: 3));
        var problems = DungeonRooms.OutsideZone(moved, Sizes).Select(p => p.ToString()).ToArray();
        Assert.Contains(problems, p => p.Contains("stands in zone (3, -1), not its location's (2, -1)"));
        Assert.Contains(problems, p => p.Contains("reaches 34 m past"));
        Assert.Contains("has no saved rooms (format none)", Assert.Single(DungeonRooms.OutsideZone(One(Dungeon(null, format: "none")))).Reason);
        var orphan = DungeonRooms.Parse(Json(Reply(new
        {
            prefab = "DG_SunkenCrypt", uid = "1:2", x = 100f, y = 5030f, z = -40f, zoneX = 2, zoneZ = -1, customInterior = false, format = "none",
            roomData = (string?)null, rooms = (int?)null, legacyRooms = Array.Empty<object>(), location = (object?)null,
        })))[0];
        Assert.Null(orphan.InteriorOffset);
        Assert.Contains("keeps no location", Assert.Single(DungeonRooms.OutsideZone(orphan)).Reason);
    }

    [Fact] public void LegacyRoomFieldsAreReadWhenThereIsNoRoomData()
    {
        var legacy = new object[] { new { hash = StableHash.Of(Corridor), x = 128f, y = 5030f, z = -64f, rx = 0f, ry = 270f, rz = 0f } };
        var dungeon = One(Dungeon(null, format: "legacy", legacy: legacy, rooms: 1));
        Assert.Equal(new DungeonRoom(0, StableHash.Of(Corridor), 128, 5030, -64, 0, 270, 0), Assert.Single(dungeon.Rooms));
        Assert.Throws<InvalidOperationException>(() => One(Dungeon(null, format: "legacy", legacy: legacy, rooms: 2)));
    }

    [Fact] public void AnObservationThatIsNotTheGamesIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => DungeonRooms.Parse(Json(new { source = "dungeon-rooms", complete = false, dungeons = Array.Empty<object>() })));
        Assert.Throws<InvalidOperationException>(() => DungeonRooms.Parse(Json(new { source = "dungeon-rooms", complete = true, zoneSize = 32f, dungeons = Array.Empty<object>() })));
        Assert.Throws<InvalidOperationException>(() => One(Dungeon(null, format: "roomData")));
        Assert.Throws<InvalidOperationException>(() => One(Dungeon(null, format: "zdo")));
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
