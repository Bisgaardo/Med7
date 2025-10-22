using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Collections;
using System.Text;

[System.Serializable]
class ClickRecord
{
    public int targetId;    // Which object was clicked
    public int misses;      // Number of wrong clicks before this target
    public float movementTime; // Movement time (seconds)
    public float indexDifficulty; // Fitts' law index of difficulty

    public ClickRecord(int targetId, int misses, float movementTime, float indexDifficulty)
    {
        this.targetId = targetId;
        this.misses = misses;
        this.movementTime = movementTime;
        this.indexDifficulty = indexDifficulty;
    }
}

struct CurrentTrial
{
    public int targetId;
    public Vector2 startMousePosition;
    public float startTime;
    public int misses;
    public ClickableObject targetObject;
}

public class ManagerScript : MonoBehaviour
{
    [SerializeField] private Camera mainCamera;
    [SerializeField] private string serverUrl = "http://localhost:3000/upload";

    private readonly List<int> targetSequence = new()
    {
        0, 14, 2, 4, 5, 6, 7, 8, 9, 10, 11
    };

    private int currentTargetIndex;
    private CurrentTrial currentTrial;
    private readonly List<ClickRecord> clickRecords = new();

    void Start()
    {
        mainCamera ??= Camera.main ?? FindObjectOfType<Camera>();
        HighlightNextTarget();
    }

    void Update()
    {
        if (Mouse.current.leftButton.wasPressedThisFrame)
            ProcessClick();
    }

    void ProcessClick()
    {
        if (!Physics.Raycast(mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue()), out RaycastHit hit))
        {
            currentTrial.misses++;
            return;
        }

        if (!hit.collider.TryGetComponent(out ClickableObject clickedObject))
        {
            currentTrial.misses++;
            return;
        }

        int expectedId = targetSequence[currentTargetIndex];

        if (clickedObject.objectId == expectedId)
        {
            Vector3 targetPos = clickedObject.GetScreenPosition(mainCamera);
            float targetWidth = clickedObject.GetDirectionalWidth(mainCamera, currentTrial.startMousePosition);
            float movementDistance = Vector2.Distance(currentTrial.startMousePosition, targetPos);

            var record = new ClickRecord(
                currentTrial.targetId,
                currentTrial.misses,
                Time.time - currentTrial.startTime,
                Mathf.Log(movementDistance / targetWidth + 1f) / Mathf.Log(2f)
            );

            clickRecords.Add(record);
            Debug.Log($"Target {record.targetId} | MT: {record.movementTime:F3}s | ID: {record.indexDifficulty:F3} | Misses: {record.misses}");

            currentTrial.targetObject?.ResetColor();
            currentTargetIndex++;

            if (currentTargetIndex >= targetSequence.Count)
                OnSequenceComplete();
            else
                HighlightNextTarget();
        }
        else
        {
            currentTrial.misses++;
        }
    }

    void OnSequenceComplete()
    {
        StartCoroutine(UploadResults());
        Debug.Log("Sequence complete. Uploading results...");
    }

    void HighlightNextTarget()
    {
        if (currentTargetIndex >= targetSequence.Count) return;

        int targetId = targetSequence[currentTargetIndex];
        currentTrial.startMousePosition = Mouse.current.position.ReadValue();
        currentTrial.startTime = Time.time;
        currentTrial.misses = 0;

        foreach (var obj in FindObjectsByType<ClickableObject>(FindObjectsSortMode.None))
        {
            if (obj.objectId == targetId)
            {
                obj.HighlightObject();
                currentTrial.targetId = targetId;
                currentTrial.targetObject = obj;
                break;
            }
        }
    }

    IEnumerator UploadResults()
    {
        StringBuilder sb = new();
        sb.AppendLine("TargetID,Misses,MovementTime,IndexDifficulty");

        foreach (var record in clickRecords)
            sb.AppendLine($"{record.targetId},{record.misses},{record.movementTime:F3},{record.indexDifficulty:F3}");

        byte[] bodyRaw = Encoding.UTF8.GetBytes(sb.ToString());
        Debug.Log($"Uploading Results ({bodyRaw.Length} bytes)");

        using UnityWebRequest req = new(serverUrl, "POST")
        {
            uploadHandler = new UploadHandlerRaw(bodyRaw),
            downloadHandler = new DownloadHandlerBuffer()
        };
        req.SetRequestHeader("Content-Type", "text/csv");

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
            Debug.Log("Results uploaded successfully");
        else
            Debug.LogError($"Upload failed: {req.error}");
    }
}
