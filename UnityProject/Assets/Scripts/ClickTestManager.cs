using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem; // ✅ New Input System
using System.IO;
using System.Text;

public class ClickTestManager : MonoBehaviour
{
    [Header("References")]
    public Camera mainCamera;                   // Drag your Main Camera here (or it will auto-find)
    public LayerMask clickableLayer;            // Set this to the Clickable layer

    [Header("Test flow")]
    public List<ClickableObject> clickableObjects = new List<ClickableObject>(); // Add objects here or leave empty
    public float clickTimeLimitSeconds = 3f;    // Time limit per object before timeout
    public bool despawnAfterClick = true;       // ✅ Option: make object disappear after click

    private int currentIndex = -1;
    private float activationTimestamp = 0f;
    private List<LoggedRow> sessionRows = new List<LoggedRow>();

    void Awake()
    {
        if (mainCamera == null)
            mainCamera = Camera.main;

        // Subscribe to static click event
        ClickableObject.OnClickedStatic += OnObjectClicked_Static;
    }

    void Start()
    {
        // Auto-find clickable objects if none are assigned
        if (clickableObjects == null || clickableObjects.Count == 0)
        {
            ClickableObject[] found = FindObjectsOfType<ClickableObject>();
            foreach (var c in found)
                clickableObjects.Add(c);
        }

        // Deactivate all at start
        foreach (var c in clickableObjects)
            c.Deactivate();

        // Start the first one
        if (clickableObjects.Count > 0)
            ActivateIndex(0);
    }

    void OnDestroy()
    {
        ClickableObject.OnClickedStatic -= OnObjectClicked_Static;
    }

    void Update()
    {
        // ✅ Use new Input System
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Vector2 mousePos = Mouse.current.position.ReadValue();
            Ray ray = mainCamera.ScreenPointToRay(mousePos);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit, 100f, clickableLayer))
            {
                var clicked = hit.collider.GetComponent<ClickableObject>();
                if (clicked != null && clicked.isActive)
                {
                    float timeSinceActivation = Time.time - activationTimestamp;
                    clicked.RegisterClick(mousePos, timeSinceActivation, mainCamera);
                }
            }
        }

        // Timeout handling
        if (currentIndex >= 0 && currentIndex < clickableObjects.Count)
        {
            if ((Time.time - activationTimestamp) > clickTimeLimitSeconds)
            {
                Debug.Log($"Timeout on {clickableObjects[currentIndex].name}");
                LogResult(clickableObjects[currentIndex], null, timedOut: true);
                AdvanceIndex();
            }
        }
    }

    private void OnObjectClicked_Static(ClickableObject obj, ClickMetrics metrics)
    {
        // Verify it’s the correct object
        if (currentIndex < 0 || obj != clickableObjects[currentIndex])
            return;

        LogResult(obj, metrics, timedOut: false);

        // ✅ Optionally despawn the clicked object
        if (despawnAfterClick)
            obj.gameObject.SetActive(false);

        AdvanceIndex();
    }

    private void ActivateIndex(int idx)
    {
        if (idx < 0 || idx >= clickableObjects.Count) return;

        // Deactivate previous
        if (currentIndex >= 0 && currentIndex < clickableObjects.Count)
            clickableObjects[currentIndex].Deactivate();

        currentIndex = idx;
        clickableObjects[currentIndex].Activate();
        activationTimestamp = Time.time;
        Debug.Log($"Activated [{currentIndex}] {clickableObjects[currentIndex].name}");
    }

    private void AdvanceIndex()
    {
        if (currentIndex >= 0 && currentIndex < clickableObjects.Count)
            clickableObjects[currentIndex].Deactivate();

        currentIndex++;
        if (currentIndex < clickableObjects.Count)
        {
            ActivateIndex(currentIndex);
        }
        else
        {
            Debug.Log("✅ Test finished!");
            SaveSessionCsv();
        }
    }

    private void LogResult(ClickableObject obj, ClickMetrics metrics, bool timedOut)
    {
        var row = new LoggedRow
        {
            objectID = obj != null ? (string.IsNullOrEmpty(obj.objectID) ? obj.name : obj.objectID) : "NULL",
            shapeType = obj != null ? obj.shapeType : "NULL",
            timedOut = timedOut
        };

        if (timedOut || metrics == null)
        {
            row.timeMs = -1;
            row.offsetPx = -1;
            row.offsetNorm = -1;
            row.success = false;
            row.distanceToCamera = obj != null && mainCamera != null ? Vector3.Distance(mainCamera.transform.position, obj.transform.position) : -1;
            row.targetRadiusPx = obj != null && obj.GetComponent<Renderer>() != null ? obj.GetComponent<Renderer>().bounds.extents.magnitude : -1;
        }
        else
        {
            row.timeMs = metrics.timeMs;
            row.offsetPx = metrics.offsetPx;
            row.offsetNorm = metrics.offsetNorm;
            row.success = metrics.success;
            row.distanceToCamera = metrics.distanceToCamera;
            row.targetRadiusPx = metrics.targetRadiusPx;
        }

        sessionRows.Add(row);
        Debug.Log($"LOG: {row.objectID} timeMs={row.timeMs} offsetNorm={row.offsetNorm:F2} success={row.success}");
    }

    private void SaveSessionCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("objectID,shapeType,timeMs,offsetPx,offsetNorm,success,distanceToCamera,targetRadiusPx,timedOut");

        foreach (var r in sessionRows)
        {
            sb.AppendLine($"{r.objectID},{r.shapeType},{r.timeMs},{r.offsetPx},{r.offsetNorm},{r.success},{r.distanceToCamera},{r.targetRadiusPx},{r.timedOut}");
        }

        string path = Path.Combine(Application.persistentDataPath, "click_session.csv");
        try
        {
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"💾 Saved session CSV to: {path}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError("Failed to save CSV: " + ex.Message);
        }
    }

    // Internal log container
    private class LoggedRow
    {
        public string objectID;
        public string shapeType;
        public float timeMs;
        public float offsetPx;
        public float offsetNorm;
        public bool success;
        public float distanceToCamera;
        public float targetRadiusPx;
        public bool timedOut;
    }
}
