using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

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
    11, 14, 2, 4, 5, 6, 7, 8, 9, 10, 11,
    12, 13, 14, 15, 16, 17, 18, 19, 20, 21
};

    int currentSequenceIndex;
    CurrentlyRunningTest currentTest;
    readonly List<TestData> results = new();

    void Start()
    {
        mainCamera ??= Camera.main ?? FindObjectOfType<Camera>();
        Mouse.current.WarpCursorPosition(new Vector2(Screen.width / 2f, Screen.height / 2f));
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
            return;

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

            Debug.Log($"Clicked {clicked.objectId} | TargetPos: {targetPos} | StartPos: {currentTest.startMousePosition} | Width: {width}");

            var result = new TestData(
                currentTest.misses,
                Time.time - currentTest.startTime,
                Mathf.Log(dist / width + 1f) / Mathf.Log(2f)
            );

            results.Add(result);
            Debug.Log($"Test {result.testId} | MT: {result.MT:F3} | ID: {result.ID:F3} | Misses: {result.misses}");

            currentTest.targetObject?.ResetColor();
            currentSequenceIndex++;
            HighlightNextInSequence();
        }
        else
        {
            Debug.Log($"Wrong object! Expected {expectedId}, got {clicked.objectId}");
            currentTest.misses++;
        }
    }

    void HighlightNextInSequence()
    {
        if (currentSequenceIndex >= sequence.Count) return;
        Mouse.current.WarpCursorPosition(new Vector2(Screen.width / 2f, Screen.height / 2f));

        int targetId = sequence[currentSequenceIndex];
        currentTest.startMousePosition = new Vector2(Screen.width / 2f, Screen.height / 2f);
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
}
