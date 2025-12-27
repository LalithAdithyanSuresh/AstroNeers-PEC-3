using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;

public class CameraStreamer : MonoBehaviour
{
    [Header("Server Settings")]
    public string serverUrl = "http://localhost:6000/detect";
    
    [Header("Capture Settings")]
    [Tooltip("Width of the image sent to YOLO (Lower = Faster)")]
    public int width = 640;
    [Tooltip("Height of the image sent to YOLO")]
    public int height = 480;
    [Tooltip("How many times per second to send data per camera")]
    public float targetFPS = 10f;

    [Header("Debug")]
    public bool showDebugLogs = true;

    // Internal list of cameras found on this object or its children
    private Camera[] cameras;
    private Dictionary<int, float> lastSendTime = new Dictionary<int, float>();

    void Start()
    {
        // 1. Find all cameras attached to this GameObject or its children
        cameras = GetComponentsInChildren<Camera>();
        
        if (cameras.Length == 0)
        {
            Debug.LogError($"[CameraStreamer] No cameras found under {gameObject.name}!");
            return;
        }

        Debug.Log($"[CameraStreamer] Found {cameras.Length} cameras. Starting stream to {serverUrl}...");

        // Initialize timers
        for (int i = 0; i < cameras.Length; i++)
        {
            lastSendTime[i] = 0f;
            // Start a separate coroutine for each camera
            StartCoroutine(CaptureAndSend(i));
        }
    }

    IEnumerator CaptureAndSend(int camIndex)
    {
        Camera cam = cameras[camIndex];
        
        // Create a temporary Render Texture for capturing
        RenderTexture rt = new RenderTexture(width, height, 24);
        Texture2D screenShot = new Texture2D(width, height, TextureFormat.RGB24, false);
        
        float interval = 1f / targetFPS;

        while (true)
        {
            yield return new WaitForSeconds(interval);

            // 2. Capture the Frame
            RenderTexture prevRT = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;

            cam.targetTexture = rt;
            cam.Render();
            
            RenderTexture.active = rt;
            screenShot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            screenShot.Apply();

            // Restore previous settings so we don't break the game view
            RenderTexture.active = prevActive;
            cam.targetTexture = prevRT;

            // 3. Encode to JPG
            byte[] bytes = screenShot.EncodeToJPG(50); // 50% quality for speed

            // 4. Send to Python Server
            StartCoroutine(PostImage(camIndex, bytes));
        }
    }

    IEnumerator PostImage(int id, byte[] imageBytes)
    {
        WWWForm form = new WWWForm();
        form.AddField("camera_id", id);
        form.AddBinaryData("file", imageBytes, $"cam_{id}.jpg", "image/jpeg");

        using (UnityWebRequest www = UnityWebRequest.Post(serverUrl, form))
        {
            // Send
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                // If 429 (Too Many Requests), it means server omitted the frame. That's fine.
                if (www.responseCode == 429)
                {
                    if (showDebugLogs) Debug.LogWarning($"[Cam {id}] Frame dropped by server (Processing busy).");
                }
                else
                {
                    Debug.LogError($"[Cam {id}] Error: {www.error}");
                }
            }
            else
            {
                if (showDebugLogs) Debug.Log($"[Cam {id}] Success: {www.downloadHandler.text}");
                // Here you can parse the JSON to get Rock IDs and draw them in Unity if needed
            }
        }
    }

    // Cleanup memory
    void OnDestroy()
    {
        StopAllCoroutines();
    }
}