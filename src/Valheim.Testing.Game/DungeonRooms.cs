using System.Globalization;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>
/// One saved dungeon room: its prefab-name hash (<see cref="StableHash.Of"/> of the room prefab's name), position and
/// rotation as Euler angles in degrees, as the dungeon generator saved them. <see cref="Index"/> is its place in the save.
/// </summary>
[ResultShape]
public sealed record DungeonRoom(int Index, int Hash, float X, float Y, float Z, float RotationX, float RotationY, float RotationZ);

/// <summary>
/// The location instance the server keeps nearest the queried ground position (in the 3 x 3 zones around it). The game keys
/// each location instance by the zone its own position stands in, so this is found from the query, not from the
/// generator's zone, and a generator displaced into a neighbouring zone still pairs with its location.
/// </summary>
[ResultShape]
public sealed record DungeonLocation(string? Prefab, float X, float Y, float Z, int ZoneX, int ZoneZ);

/// <summary>A room prefab's size (<c>Room.m_size</c>) in metres: x across, y up, z along the room's own axes.</summary>
public sealed record RoomSize(float X, float Y, float Z);

/// <summary>A reason a dungeon fails <see cref="DungeonRooms.OutsideZone"/>: a room (when <see cref="Room"/> is set) or the dungeon itself.</summary>
[ResultShape]
public sealed record DungeonRoomProblem(int? Room, int? Hash, string Reason)
{
    public override string ToString() => Room is int index ? $"room {index} ({Hash}): {Reason}" : Reason;
}

/// <summary>
/// A dungeon generator's saved state on the server. <see cref="Format"/> is <c>roomData</c> (the byte array Valheim 1.0
/// writes), <c>legacy</c> (the <c>room&lt;i&gt;</c> fields of older saves, which the game reads when <c>roomData</c> is
/// absent) or <c>none</c>. The generator stands at its location's position plus the location's rotation applied to the
/// generator's offset in the location prefab (typically about 5000 m up); <see cref="InteriorOffset"/> is that difference.
/// </summary>
[ResultShape]
public sealed record SavedDungeon(string Prefab, string Uid, float X, float Y, float Z, int ZoneX, int ZoneZ, bool CustomInterior, string Format,
    IReadOnlyList<DungeonRoom> Rooms, DungeonLocation? Location)
{
    /// <summary>The generator's position minus its location's, or null without a location.</summary>
    public (float X, float Y, float Z)? InteriorOffset => Location is { } l ? (X - l.X, Y - l.Y, Z - l.Z) : null;
}

/// <summary>
/// Reads a dungeon's saved rooms through an adapter's <c>DungeonRooms.Command()</c> (Valheim.Testing.Adapter) on the
/// server, and checks that every room lies inside its location's zone. In Valheim 1.0.16 the game gives a dungeon interior
/// its environment with a 64 m wide box centred on the location's zone (5000 m above the location), so the part of a room
/// outside that zone loses the interior environment; the generator itself keeps rooms inside the zone of its own position.
/// </summary>
public static class DungeonRooms
{
    public const string Source = "dungeon-rooms";
    /// <summary>Valheim's zone width in metres; zone (i, j) covers x in [64i - 32, 64i + 32] and z likewise.</summary>
    public const float ZoneSize = 64f;

    /// <summary>
    /// The dungeon generators saved within <paramref name="radius"/> metres (horizontally, at most 256) of
    /// (<paramref name="x"/>, <paramref name="z"/>), through <paramref name="capabilityPath"/> (for example
    /// <c>mymod.testing/dungeon-rooms</c>), each with the location nearest (x, z). Give the location's ground position. A
    /// dungeon has no saved generator until its zone has been generated, so an unvisited dungeon reads as an empty list.
    /// </summary>
    public static IReadOnlyList<SavedDungeon> Read(GameActor actor, string capabilityPath, float x, float z, float radius = 64)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!(radius > 0 && radius <= 256)) throw new ArgumentOutOfRangeException(nameof(radius), "0 < radius <= 256 m.");
        var observation = actor.ObserveComplete(actor.RequireCapability(capabilityPath), Source, Arg(x), Arg(z), Arg(radius));
        return Parse(observation.Data);
    }

    /// <summary>Parses the adapter's data, decoding each generator's rooms; anything malformed or inconsistent throws.</summary>
    public static IReadOnlyList<SavedDungeon> Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("source", out var source) || source.GetString() != Source ||
            !data.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Not a complete dungeon-room observation.");
        if (data.GetProperty("zoneSize").GetSingle() != ZoneSize)
            throw new InvalidOperationException("The game reports a zone size other than 64 m; these checks assume the game's.");
        var dungeons = new List<SavedDungeon>();
        foreach (var d in data.GetProperty("dungeons").EnumerateArray())
        {
            string format = d.GetProperty("format").GetString() ?? "";
            IReadOnlyList<DungeonRoom> rooms = format switch
            {
                "roomData" => ParseRoomData(Convert.FromBase64String(d.GetProperty("roomData").GetString() ?? throw new InvalidOperationException("roomData format without roomData."))),
                "legacy" => Legacy(d),
                "none" => Array.Empty<DungeonRoom>(),
                _ => throw new InvalidOperationException("Unknown dungeon save format: " + format),
            };
            DungeonLocation? location = null;
            if (d.GetProperty("location") is { ValueKind: JsonValueKind.Object } l)
                location = new(l.GetProperty("prefab").GetString(), l.GetProperty("x").GetSingle(), l.GetProperty("y").GetSingle(), l.GetProperty("z").GetSingle(),
                    l.GetProperty("zoneX").GetInt32(), l.GetProperty("zoneZ").GetInt32());
            dungeons.Add(new(d.GetProperty("prefab").GetString() ?? throw new InvalidOperationException("A dungeon without a prefab name."),
                d.GetProperty("uid").GetString() ?? throw new InvalidOperationException("A dungeon without a ZDO id."),
                d.GetProperty("x").GetSingle(), d.GetProperty("y").GetSingle(), d.GetProperty("z").GetSingle(), d.GetProperty("zoneX").GetInt32(), d.GetProperty("zoneZ").GetInt32(),
                d.GetProperty("customInterior").GetBoolean(), format, rooms, location));
        }
        return dungeons;
    }

    /// <summary>
    /// Decodes Valheim 1.0's <c>roomData</c> byte array: a little-endian int32 room count, then per room an int32 hash, the
    /// position as three float32 and the rotation as three float32 Euler angles. A negative count, a short array or bytes
    /// left over throw.
    /// </summary>
    public static IReadOnlyList<DungeonRoom> ParseRoomData(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        const int Entry = 4 + 6 * 4;
        using var reader = new BinaryReader(new MemoryStream(data, writable: false));
        if (data.Length < 4) throw new InvalidDataException("roomData is shorter than its room count.");
        int count = reader.ReadInt32();
        if (count < 0 || (long)count * Entry != data.Length - 4)
            throw new InvalidDataException($"roomData says {count} rooms but holds {data.Length - 4} bytes of rooms ({Entry} bytes each).");
        var rooms = new DungeonRoom[count];
        for (int i = 0; i < count; i++)
            rooms[i] = new(i, reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        return rooms;
    }

    /// <summary>
    /// Every reason <paramref name="dungeon"/>'s rooms are not all inside its location's zone: no location, no saved rooms,
    /// a generator in another zone, or a room reaching past the zone's edge by more than <paramref name="tolerance"/>
    /// metres. A room whose size is in <paramref name="sizes"/> (by room prefab name) is checked by its footprint turned by
    /// its yaw (rotation about y; pitch and roll are not applied); any other room by its centre only, which cannot see a
    /// room that is too large. Empty when every room is inside.
    /// </summary>
    public static IReadOnlyList<DungeonRoomProblem> OutsideZone(SavedDungeon dungeon, IReadOnlyDictionary<string, RoomSize>? sizes = null, float tolerance = .01f)
    {
        ArgumentNullException.ThrowIfNull(dungeon);
        if (!(tolerance >= 0)) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (dungeon.Location is not { } location) return [new(null, null, $"{dungeon.Prefab} {dungeon.Uid}: the server keeps no location near the queried position (generator in zone ({dungeon.ZoneX}, {dungeon.ZoneZ}))")];
        var problems = new List<DungeonRoomProblem>();
        if (dungeon.Rooms.Count == 0) problems.Add(new(null, null, $"{dungeon.Prefab} {dungeon.Uid} has no saved rooms (format {dungeon.Format})"));
        if (dungeon.ZoneX != location.ZoneX || dungeon.ZoneZ != location.ZoneZ)
            problems.Add(new(null, null, $"{dungeon.Prefab} {dungeon.Uid} stands in zone ({dungeon.ZoneX}, {dungeon.ZoneZ}), not its location's ({location.ZoneX}, {location.ZoneZ})"));
        var bySize = new Dictionary<int, (string Name, RoomSize Size)>();
        foreach (var entry in sizes ?? new Dictionary<string, RoomSize>())
            if (!bySize.TryAdd(StableHash.Of(entry.Key), (entry.Key, entry.Value))) throw new ArgumentException("Two room names with one hash: " + entry.Key, nameof(sizes));
        float minX = location.ZoneX * ZoneSize - ZoneSize / 2, maxX = location.ZoneX * ZoneSize + ZoneSize / 2;
        float minZ = location.ZoneZ * ZoneSize - ZoneSize / 2, maxZ = location.ZoneZ * ZoneSize + ZoneSize / 2;
        foreach (var room in dungeon.Rooms)
        {
            float halfX = 0, halfZ = 0;
            string name = room.Hash.ToString(CultureInfo.InvariantCulture);
            if (bySize.TryGetValue(room.Hash, out var known))
            {
                double yaw = room.RotationY * Math.PI / 180, cos = Math.Abs(Math.Cos(yaw)), sin = Math.Abs(Math.Sin(yaw));
                halfX = (float)(cos * known.Size.X / 2 + sin * known.Size.Z / 2);
                halfZ = (float)(sin * known.Size.X / 2 + cos * known.Size.Z / 2);
                name = known.Name;
            }
            float over = new[] { minX - (room.X - halfX), room.X + halfX - maxX, minZ - (room.Z - halfZ), room.Z + halfZ - maxZ }.Max();
            if (over > tolerance)
                problems.Add(new(room.Index, room.Hash, $"{name} at ({N(room.X)}, {N(room.Z)}) reaches {N(over)} m past zone ({location.ZoneX}, {location.ZoneZ}) " +
                    $"[x {N(minX)}..{N(maxX)}, z {N(minZ)}..{N(maxZ)}]" + (halfX == 0 && halfZ == 0 ? " (centre only; no size given)" : "")));
        }
        return problems;
    }

    /// <summary>Throws unless <see cref="OutsideZone"/> finds nothing, naming every problem.</summary>
    public static void RequireWithinZone(SavedDungeon dungeon, IReadOnlyDictionary<string, RoomSize>? sizes = null, float tolerance = .01f)
    {
        var problems = OutsideZone(dungeon, sizes, tolerance);
        if (problems.Count > 0)
            throw new InvalidOperationException($"{dungeon.Prefab} {dungeon.Uid}: {problems.Count} problem(s): " + string.Join("; ", problems) + ".");
    }

    private static DungeonRoom[] Legacy(JsonElement dungeon)
    {
        var rooms = dungeon.GetProperty("legacyRooms").EnumerateArray().Select((r, i) => new DungeonRoom(i, r.GetProperty("hash").GetInt32(),
            r.GetProperty("x").GetSingle(), r.GetProperty("y").GetSingle(), r.GetProperty("z").GetSingle(),
            r.GetProperty("rx").GetSingle(), r.GetProperty("ry").GetSingle(), r.GetProperty("rz").GetSingle())).ToArray();
        if (dungeon.GetProperty("rooms").GetInt32() != rooms.Length) throw new InvalidOperationException("A legacy dungeon whose room count and rooms differ.");
        return rooms;
    }

    private static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Arg(float value) => float.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(value), "A finite coordinate.");
}
