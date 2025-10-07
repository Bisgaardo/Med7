using System.Collections.Generic;
using UnityEngine;

public class ObjectSpawner : MonoBehaviour
{
    [Header("References")]
    public ClickTestManager testManager;        // Drag your TestManager here
    public GameObject[] prefabOptions;          // Prefabs (Circle, Square, Capsule)
    public Transform[] shelfSpawnPoints;        // 🟢 Add 3 shelf spawn transforms here

    [Header("Spawn Settings")]
    public int totalObjects = 50;
    public float scatterRadius = 0.25f;         // How much random spread around each shelf
    public bool randomizeShelf = true;          // Randomly choose shelf per object

    private List<ClickableObject> spawnedObjects = new List<ClickableObject>();

    void Start()
    {
        if (prefabOptions == null || prefabOptions.Length == 0)
        {
            Debug.LogError("❌ No prefabs assigned to ObjectSpawner!");
            return;
        }

        if (shelfSpawnPoints == null || shelfSpawnPoints.Length == 0)
        {
            Debug.LogError("❌ No shelf spawn points assigned!");
            return;
        }

        SpawnObjects();
        AssignToManager();
    }

    void SpawnObjects()
    {
        // optional: fixed random seed for reproducibility
        Random.InitState(42);

        for (int i = 0; i < totalObjects; i++)
        {
            // Choose prefab
            GameObject prefab = prefabOptions[i % prefabOptions.Length];

            // Choose which shelf to spawn on
            Transform chosenShelf = randomizeShelf
                ? shelfSpawnPoints[Random.Range(0, shelfSpawnPoints.Length)]
                : shelfSpawnPoints[i % shelfSpawnPoints.Length];

            // Scatter around that shelf point
            Vector2 randCircle = Random.insideUnitCircle * scatterRadius;
            Vector3 spawnPos = chosenShelf.position + new Vector3(randCircle.x, 0f, randCircle.y);

            // Instantiate object
            GameObject obj = Instantiate(prefab, spawnPos, Quaternion.identity);
            obj.name = $"Target_{i + 1:D2}";
            obj.transform.SetParent(chosenShelf); // keep organized

            var clickObj = obj.GetComponent<ClickableObject>();
            if (clickObj != null)
            {
                clickObj.objectID = obj.name;
                clickObj.shapeType = prefab.name;
                spawnedObjects.Add(clickObj);
            }
        }

        Debug.Log($"✅ Spawned {spawnedObjects.Count} clickable objects across {shelfSpawnPoints.Length} shelves.");
    }

    void AssignToManager()
    {
        if (testManager == null)
            testManager = FindObjectOfType<ClickTestManager>();

        if (testManager != null)
        {
            testManager.clickableObjects = spawnedObjects;
            Debug.Log($"✅ Assigned {spawnedObjects.Count} objects to TestManager.");
        }
        else
        {
            Debug.LogError("❌ No ClickTestManager found in scene!");
        }
    }
}
