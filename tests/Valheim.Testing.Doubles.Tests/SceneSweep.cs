using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

// Mod source as a consumer writes it (#154): it compiles here against the doubles and, in a mod, against the game. A
// deletion command selects live objects by enumerating the scene's GameObjects and checking a component by Type, which
// it reads from configuration. Its own component (Beacon) is mod code, not a double. Game types the doubles do not
// define (here none: ZNetView, Piece and ZNetScene are doubles) would be the consumer's own partial classes.
namespace ExampleMod
{
    public sealed class Beacon : MonoBehaviour { }

    public static class SceneSweep
    {
        /// <summary>The active scene objects named <paramref name="prefabName"/> (a copy's "(Clone)" suffix ignored) that carry a <paramref name="componentType"/>.</summary>
        public static List<GameObject> Select(string prefabName, Type componentType)
        {
            var selected = new List<GameObject>();
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                if (go.name.Replace("(Clone)", "") == prefabName && go.GetComponent(componentType) != null) selected.Add(go);
            return selected;
        }

        /// <summary>Destroys the selected objects through ZNetScene, as the game deletes networked objects; returns how many.</summary>
        public static int Delete(string prefabName, Type componentType)
        {
            var selected = Select(prefabName, componentType);
            foreach (var go in selected) ZNetScene.instance!.Destroy(go);
            return selected.Count;
        }
    }
}
