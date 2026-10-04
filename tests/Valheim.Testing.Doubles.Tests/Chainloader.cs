using BepInEx;
using UnityEngine;

// As BepInEx's chainloader loads plugins: every plugin is added to one manager object, so its Awake runs at once and a
// plugin finds its siblings with GetComponent.
internal sealed class Chainloader
{
    private GameObject? _manager;
    public T Load<T>() where T : BaseUnityPlugin => (_manager ??= new GameObject("BepInEx_Manager")).AddComponent<T>();
}
