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
    private bool skipLookFrame = false; // discard first delta after relock

    void Start()
    {
        // Sync yaw/pitch to current rotation
        var e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        // --- Toggle look mode with Right Mouse Button ---
        if (Input.GetMouseButtonDown(1)) // right click down
        {
            lookEnabled = true;
            // Resync to current rotation to avoid jumps
            var e = transform.eulerAngles;
            yaw = e.y;
            pitch = e.x;
            skipLookFrame = true; // ignore first delta after lock
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

            if (skipLookFrame)
            {
                // Discard the large recenter delta on the first frame
                skipLookFrame = false;
                mouseX = 0f; mouseY = 0f;
            }

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
