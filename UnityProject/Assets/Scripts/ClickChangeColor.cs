using UnityEngine;

public class ClickChangeColor : MonoBehaviour
{
    private Renderer objectRenderer;
    private Material instanceMaterial;

    void Start()
    {
        // Get Renderer
        objectRenderer = GetComponent<Renderer>();
        if (objectRenderer == null)
        {
            Debug.LogError("No Renderer found on " + gameObject.name);
        }
        else
        {
            // Create a unique material instance so changing color doesn't affect others
            instanceMaterial = new Material(objectRenderer.material);
            objectRenderer.material = instanceMaterial;
        }

        // Register this object with the manager
        ClickManager.Instance.RegisterObject(this);
    }

    void OnMouseDown()
    {
        // Change to a random color when clicked
        if (instanceMaterial != null)
        {
            instanceMaterial.color = new Color(Random.value, Random.value, Random.value);
        }

        // Notify manager of click
        ClickManager.Instance.ObjectClicked(this);
    }
}
