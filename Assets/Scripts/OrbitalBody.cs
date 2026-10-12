// OrbitalBody.cs
using UnityEngine;

public class OrbitalBody : MonoBehaviour
{
    public static int EARTH_YEAR_FRAMES = 10800; // 60 seconds per Earth orbit at 60 fps
    public static int ORBIT_SCALE = 20;

    [Tooltip("Centre of this orbit. Leave empty to orbit the world origin (the Sun). Assign a planet Transform for moons.")]
    public Transform orbitCenter;

    public float semiMajorAxis;
    public float eccentricity;
    public float inclination;          // degrees
    public float argPeriapsis;         // degrees
    public float longAscNode;          // degrees
    public float periodYear;           // orbit period in Earth years
    public float meanAnomalyAtEpoch;

    [Header("Rotation")]
    [Tooltip("Tilt of the spin axis in degrees. Real values: Earth=23.4, Mars=25.2, Saturn=26.7, Venus=177.4 (retrograde).")]
    public float axialTilt = 0f;

    [Tooltip("Sidereal rotation period in Earth days. Earth=1, Mars=1.026, Jupiter=0.415. " +
             "0 = no spin. Negative = retrograde rotation (e.g. Venus=-243).")]
    public float spinPeriodDays = 0f;

    [Tooltip("Tidally locked: always show the same face toward orbitCenter. " +
             "Enable for the Moon. When true, axialTilt and spinPeriodDays are ignored.")]
    public bool tidallyLocked = false;

    void Update()
    {
        // ── Orbital position ──────────────────────────────────────────────────
        float periodSeconds = ((EARTH_YEAR_FRAMES * periodYear) / 3) / 60;
        float t = Time.time;
        float M = meanAnomalyAtEpoch + (t / periodSeconds) * 2f * Mathf.PI;

        float E  = M + eccentricity * Mathf.Sin(M);
        float nu = E + 2f * eccentricity * Mathf.Sin(E);
        float r  = (semiMajorAxis * ORBIT_SCALE) * (1f - eccentricity * Mathf.Cos(E));

        float xO = r * Mathf.Cos(nu);
        float yO = r * Mathf.Sin(nu);

        float w  = argPeriapsis * Mathf.Deg2Rad;
        float i  = inclination  * Mathf.Deg2Rad;
        float O  = longAscNode  * Mathf.Deg2Rad;

        float x1 = xO * Mathf.Cos(w) - yO * Mathf.Sin(w);
        float y1 = xO * Mathf.Sin(w) + yO * Mathf.Cos(w);

        float xW = x1;
        float yW = y1 * Mathf.Cos(i);
        float zW = y1 * Mathf.Sin(i);

        float xF = xW * Mathf.Cos(O) - yW * Mathf.Sin(O);
        float yF = xW * Mathf.Sin(O) + yW * Mathf.Cos(O);

        Vector3 center = orbitCenter != null ? orbitCenter.position : Vector3.zero;
        transform.position = center + new Vector3(xF, zW, yF); // Blender Z-up → Unity Y-up

        // ── Rotation ──────────────────────────────────────────────────────────
        if (tidallyLocked && orbitCenter != null)
        {
            // Synchronous rotation: always show the same face toward the parent body.
            Vector3 toParent = (orbitCenter.position - transform.position).normalized;
            if (toParent.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(toParent, Vector3.up);
        }
        else if (spinPeriodDays != 0f)
        {
            // Convert spin period from Earth days to game seconds using the same
            // time scale as orbital periods: 1 Earth year = 60 game seconds.
            // Scale down for aesthetic reasons
            float earthYearGameSec  = ((EARTH_YEAR_FRAMES * 1f) / 3f) / 60f; // = 60s
            float spinPeriodGameSec = spinPeriodDays * (earthYearGameSec / 365.25f) * 20f;
            float spinAngle         = (t / spinPeriodGameSec) * 360f;
            transform.rotation      = Quaternion.Euler(axialTilt, spinAngle, 0f);
        }
    }
}
