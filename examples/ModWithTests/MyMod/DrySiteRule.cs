using UnityEngine;

namespace MyMod;

// This is the example mod's implementation, not a test replacement.
// A real mod compiles it against Valheim; the test project links this same file.
internal static class DrySiteRule
{
    // Checks generator ground, not the proposed object's y or loaded/collider ground.
    internal static bool CanPlace(Vector3 position, float waterLevel, float clearance)
        => WorldGenerator.instance is { } world
            && world.GetHeight(position.x, position.z) >= waterLevel + clearance;
}
