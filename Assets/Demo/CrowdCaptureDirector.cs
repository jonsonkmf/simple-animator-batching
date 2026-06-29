using System.Collections;
using System.Collections.Generic;
using System.IO;
using SimpleAnimatorBatching;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Editor-only capture director for the portfolio demo video. Disables the ordinary wave demo,
/// ramps the crowd from 1 -> targetCount through the normal CrowdSpawner API, frames the camera over
/// the grid, draws an on-screen stats HUD, and writes a deterministic PNG frame sequence
/// (Time.captureFramerate) that is later stitched into an MP4. Auto-exits play mode when done.
///
/// The point of the ramp: as Instances and Triangles climb to 400, Batches / SetPass calls stay flat
/// and Visible Skinned Meshes stays 0 — the on-screen proof that the whole crowd is one batched draw.
/// </summary>
public class CrowdCaptureDirector : MonoBehaviour
{
    [Header("Crowd")]
    public int targetCount = 400;
    public int columns = 20;
    public float spacing = 1.3f;
    public string animatorState = "Taunt";
    public Vector2 speedRange = new Vector2(0.7f, 1.3f);

    [Header("Capture")]
    public int captureFps = 30;
    public int rampFrames = 150;   // ~5s to grow 1 -> targetCount
    public int holdFrames = 120;   // ~4s hold at targetCount
    public string outDir =
        @"C:\Users\evgen\AppData\Local\Temp\claude\C--Users-evgen-Simple-Animator-Batching\e38a75ba-f154-4153-bc4e-d6dcfed17ef1\scratchpad\frames";

    [Header("Camera")]
    public Vector3 cameraPos = new Vector3(0f, 10f, -20f);
    public Vector3 cameraLookAt = new Vector3(0f, 1.2f, 3f);
    public float fieldOfView = 52f;

    CrowdSpawner spawner;
    readonly List<CrowdInstanceHandle> live = new List<CrowdInstanceHandle>();
    int spawnedCount;
    int displayedCount;
    GUIStyle panelStyle;
    Texture2D panelTex;

    void Awake()
    {
        var wave = FindObjectOfType<CrowdWaveDemo>();
        if (wave != null) wave.enabled = false; // stop the old 40-instance churn demo
        spawner = FindObjectOfType<CrowdSpawner>();
    }

    IEnumerator Start()
    {
        if (spawner == null)
        {
            Debug.LogError("[CrowdCaptureDirector] No CrowdSpawner in scene.");
            yield break;
        }

        // Keep the editor player loop running at full speed even when the Unity window is not
        // focused — otherwise the Game view repaint is throttled to ~once every several seconds
        // and capture crawls. This is the key to fast offline capture via MCP.
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;

        spawner.WarmUp();
        FrameCamera();

        Directory.CreateDirectory(outDir);
        // Clear any stale frames from a previous run.
        foreach (var f in Directory.GetFiles(outDir, "*.png"))
            File.Delete(f);

        Time.captureFramerate = captureFps;

        int total = rampFrames + holdFrames;
        for (int frame = 0; frame < total; frame++)
        {
            int wanted = (frame >= rampFrames)
                ? targetCount
                : Mathf.Clamp(Mathf.CeilToInt(Mathf.Lerp(1f, targetCount, (float)frame / rampFrames)), 1, targetCount);

            while (spawnedCount < wanted)
                SpawnNext();
            displayedCount = spawnedCount;

            yield return new WaitForEndOfFrame();
            CaptureFrame(frame);
        }

        Time.captureFramerate = 0;
        Debug.Log($"[CrowdCaptureDirector] Captured {total} frames to {outDir}");
#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#endif
    }

    void SpawnNext()
    {
        int i = spawnedCount++;
        int rows = Mathf.CeilToInt((float)targetCount / columns);
        int col = i % columns;
        int row = i / columns;
        float x = (col - (columns - 1) * 0.5f) * spacing;
        float z = (row - (rows - 1) * 0.5f) * spacing;
        var handle = spawner.Spawn(new Vector3(x, 0f, z), Quaternion.identity);
        if (spawner.IsValid(handle) && spawner.TryGetAnimator(handle, out var a))
        {
            a.Play(animatorState, 0, Random.value);
            a.speed = Random.Range(speedRange.x, speedRange.y);
        }
        live.Add(handle);
    }

    void FrameCamera()
    {
        var cam = Camera.main;
        if (cam == null) return;
        cam.transform.position = cameraPos;
        cam.transform.LookAt(cameraLookAt);
        cam.fieldOfView = fieldOfView;
        cam.farClipPlane = 2000f;
    }

    void CaptureFrame(int frame)
    {
        var tex = ScreenCapture.CaptureScreenshotAsTexture();
        File.WriteAllBytes(Path.Combine(outDir, $"frame_{frame:D4}.png"), tex.EncodeToPNG());
        Destroy(tex);
    }

#if UNITY_EDITOR
    void OnGUI()
    {
        if (panelStyle == null)
        {
            panelTex = new Texture2D(1, 1);
            panelTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.62f));
            panelTex.Apply();
            panelStyle = new GUIStyle(GUI.skin.box) { normal = { background = panelTex } };
        }

        int drawCalls = UnityStats.drawCalls;
        int batches = UnityStats.batches;
        int setPass = UnityStats.setPassCalls;
        int vsm = UnityStats.visibleSkinnedMeshes;
        long tris = UnityStats.triangles;

        float scale = Mathf.Max(1f, Screen.height / 720f);
        float pad = 16f * scale;
        float w = 430f * scale;
        float h = 250f * scale;
        GUI.Box(new Rect(pad, pad, w, h), GUIContent.none, panelStyle);

        var title = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(26 * scale),
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        var row = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(22 * scale),
            normal = { textColor = Color.white }
        };
        var good = new GUIStyle(row) { normal = { textColor = new Color(0.4f, 1f, 0.5f) } };

        float x = pad * 2f;
        float y = pad * 1.6f;
        float lh = 30f * scale;
        GUI.Label(new Rect(x, y, w, lh), "Simple Animator Batching", title); y += lh * 1.4f;
        GUI.Label(new Rect(x, y, w, lh), $"Instances:            {displayedCount}", row); y += lh;
        GUI.Label(new Rect(x, y, w, lh), $"Triangles:            {tris:N0}", row); y += lh;
        GUI.Label(new Rect(x, y, w, lh), $"Visible Skinned Meshes:  {vsm}", vsm == 0 ? good : row); y += lh;
        GUI.Label(new Rect(x, y, w, lh), $"Batches (whole frame):   {batches}", row); y += lh;
        GUI.Label(new Rect(x, y, w, lh), $"SetPass calls:           {setPass}", row); y += lh;
        GUI.Label(new Rect(x, y, w, lh), $"Draw calls (whole frame): {drawCalls}", row);
    }
#endif
}
