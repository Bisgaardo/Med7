using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

public class ManagerScript : MonoBehaviour
{
    List<int> sequence = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,15,16,17,18,19,20,21 };
    private int currentSequenceIndex = 0;
    [SerializeField] private Camera mainCamera;
    
    private ClickableObject currentHighlightedObject;

    void Start()
    {
        // If no camera is assigned, try to find the main camera
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
            if (mainCamera == null)
            {
                mainCamera = FindObjectOfType<Camera>();
            }
        }
        
        // Start the sequence by highlighting the first item
        HighlightNextInSequence();
    }

    // Update is called once per frame
    void Update()
    {
        // Check for mouse click using new Input System
        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            HandleMouseClick();
        }
    }

    void HandleMouseClick()
    {
        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Ray ray = mainCamera.ScreenPointToRay(mousePosition);
        
        if (!Physics.Raycast(ray, out RaycastHit hit)) return;
        ClickableObject clickableObj = hit.collider.GetComponent<ClickableObject>();

        if (!clickableObj) return;

        int objectId = clickableObj.objectId;
        Debug.Log($"Clicked on object with ID: {objectId}");
        
        // Check if this is the correct object in the sequence
        if (currentSequenceIndex < sequence.Count && objectId == sequence[currentSequenceIndex])
        {
            Debug.Log($"Correct! Clicked object {objectId} at sequence position {currentSequenceIndex}");
            
            // Move to next in sequence
            currentSequenceIndex++;
            
            // Reset the color of the previously highlighted object
            if (currentHighlightedObject != null)
            {
                currentHighlightedObject.ResetColor();
            }
            
            // Check if sequence is complete
            if (currentSequenceIndex >= sequence.Count)
            {
                Debug.Log("Sequence completed!");
                // Optionally restart the sequence
                // RestartSequence();
            }
            else
            {
                // Highlight the next object in sequence
                HighlightNextInSequence();
            }
        }
        else
        {
            Debug.Log($"Wrong object! Expected {sequence[currentSequenceIndex]}, but clicked {objectId}");
        }
    }
    
    void HighlightNextInSequence()
    {
        if (currentSequenceIndex >= sequence.Count) return;
        
        int targetId = sequence[currentSequenceIndex];
        
        // Find the object with the target ID
        ClickableObject[] allClickableObjects = FindObjectsByType<ClickableObject>(FindObjectsSortMode.None);
        
        foreach (ClickableObject obj in allClickableObjects)
        {
            if (obj.objectId == targetId)
            {
                obj.HighlightObject();
                currentHighlightedObject = obj;
                Debug.Log($"Highlighting object with ID: {targetId}");
                break;
            }
        }
    }
    
    public void RestartSequence()
    {
        // Reset all objects to original color
        ClickableObject[] allClickableObjects = FindObjectsByType<ClickableObject>(FindObjectsSortMode.None);
        foreach (ClickableObject obj in allClickableObjects)
        {
            obj.ResetColor();
        }
        
        // Reset sequence tracking
        currentSequenceIndex = 0;
        currentHighlightedObject = null;
        
        // Start over
        HighlightNextInSequence();
        Debug.Log("Sequence restarted!");
    }
}
