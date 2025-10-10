using UnityEngine;

public class ClickableObject : MonoBehaviour
{
    public int objectId;
    private Color originalColor;

    void Start()
    {
        originalColor = GetComponent<Renderer>().material.color;
    }
    void Update()
    {

    }

    public void HighlightObject()
    {
        GetComponent<Renderer>().material.color = Color.yellow;
    }

    public void ResetColor()
    {
        GetComponent<Renderer>().material.color = originalColor;
    }

}
