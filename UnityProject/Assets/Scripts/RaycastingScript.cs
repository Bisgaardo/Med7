using UnityEngine;
using UnityEngine.InputSystem; // new input system

public class ObjectSelector : MonoBehaviour
{
    private Camera cam;
    private GameObject selectedObject;
    private Material originalMaterial;
    public Material outlineMaterial; // assign in Inspector

    void Start()
    {
        cam = Camera.main;
    }

    void Update()
    {
        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                // Restore previous
                if (selectedObject != null)
                {
                    selectedObject.GetComponent<Renderer>().material = originalMaterial;
                }

                // Highlight new
                selectedObject = hit.collider.gameObject;
                originalMaterial = selectedObject.GetComponent<Renderer>().material;
                selectedObject.GetComponent<Renderer>().material = outlineMaterial;
            }
            else
            {
                // Clicked empty space → unhighlight
                if (selectedObject != null)
                {
                    selectedObject.GetComponent<Renderer>().material = originalMaterial;
                    selectedObject = null;
                }
            }
        }
    }
}
