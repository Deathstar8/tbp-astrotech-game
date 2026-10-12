using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Drives all sun-related visuals from one Inspector-friendly component.
///
/// Setup:
///   1. Attach to the sun GameObject (world origin).
///   2. Add a Point Light component to the same GameObject and assign it to sunLight.
///   3. Assign the sun's MeshRenderer to sunRenderer.
///   4. Assign the scene's Global Volume to postProcessVolume.
///   5. Disable (or delete) the scene's Directional Light.
/// </summary>
public class SunController : MonoBehaviour
{
    [Header("Point Light")]
    [Tooltip("Point Light component on this GameObject.")]
    public Light sunLight;
    [Tooltip("Color of the emitted light reaching planets and asteroids.")]
    public Color lightColor     = new Color(1.00f, 0.95f, 0.85f);
    [Tooltip("Intensity of the Point Light. URP uses inverse-square falloff, so this needs to be high at solar-system scale. " +
             "At ORBIT_SCALE=20: ~400 to illuminate Earth's orbit (20u), ~1000+ to reach Jupiter (100u).")]
    public float lightIntensity = 1000f;
    [Tooltip("Maximum range (world units). Set large enough to cover all planets — outermost orbit is ~380 units at ORBIT_SCALE = 20.")]
    public float lightRange     = 2000f;

    [Header("Sun Glow")]
    [Tooltip("MeshRenderer of the sun sphere.")]
    public Renderer sunRenderer;
    [Tooltip("Base emission colour of the sun surface (will be multiplied by emissionIntensity).")]
    public Color emissionColor      = new Color(1.00f, 0.90f, 0.50f);
    [Tooltip("HDR emission multiplier. Values above 1 push the sun surface into the bloom range.")]
    [Range(1f, 20f)]
    public float emissionIntensity  = 5f;

    [Header("Bloom")]
    [Tooltip("The scene's Global Volume that holds the Bloom override.")]
    public Volume postProcessVolume;
    [Range(0f, 5f)]
    public float bloomIntensity  = 1.5f;
    [Range(0f, 1f)]
    public float bloomThreshold  = 0.9f;
    [Range(0f, 1f)]
    public float bloomScatter    = 0.7f;

    [Header("Asteroid Shaders")]
    [Tooltip("Minimum light level applied to all asteroid shaders (0 = pitch black away from sun, 0.05 = faint starfield ambiance).")]
    [Range(0f, 0.5f)]
    public float ambientIntensity = 0.05f;

    Bloom _bloom;

    void Start()
    {
        ConfigureLight();
        ConfigureEmission();
        ConfigureBloom();
        PushShaderGlobals();
    }

    void Update()
    {
        // Re-push every frame so Inspector sliders update live in Play mode
        // and so the sun position is always current (it could orbit in future).
        PushShaderGlobals();

        sunLight.color     = lightColor;
        sunLight.intensity = lightIntensity;
        sunLight.range     = lightRange;

        sunRenderer.material.SetColor("_EmissionColor", emissionColor * emissionIntensity);

#if UNITY_EDITOR
        // Bloom parameter live-editing in editor Play mode only — avoid Volume API overhead in builds.
        if (_bloom != null)
        {
            _bloom.intensity .Override(bloomIntensity);
            _bloom.threshold .Override(bloomThreshold);
            _bloom.scatter   .Override(bloomScatter);
        }
#endif
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void ConfigureLight()
    {
        if (sunLight == null) { Debug.LogWarning("[SunController] sunLight not assigned."); return; }
        sunLight.type      = LightType.Point;
        sunLight.color     = lightColor;
        sunLight.intensity = lightIntensity;
        sunLight.range     = lightRange;
        sunLight.shadows   = LightShadows.None; // shadows are impractical at solar-system scale
    }

    void ConfigureEmission()
    {
        if (sunRenderer == null) { Debug.LogWarning("[SunController] sunRenderer not assigned."); return; }
        sunRenderer.material.EnableKeyword("_EMISSION");
        sunRenderer.material.SetColor("_EmissionColor", emissionColor * emissionIntensity);
    }

    void ConfigureBloom()
    {
        if (postProcessVolume == null) { Debug.LogWarning("[SunController] postProcessVolume not assigned."); return; }

        if (postProcessVolume.profile.TryGet<Bloom>(out _bloom))
        {
            _bloom.active = true;
            _bloom.intensity .Override(bloomIntensity);
            _bloom.threshold .Override(bloomThreshold);
            _bloom.scatter   .Override(bloomScatter);
        }
        else
        {
            Debug.LogWarning("[SunController] No Bloom override found in the Volume profile. Add one via the Volume component.");
        }
    }

    void PushShaderGlobals()
    {
        Shader.SetGlobalVector("_SunWorldPos", transform.position);
        Shader.SetGlobalFloat ("_SunAmbient",  ambientIntensity);
    }
}
