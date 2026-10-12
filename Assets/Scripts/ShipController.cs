using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Physics-based spaceship controller. Reads from the "Spacecraft" action map in
/// InputSystem_Actions. Requires a Rigidbody on the same GameObject (Use Gravity = false).
///
/// Keyboard layout:
///   W / S        — thrust forward / brake
///   A / D        — yaw left / right
///   Q / E        — roll left / right
///   Mouse Y      — pitch
///   Left Shift   — boost
///   R            — return to home target
///
/// Gamepad layout:
///   RT / LT      — thrust / brake
///   Right stick  — pitch + yaw
///   A button     — boost
///
/// T.Flight HOTAS One (detected as Joystick):
///   Throttle lever  — thrust   (slider axis, mapped 0→1 → -1→1)
///   Joystick X      — roll
///   Joystick Y      — pitch
///   Rudder twist    — yaw
///   Button 2        — return to home target
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class ShipController : MonoBehaviour
{
    [Header("Thrust")]
    [Tooltip("Maximum force applied when thrust input is +1 (non-boosted).")]
    public float thrustForce     = 10f;
    [Tooltip("Multiplier applied to thrustForce while Boost is held.")]
    public float boostMultiplier = 2.5f;
    [Tooltip("Maximum speed (m/s). Velocity is clamped each FixedUpdate.")]
    public float maxSpeed        = 80f;

    [Header("Rotation (degrees per second)")]
    public float pitchSpeed = 120f;
    public float yawSpeed   = 240f;
    public float rollSpeed  = 120f;

    [Header("Damping")]
    [Tooltip("Linear drag applied to the Rigidbody — gives gentle deceleration in space.")]
    public float linearDrag  = 1.5f;
    [Tooltip("Angular drag applied to the Rigidbody — stops spin quickly when input is released.")]
    public float angularDrag = 8f;

    [Header("Home")]
    [Tooltip("Target to return to when ReturnHome is pressed (e.g. the Earth transform). " +
             "If unassigned, the ship returns to its position at scene start.")]
    public Transform homeTarget;
    [Tooltip("Offset from homeTarget in world space. Places the ship just outside the target.")]
    public Vector3 homeOffset = new Vector3(0f, 0f, -3f);

    Rigidbody           _rb;
    InputSystem_Actions _actions;

    InputAction _thrustAction;
    InputAction _pitchAction;
    InputAction _yawAction;
    InputAction _rollAction;
    InputAction _boostAction;
    InputAction _returnAction;

    Vector3    _spawnPosition;
    Quaternion _spawnRotation;

    void Awake()
    {
        // Singleton guard: destroy any duplicate spawned by a newly loaded scene.
        if (FindObjectsByType<ShipController>().Length > 1)
        {
            Destroy(gameObject);
            return;
        }
        DontDestroyOnLoad(gameObject);

        _rb = GetComponent<Rigidbody>();
        _rb.useGravity     = false;
        _rb.linearDamping  = linearDrag;
        _rb.angularDamping = angularDrag;

        _spawnPosition = transform.position;
        _spawnRotation = transform.rotation;

        _actions = new InputSystem_Actions();

        var sc        = _actions.Spacecraft;
        _thrustAction = sc.Thrust;
        _pitchAction  = sc.Pitch;
        _yawAction    = sc.Yaw;
        _rollAction   = sc.Roll;
        _boostAction  = sc.Boost;
        _returnAction = sc.ReturnHome;

        // Lock and hide the cursor for the flight experience.
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;
    }

    void OnEnable()  => _actions?.Spacecraft.Enable();
    void OnDisable() => _actions?.Spacecraft.Disable();

    void Update()
    {
        // ReturnHome is checked in Update (not FixedUpdate) so the press is never missed.
        if (_returnAction != null && _returnAction.WasPressedThisFrame())
            TeleportHome();

        // Escape quits the build; stops Play mode in the editor.
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }

    void FixedUpdate()
    {
        float thrust = _thrustAction.ReadValue<float>();
        float pitch  = _pitchAction .ReadValue<float>();
        float yaw    = _yawAction   .ReadValue<float>();
        float roll   = _rollAction  .ReadValue<float>();
        bool  boost  = _boostAction .IsPressed();

        // ── Thrust ──────────────────────────────────────────────────────────
        float force = thrustForce * (boost ? boostMultiplier : 1f);
        _rb.AddRelativeForce(Vector3.forward * thrust * force);

        if (_rb.linearVelocity.magnitude > maxSpeed)
            _rb.linearVelocity = _rb.linearVelocity.normalized * maxSpeed;

        // ── Rotation ────────────────────────────────────────────────────────
        Vector3 torque = new Vector3(
            -pitch * pitchSpeed,
             yaw   * yawSpeed,
            -roll  * rollSpeed) * Mathf.Deg2Rad;

        _rb.AddRelativeTorque(torque * Time.fixedDeltaTime, ForceMode.VelocityChange);
    }

    void TeleportHome()
    {
        Vector3    dest;
        Quaternion destRot;

        if (homeTarget != null)
        {
            dest    = homeTarget.position + homeOffset;
            Vector3 lookDir = homeTarget.position - dest;
            destRot = lookDir.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(lookDir, Vector3.up)
                : _spawnRotation;
        }
        else
        {
            dest    = _spawnPosition;
            destRot = _spawnRotation;
        }

        _rb.linearVelocity  = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.MovePosition(dest);
        _rb.MoveRotation(destRot);
    }
}
