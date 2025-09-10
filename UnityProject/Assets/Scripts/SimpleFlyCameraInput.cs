using UnityEngine;
using UnityEngine.InputSystem;

/// Fly around in Play Mode using the *New Input System*.
/// RMB = hold to look, WASD = move, Q/E = down/up, Shift = fast, Ctrl = slow,
/// Mouse Wheel = tweak base speed, Esc = release cursor.
[DisallowMultipleComponent]
public class SimpleFlyCameraInput : MonoBehaviour
{
    [Header("Speed (m/s)")]
    public float moveSpeed = 5f;
    public float fastMultiplier = 4f;
    public float slowMultiplier = 0.25f;
    public float scrollStep = 1f;

    [Header("Mouse Look")]
    public float lookSensitivity = 0.15f; // degrees per pixel
    public float pitchMin = -89f;
    public float pitchMax =  89f;

    [Header("Smoothing")]
    public float moveDamping = 12f;
    public float lookDamping = 20f;

    float yaw, pitch;
    Vector3 vel;    // smoothed velocity
    Vector2 lookVel;

    bool looking;

    // --- New Input System actions (created in code, no .inputactions asset needed) ---
    InputAction lookAction;      // Mouse delta / gamepad right stick
    InputAction moveAction;      // WASD / gamepad left stick
    InputAction vertAction;      // Q/E (down/up), plus Ctrl/Space
    InputAction sprintAction;    // Shift
    InputAction slowAction;      // Ctrl
    InputAction rmbAction;       // Right mouse button (hold to look)
    InputAction escAction;       // Escape (release cursor)
    InputAction scrollAction;    // Mouse wheel (Vector2.y)

    void OnEnable()
    {
        // cache starting orientation
        var e = transform.eulerAngles;
        yaw = e.y; pitch = e.x;

        // Build actions + bindings
        var map = new InputActionMap("FlyCam");

        lookAction   = map.AddAction("look",   binding: "<Mouse>/delta");
        lookAction.AddBinding("<Gamepad>/rightStick");

        moveAction   = map.AddAction("move");
        moveAction.AddCompositeBinding("2DVector")
            .With("Up",    "<Keyboard>/w")
            .With("Down",  "<Keyboard>/s")
            .With("Left",  "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");
        moveAction.AddBinding("<Gamepad>/leftStick");

        vertAction   = map.AddAction("vert");
        vertAction.AddCompositeBinding("1DAxis")
            .With("Positive", "<Keyboard>/e")      // up
            .With("Negative", "<Keyboard>/q");     // down
        vertAction.AddBinding("<Keyboard>/space").WithProcessor("scale(factor=1)"); // extra up
        vertAction.AddBinding("<Keyboard>/leftCtrl").WithProcessor("scale(factor=-1)"); // extra down

        sprintAction = map.AddAction("sprint", binding: "<Keyboard>/leftShift");
        slowAction   = map.AddAction("slow",   binding: "<Keyboard>/leftCtrl");

        rmbAction    = map.AddAction("rmb",    binding: "<Mouse>/rightButton");
        escAction    = map.AddAction("esc",    binding: "<Keyboard>/escape");
        scrollAction = map.AddAction("scroll", binding: "<Mouse>/scroll");

        // Cursor lock/unlock
        rmbAction.performed += _ => SetLooking(true);
        rmbAction.canceled  += _ => SetLooking(false);
        escAction.performed += _ => SetLooking(false);

        map.Enable();
    }

    void OnDisable()
    {
        lookAction?.actionMap?.Disable();
    }

    void SetLooking(bool enable)
    {
        looking = enable;
        Cursor.lockState = enable ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible   = !enable;
    }

    void Update()
    {
        if (!Application.isPlaying) return;

        // --- Mouse look ---
        Vector2 lookInput = looking ? lookAction.ReadValue<Vector2>() : Vector2.zero;
        // Convert pixels to degrees (scale by sensitivity)
        Vector2 targetLook = lookInput * lookSensitivity;

        lookVel = Vector2.Lerp(lookVel, targetLook, 1f - Mathf.Exp(-lookDamping * Time.unscaledDeltaTime));
        yaw   += lookVel.x;
        pitch += -lookVel.y; // invert Y (mouse up = look up)
        pitch = Mathf.Clamp(pitch, pitchMin, pitchMax);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        // --- Speed modifiers ---
        float speed = moveSpeed;
        if (sprintAction.IsPressed()) speed *= fastMultiplier;
        if (slowAction.IsPressed())   speed *= slowMultiplier;

        // Mouse wheel tweaks base speed
        float scrollY = scrollAction.ReadValue<Vector2>().y;
        if (Mathf.Abs(scrollY) > 0.0001f)
            moveSpeed = Mathf.Max(0.1f, moveSpeed + scrollY * scrollStep);

        // --- Movement (WASD + QE/Space/Ctrl) ---
        Vector2 planar = moveAction.ReadValue<Vector2>(); // x=left/right, y=forward/back
        float   updown = vertAction.ReadValue<float>();
        Vector3 input  = new Vector3(planar.x, updown, planar.y);
        input = Vector3.ClampMagnitude(input, 1f);

        Vector3 targetVel = (transform.right * input.x + transform.up * input.y + transform.forward * input.z) * speed;
        vel = Vector3.Lerp(vel, targetVel, 1f - Mathf.Exp(-moveDamping * Time.unscaledDeltaTime));
        transform.position += vel * Time.unscaledDeltaTime;
    }
}
