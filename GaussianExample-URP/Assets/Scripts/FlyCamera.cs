using UnityEngine;

public class FlyCameraLegacy : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;
    public float boostMultiplier = 3f;
    public float lookSensitivity = 2f;

    private float yaw;
    private float pitch;
    private bool lookEnabled = true;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        // --- Toggle look mode with Right Mouse Button ---
        if (Input.GetMouseButtonDown(1)) // right click down
        {
            lookEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        if (Input.GetMouseButtonUp(1)) // right click released
        {
            lookEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // --- Look (only when enabled) ---
        if (lookEnabled)
        {
            float mouseX = Input.GetAxis("Mouse X");
            float mouseY = Input.GetAxis("Mouse Y");

            yaw += mouseX * lookSensitivity;
            pitch -= mouseY * lookSensitivity;
            pitch = Mathf.Clamp(pitch, -89f, 89f);

            transform.eulerAngles = new Vector3(pitch, yaw, 0f);
        }

        // --- Move ---
        Vector3 move = new Vector3(
            Input.GetAxis("Horizontal"), // A/D
            0,
            Input.GetAxis("Vertical")    // W/S
        );

        if (Input.GetKey(KeyCode.Space)) move.y += 1;
        if (Input.GetKey(KeyCode.LeftControl)) move.y -= 1;

        float speed = Input.GetKey(KeyCode.LeftShift) ? moveSpeed * boostMultiplier : moveSpeed;
        transform.Translate(move * speed * Time.deltaTime, Space.Self);
    }
}
