using System.Collections.Generic;
using UnityEngine;

public class ObjectSpawner : MonoBehaviour
{
    [Header("References")]
    public ClickTestManager testManager;     // Drag your TestManager here
    public GameObject[] prefabOptions;       // Circle, Square, Capsule prefabs
    public Transform workbenchSurface;       // Optional: the parent or reference for positioning

    [Header("Spawn Settings")]
    public int totalObjects = 50;
    public float spacing = 0.25f;            // Distance between each object
    public int columns = 10;                 // Controls grid layout

    private List<ClickableObject> spawnedObjects = new List<ClickableObject>();

    void Start()
    {
        if (prefabOptions == null || prefabOptions.Length == 0)
        {
            Debug.LogError("No prefabs assigned to ObjectSpawner!");
            return;
        }

        SpawnObjects();
        AssignToManager();
    }

    void SpawnObjects()
    {
        // Start position centered on workbench or world origin
        Vector3 startPos = (workbenchSurface != null)
            ? workbenchSurface.position + Vector3.up * 0.05f // slightly above surface
            : Vector3.zero;

        int rows = Mathf.CeilToInt(totalObjects / (float)columns);

        for (int i = 0; i < totalObjects; i++)
        {
            // Choose prefab (cyclic or random)
            GameObject prefab = prefabOptions[i % prefabOptions.Length];

            int x = i % columns;
            int z = i / columns;

            Vector3 offset = new Vector3(x * spacing, 0, z * spacing);
            Vector3 spawnPos = startPos + offset;

            GameObject obj = Instantiate(prefab, spawnPos, Quaternion.identity);
            obj.name = $"Target_{i + 1:D2}";

            var clickObj = obj.GetComponent<ClickableObject>();
            if (clickObj != null)
            {
                clickObj.objectID = obj.name;
                clickObj.shapeType = prefab.name;
                spawnedObjects.Add(clickObj);
            }
        }
    }

    void AssignToManager()
    {
        if (testManager == null)
        {
            testManager = FindObjectOfType<ClickTestManager>();
        }

        if (testManager != null)
        {
            testManager.clickableObjects = spawnedObjects;
            Debug.Log($"Assigned {spawnedObjects.Count} objects to TestManager in fixed order.");
        }
        else
        {
            Debug.LogError("No ClickTestManager found in scene!");
        }
    }
}
