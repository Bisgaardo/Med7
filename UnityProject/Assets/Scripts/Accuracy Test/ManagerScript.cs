using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Collections;
using System.Text;

[System.Serializable]
class ClickRecord
{
    public int userAge;
    public string device;
    public int computerTime;
    public System.Guid uuid;
    public int targetObjectId;    // Which object was clicked
    public int misses;      // Number of wrong clicks before this target
    public float movementTime; // Movement time (seconds)
    public float indexDifficulty; // Fitts' law index of difficulty

    public ClickRecord(int userAge, System.Guid uuid, string device, int computerTime, int targetObjectId, int misses, float movementTime, float indexDifficulty)
    {
        this.userAge = userAge;
        this.device = device;
        this.computerTime = computerTime;
        this.uuid = uuid;
        this.targetObjectId = targetObjectId;
        this.misses = misses;
        this.movementTime = movementTime;
        this.indexDifficulty = indexDifficulty;
    }
}

struct CurrentTrial
{
    public int targetObjectId;
    public Vector2 startMousePosition;
    public float startTime;
    public int misses;
    public ClickableObject targetObject;

}

public class ManagerScript : MonoBehaviour
{
    [SerializeField] private Camera mainCamera;
    [SerializeField] private string serverUrl = "https://unity.api.runsesmithy.dev/upload";
    [SerializeField] private UIManager uiManager;




    private List<int> targetSequence;

    private int currentTargetIndex;
    private CurrentTrial currentTrial;
    private readonly List<ClickRecord> clickRecords = new();

    public int age;
    public string device;
    public int computerTime;

    private System.Guid uuid;

   private List<int> GenerateRandomSequence(int length, int objectCount)
{
    if (length > objectCount) throw new System.Exception("length exceeds unique pool");

    List<int> pool = new();
    for (int i = 0; i < objectCount; i++) pool.Add(i);

    System.Random rand = new();

    // Fisher–Yates
    for (int i = pool.Count - 1; i > 0; i--)
    {
        int j = rand.Next(0, i + 1);
        (pool[i], pool[j]) = (pool[j], pool[i]);
    }

    return pool.GetRange(0, length);
}


    void Awake()
    {
        uuid = System.Guid.NewGuid();
        targetSequence = GenerateRandomSequence(23, 23);
        print("Generated target sequence: " + string.Join(", ", targetSequence));
    }

    void Start()
    {
        mainCamera ??= Camera.main ?? FindFirstObjectByType<Camera>();
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
                userAge:age,
                device: device,
                computerTime: computerTime,
                uuid: uuid,
                targetObjectId: clickedObject.objectId,
                misses: currentTrial.misses,
                movementTime: Time.time - currentTrial.startTime,
                indexDifficulty: Mathf.Log(movementDistance / targetWidth + 1f) / Mathf.Log(2f)
            );

            clickRecords.Add(record);
            Debug.Log($"Target {record.targetObjectId} | MT: {record.movementTime:F3}s | ID: {record.indexDifficulty:F3} | Misses: {record.misses}");

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
        uiManager.handleFinished(uuid.ToString());
        Debug.Log("Sequence complete. Uploading results...");
    }

    public void HighlightNextTarget()
    {
        if (currentTargetIndex >= targetSequence.Count) return;

        int targetObjectId = targetSequence[currentTargetIndex];
        currentTrial.startMousePosition = Mouse.current.position.ReadValue();
        currentTrial.startTime = Time.time;
        currentTrial.misses = 0;
        ;


        foreach (var obj in FindObjectsByType<ClickableObject>(FindObjectsSortMode.None))
        {
            if (obj.objectId == targetObjectId)
            {
                obj.HighlightObject();
                currentTrial.targetObjectId = targetObjectId;
                currentTrial.targetObject = obj;
                break;
            }
        }
    }

    IEnumerator UploadResults()
    {
        StringBuilder sb = new();
        sb.AppendLine("uuid,user_age,device,computer_time,target_object_id,misses,movement_time,index_difficulty");

        foreach (var record in clickRecords)
            sb.AppendLine(string.Join(",",
                record.uuid.ToString(),
                record.userAge.ToString(System.Globalization.CultureInfo.InvariantCulture),
                record.device.ToString(),
                record.computerTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                record.targetObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                record.misses.ToString(System.Globalization.CultureInfo.InvariantCulture),
                record.movementTime.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                record.indexDifficulty.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            ));
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
        {
            StringBuilder err = new();
            err.AppendLine("Upload failed:");
            err.AppendLine($"• Result: {req.result}");
            err.AppendLine($"• Response Code: {req.responseCode}");
            err.AppendLine($"• Error: {req.error}");
            err.AppendLine($"• URL: {req.url}");
            err.AppendLine($"• Response Text: {req.downloadHandler?.text}");
            err.AppendLine($"• Headers:");
            foreach (var kvp in req.GetResponseHeaders() ?? new Dictionary<string, string>())
                err.AppendLine($"    {kvp.Key}: {kvp.Value}");

            Debug.LogError(err.ToString());
        }
    }
}
