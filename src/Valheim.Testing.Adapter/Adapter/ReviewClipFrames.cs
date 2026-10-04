// Source compiled into a mod's game-side test adapter. This is a scene-only frame source for a bounded private clip,
// not a desktop recorder. The host owns fetching, validation and deletion after a failed run.
#if VALHEIM_TESTING_REVIEW_CLIP
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using valheimCLI.Extensions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// Renders a short sequence from a separate scene camera into private PNG files. The camera follows the game's
    /// camera each frame, but cannot draw screen-space canvases because it is not their camera. World-space canvas
    /// layers are excluded as well; the resulting frames may omit world objects on those layers. This is an opt-in
    /// human-review source, not evidence that the view is visually correct.
    /// </summary>
    public static class ReviewClipFrames
    {
        private static string? _activeDirectory;
        private const int MaxBytes = 24 * 1024 * 1024;

        /// <summary>Capture &lt;review-id&gt; &lt;new-absolute-directory&gt; &lt;width&gt; &lt;height&gt; &lt;fps&gt; &lt;frames&gt;.</summary>
        public static ExtensionCommand Command(string name = "review-clip-frames") => new ExtensionCommand(name,
            "Render bounded world-only PNG frames: <review-id> <new-absolute-directory> <width> <height> <fps> <frames>",
            Capture, role: ExtensionRole.Client, needsWorld: true);

        private static IEnumerator Capture(ExtensionContext context)
        {
            if (context.Arguments.Count != 6 || !Id(context.Arguments[0]) ||
                !int.TryParse(context.Arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out int width) ||
                !int.TryParse(context.Arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out int height) ||
                !int.TryParse(context.Arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out int fps) ||
                !int.TryParse(context.Arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out int frames) ||
                width is < 160 or > 640 || height is < 90 or > 360 || fps is < 2 or > 10 ||
                frames is < 2 or > 60 || frames > fps * 10)
            {
                context.Fail("usage", "Use a review id, a new absolute no-space directory, 160-640 by 90-360 pixels, 2-10 fps and 2-60 frames over at most 10 seconds.");
                yield break;
            }
            string id = context.Arguments[0];
            string directory = context.Arguments[1];
            if (!Path.IsPathRooted(directory) || directory.Any(char.IsWhiteSpace) ||
                directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "." or "..") ||
                Directory.Exists(directory) || File.Exists(directory))
            {
                context.Fail("path", "The capture directory must be new, absolute, private and without spaces or traversal segments.");
                yield break;
            }
            if (!ReviewState.Owns(id) || Player.m_localPlayer == null || GameCamera.instance == null || ZNet.instance == null)
            {
                context.Fail("not_ready", "Begin the matching review-state lease in a loaded client world before capturing.");
                yield break;
            }
            if (_activeDirectory != null)
            {
                context.Fail("busy", "Another clip capture is active.");
                yield break;
            }

            GameObject? cameraObject = null;
            RenderTexture? render = null;
            Texture2D? pixels = null;
            bool success = false;
            int bytes = 0;
            var rows = new List<string> { "frame,elapsed_ms,bytes,sha256" };
            float started = Time.realtimeSinceStartup;
            try
            {
                Directory.CreateDirectory(directory);
                _activeDirectory = directory;
                var source = GameCamera.instance.GetComponent<Camera>();
                if (source == null) throw new InvalidOperationException("The game's scene camera is missing.");
                cameraObject = new GameObject("ValheimTesting.WorldClipCamera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.CopyFrom(source);
                camera.enabled = false;
                render = new RenderTexture(width, height, 24);
                camera.targetTexture = render;
                pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
                for (int index = 0; index < frames; index++)
                {
                    if (context.Cancelled || !ReviewState.Owns(id) || Player.m_localPlayer == null || GameCamera.instance == null)
                        throw new OperationCanceledException("Review ownership or the client world ended during capture.");
                    source = GameCamera.instance.GetComponent<Camera>();
                    if (source == null) throw new InvalidOperationException("The game's scene camera disappeared.");
                    // A separate camera has no screen-space camera canvas assigned to it. Also remove every layer used
                    // by an active world-space canvas, even when a mod put that canvas on an unexpected layer.
                    int mask = source.cullingMask & ~LayerMask.GetMask("UI", "UI3D");
                    foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                        if (canvas.renderMode == RenderMode.WorldSpace) mask &= ~(1 << canvas.gameObject.layer);
                    camera.cullingMask = mask;
                    camera.transform.position = source.transform.position;
                    camera.transform.rotation = source.transform.rotation;
                    RenderTexture? previous = RenderTexture.active;
                    try
                    {
                        RenderTexture.active = render;
                        camera.Render();
                        pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                        pixels.Apply(false);
                    }
                    finally { RenderTexture.active = previous; }
                    byte[] png = pixels.EncodeToPNG();
                    bytes = checked(bytes + png.Length);
                    if (png.Length > 2 * 1024 * 1024 || bytes > MaxBytes)
                        throw new InvalidDataException("The bounded clip exceeded its frame or total byte limit.");
                    string name = "frame-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".png";
                    File.WriteAllBytes(Path.Combine(directory, name), png);
                    string hash;
                    using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(png)).Replace("-", "").ToLowerInvariant();
                    rows.Add(index.ToString(CultureInfo.InvariantCulture) + "," +
                        Math.Round((Time.realtimeSinceStartup - started) * 1000f).ToString(CultureInfo.InvariantCulture) + "," +
                        png.Length.ToString(CultureInfo.InvariantCulture) + "," + hash);
                    if (index + 1 < frames)
                    {
                        float nextFrame = started + (index + 1f) / fps;
                        while (Time.realtimeSinceStartup < nextFrame)
                        {
                            if (context.Cancelled || !ReviewState.Owns(id))
                                throw new OperationCanceledException("Review ownership ended during capture.");
                            yield return null;
                        }
                    }
                }
                if (context.Cancelled) throw new OperationCanceledException("The capture request was cancelled.");
                File.WriteAllLines(Path.Combine(directory, "frames.csv"), rows, Encoding.ASCII);
                success = true;
                context.Succeed(new Dictionary<string, object?>
                {
                    ["source"] = "scene-only-frames", ["complete"] = true, ["id"] = id, ["directory"] = directory,
                    ["width"] = width, ["height"] = height, ["fpsRequested"] = fps, ["frames"] = frames,
                    ["elapsedMs"] = Math.Round((Time.realtimeSinceStartup - started) * 1000f), ["bytes"] = bytes,
                });
            }
            finally
            {
                if (pixels != null) UnityEngine.Object.Destroy(pixels);
                if (render != null) UnityEngine.Object.Destroy(render);
                if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject);
                if (!success && Directory.Exists(directory)) Directory.Delete(directory, true);
                _activeDirectory = null;
            }
        }

        /// <summary>Delete only this capture's incomplete frame directory when the adapter is unloaded.</summary>
        public static void AbortOnUnload()
        {
            string? path = _activeDirectory;
            _activeDirectory = null;
            if (path != null && Directory.Exists(path)) Directory.Delete(path, true);
        }

        private static bool Id(string value) => value.Length is > 0 and <= 64 && value.All(c =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }
}
#endif
