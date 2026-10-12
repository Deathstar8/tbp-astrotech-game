using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Singleton that persists across scene loads and handles cycling between scenes.
///
/// Setup:
///   1. Attach to an empty GameObject in the Astrotech scene.
///   2. Assign Stars Skybox  → Assets/Materials/MilkyWay.mat
///   3. Assign Solar System Skybox → the skybox used by the Astrotech scene (or leave null).
///   4. Ensure both scenes are in File → Build Settings (Astrotech = index 0, AstroTech - Stars = index 1).
///
/// Controls:
///   P key  /  HOTAS button14  →  cycle to the next scene
/// </summary>
public class SceneTransitionManager : MonoBehaviour
{
    [Header("Scenes")]
    [Tooltip("Scene names to cycle through, in order. Must match names in Build Settings exactly.")]
    public string[] sceneNames = { "Astrotech", "AstroTech - Stars" };

    [Header("Skyboxes")]
    [Tooltip("Skybox applied when the Astrotech (solar system) scene is active.")]
    public Material solarSystemSkybox;
    [Tooltip("Skybox applied when the AstroTech - Stars scene is active. Assign MilkyWay.mat.")]
    public Material starsSkybox;

    int                  _currentIndex;
    InputSystem_Actions  _actions;
    bool                 _transitioning;

    void Awake()
    {
        // Singleton guard — only one instance should ever exist.
        if (FindObjectsByType<SceneTransitionManager>().Length > 1)
        {
            Destroy(gameObject);
            return;
        }
        DontDestroyOnLoad(gameObject);

        _actions = new InputSystem_Actions();
        _actions.Player.Enable();

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        _actions?.Player.Disable();
    }

    void Update()
    {
        if (!_transitioning && _actions.Player.Next.WasPressedThisFrame())
            LoadNextScene();
    }

    void LoadNextScene()
    {
        _transitioning = true;
        _currentIndex  = (_currentIndex + 1) % sceneNames.Length;
        SceneManager.LoadScene(sceneNames[_currentIndex]);
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _transitioning = false;

        // Sync the index in case the scene was loaded from outside (e.g. editor play mode starting in Stars).
        for (int i = 0; i < sceneNames.Length; i++)
            if (sceneNames[i] == scene.name) { _currentIndex = i; break; }

        ApplySkybox(scene.name);
    }

    void ApplySkybox(string sceneName)
    {
        Material sky = null;

        if (sceneName == "AstroTech - Stars" && starsSkybox != null)
            sky = starsSkybox;
        else if (solarSystemSkybox != null)
            sky = solarSystemSkybox;

        if (sky != null)
        {
            RenderSettings.skybox = sky;
            DynamicGI.UpdateEnvironment();
        }
    }
}
