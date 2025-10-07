using UnityEngine;
using System.Collections.Generic;

public class ClickManager : MonoBehaviour
{
    // Singleton pattern so we can access this easily
    public static ClickManager Instance { get; private set; }

    private List<ClickChangeColor> allObjects = new List<ClickChangeColor>();

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
        }
    }

    // Register each prefab as it starts
    public void RegisterObject(ClickChangeColor obj)
    {
        if (!allObjects.Contains(obj))
        {
            allObjects.Add(obj);
        }
    }

    // Called when an object is clicked
    public void ObjectClicked(ClickChangeColor obj)
    {
        Debug.Log(obj.gameObject.name + " was clicked!");
    }

    // Optional: Change all objects to random colors
    public void ChangeAllColors()
    {
        foreach (var obj in allObjects)
        {
            obj.GetComponent<Renderer>().material.color = new Color(Random.value, Random.value, Random.value);
        }
    }
}
