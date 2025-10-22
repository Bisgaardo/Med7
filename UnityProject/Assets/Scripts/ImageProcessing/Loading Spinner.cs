using UnityEngine;

public class LoadingSpinner : MonoBehaviour
{
    [SerializeField] private float rotationSpeed = 360f; // degrees per second

    void Update()
    {
        transform.Rotate(0, 0, rotationSpeed * Time.deltaTime);
    }
}
