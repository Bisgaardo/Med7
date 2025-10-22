using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Text;

class TestData
{
    static int nextId = 1;
    public int testId { get; }
    public int misses;
    public float MT;
    public float ID;

    public TestData(int misses, float MT, float ID)
    {
        testId = nextId++;
        this.misses = misses;
        this.MT = MT;
        this.ID = ID;
    }
}

struct CurrentlyRunningTest
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
    private List<int> sequence = new()
    {
        0, 14, 2, 4, 5, 6, 7, 8, 9, 10, 11,
        // 12, 13, 14, 15, 16, 17, 18, 19, 20, 21
    };

    int currentSequenceIndex;
    CurrentlyRunningTest currentTest;
    readonly List<TestData> results = new();

    void Start()
    {
        mainCamera ??= Camera.main ?? FindObjectOfType<Camera>();
        HighlightNextInSequence();
    }

    void Update()
    {
        if (Mouse.current.leftButton.wasPressedThisFrame)
            HandleMouseClick();
    }

    void HandleMouseClick()
    {
        if (!Physics.Raycast(mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue()), out RaycastHit hit))
        {
            currentTest.misses++;
            return;
        }

        if (!hit.collider.TryGetComponent(out ClickableObject clicked))
        {
            currentTest.misses++;
            return;
        }

        int expectedId = sequence[currentSequenceIndex];

        if (clicked.objectId == expectedId)
        {
            Vector3 targetPos = clicked.GetScreenPosition(mainCamera);
            float width = clicked.GetDirectionalWidth(mainCamera, currentTest.startMousePosition);
            float dist = Vector2.Distance(currentTest.startMousePosition, targetPos);

            var result = new TestData(
                currentTest.misses,
                Time.time - currentTest.startTime,
                Mathf.Log(dist / width + 1f) / Mathf.Log(2f)
            );

            results.Add(result);
            Debug.Log($"Test {result.testId} | MT: {result.MT:F3} | ID: {result.ID:F3} | Misses: {result.misses}");

            currentTest.targetObject?.ResetColor();
            currentSequenceIndex++;
            if (currentSequenceIndex >= sequence.Count)
            {
                StartCoroutine(UploadResults());
                Debug.Log("All tests complete. Results uploaded.");
            }
            else
            {
                HighlightNextInSequence();
            }
        }
        else
        {
            currentTest.misses++;
        }
    }

    void HighlightNextInSequence()
    {
        if (currentSequenceIndex >= sequence.Count) return;

        int targetId = sequence[currentSequenceIndex];
        currentTest.startMousePosition = Mouse.current.position.ReadValue();
        currentTest.startTime = Time.time;
        currentTest.misses = 0;

        foreach (var obj in FindObjectsByType<ClickableObject>(FindObjectsSortMode.None))
        {
            if (obj.objectId == targetId)
            {
                obj.HighlightObject();
                currentTest.targetId = targetId;
                currentTest.targetObject = obj;
                break;
            }
        }
    }

    IEnumerator UploadResults()
    {
        StringBuilder sb = new();

        sb.AppendLine("TestID, Misses, MT, ID");

        foreach (var r in results)
        {
            
            sb.AppendLine($"{r.testId}, {r.misses}, {r.MT:F3}, {r.ID:F3}");
        }

        byte[] bodyRaw = Encoding.UTF8.GetBytes(sb.ToString());
        Debug.Log("Uploading Results:\n" + bodyRaw.Length + " bytes");  

        string serverUrl = "http://localhost:3000/upload"; // replace

        using UnityWebRequest req = new(serverUrl, "POST");
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "text/csv");

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
            Debug.Log("Results uploaded successfully");
        else
            Debug.LogError($"Upload failed: {req.error}");
    }
}
