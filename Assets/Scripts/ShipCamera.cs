using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Smooth third-person follow camera for the player ship.
/// Attach to the Main Camera. Drag the ship's root Transform into the Target field.
///
/// The camera sits at a fixed local offset behind and above the ship, smoothly
/// following both position and rotation. A small look-ahead shifts the aim point
/// slightly in front of the ship so motion feels more responsive.
/// </summary>
public class ShipCamera : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("The ship transform to follow.")]
    public Transform target;

    [Header("Position")]
    [Tooltip("Camera offset in the ship's local space. Negative Z = behind, positive Y = above.")]
    public Vector3 offset = new Vector3(0f, 0.66f, -3f);
    [Tooltip("SmoothDamp time for position tracking. Lower = snappier.")]
    public float positionSmooth = 0.12f;

    [Header("Rotation")]
    [Tooltip("Slerp speed for rotation tracking (multiplied by deltaTime). Lower = floatier.")]
    public float rotationSmooth = 8f;
    [Tooltip("How far ahead of the ship the camera looks (world units). Gives a sense of direction.")]
    public float lookAheadDist  = 5f;

    Vector3 _velocity;

    void Awake()
    {
        // Singleton guard: destroy any duplicate spawned by a newly loaded scene.
        if (FindObjectsByType<ShipCamera>().Length > 1)
        {
            Destroy(gameObject);
            return;
        }
        DontDestroyOnLoad(gameObject);

        // Disable any extra cameras that arrive with a freshly loaded scene.
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        var thisCam = GetComponent<Camera>();
        foreach (var cam in FindObjectsByType<Camera>())
            if (cam != thisCam)
                cam.gameObject.SetActive(false);
    }

    void LateUpdate()
    {
        if (target == null) return;

        // ── Position ────────────────────────────────────────────────────────
        // Desired world position = ship's local offset transformed to world space.
        Vector3 desiredPos = target.TransformPoint(offset);
        transform.position = Vector3.SmoothDamp(
            transform.position, desiredPos, ref _velocity, positionSmooth);

        // ── Rotation ────────────────────────────────────────────────────────
        // Aim slightly ahead of the ship so the camera leads the motion.
        Vector3 lookTarget  = target.position + target.forward * lookAheadDist;
        Vector3 lookDir     = lookTarget - transform.position;

        if (lookDir.sqrMagnitude > 0.001f)
        {
            Quaternion desiredRot = Quaternion.LookRotation(lookDir);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, desiredRot, rotationSmooth * Time.deltaTime);
        }
    }
}
