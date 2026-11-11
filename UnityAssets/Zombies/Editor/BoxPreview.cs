using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Renders the box closed, open, and with the weapon pivot risen, from the front and a corner, to logs/preview_box_*.png,
/// so the import can be checked without the game. The weapon is a stand-in bar where the gun will hang.
/// Batch: Unity.exe -batchmode -quit -projectPath ... -executeMethod BoxPreview.RenderBatch
/// </summary>
public static class BoxPreview
{
    public static void RenderBatch()
    {
        var lightGo = new GameObject("light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 0.9f;
        lightGo.transform.rotation = Quaternion.Euler(40f, 150f, 0f);
        RenderSettings.ambientLight = new Color(0.3f, 0.3f, 0.3f);

        var camGo = new GameObject("cam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.12f, 0.13f, 0.15f);
        cam.fieldOfView = 40f;
        var rt = new RenderTexture(768, 768, 24);
        cam.targetTexture = rt;
        Directory.CreateDirectory("../logs");

        GameObject box = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(BoxImporter.PrefabPath));
        Bounds bounds = box.GetComponentsInChildren<Renderer>().First().bounds;
        Transform weapon = box.transform.Find("WeaponPivot/tag_origin");
        var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.transform.SetParent(weapon, false);
        bar.transform.localScale = new Vector3(0.6f, 0.08f, 0.04f);
        bar.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.9f, 0.4f, 0.1f) };

        float dist = bounds.extents.magnitude * 3f;
        Sample(box, "zm_box_idle", 0f);
        Sample(box.transform.Find("WeaponPivot").gameObject, "zm_box_weaponpivot_down", 0f);
        GameObject baked = BakePose(box);
        Shoot(cam, rt, bounds, dist, new Vector3(0f, 0.25f, 1f), "box_front");
        Shoot(cam, rt, bounds, dist, new Vector3(0.8f, 0.4f, 0.8f), "box_corner");
        Shoot(cam, rt, bounds, dist, new Vector3(0f, 0.25f, -1f), "box_back");
        Unbake(box, baked);

        Sample(box, "zm_box_opened", 0f);
        Sample(box.transform.Find("WeaponPivot").gameObject, "zm_box_weaponpivot_up", 0f);
        baked = BakePose(box);
        Bounds open = new Bounds(bounds.center + Vector3.up * 0.4f, bounds.size + Vector3.up * 0.8f);
        Shoot(cam, rt, open, dist * 1.6f, new Vector3(0f, 0.6f, -1f), "box_open_front");
        Shoot(cam, rt, open, dist * 1.6f, new Vector3(0.8f, 0.5f, -0.8f), "box_open_corner");
        Unbake(box, baked);

        // The teddy, risen (the rise anim at 6 s), weapon bar out of the way
        bar.SetActive(false);
        Transform teddy = box.transform.Find("TeddyPivot/tag_origin/Teddy");
        if (teddy != null)
        {
            teddy.gameObject.SetActive(true);
            Sample(box.transform.Find("TeddyPivot").gameObject, "zm_box_teddypivot_rise", 0.5f);
            baked = BakePose(box);
            Bounds withTeddy = new Bounds(bounds.center + Vector3.up * 1.2f, bounds.size + Vector3.up * 2.4f);
            Shoot(cam, rt, withTeddy, dist * 1.8f, new Vector3(0f, 0.2f, -1f), "box_teddy_front");
            Shoot(cam, rt, withTeddy, dist * 1.4f, new Vector3(0.8f, 0.3f, 0.8f), "box_teddy_corner");
            Unbake(box, baked);
            Debug.Log($"[Zombies] Teddy at {teddy.position}, facing {teddy.forward}");
        }

        Debug.Log($"[Zombies] Box bounds {bounds.size} centre {bounds.center}; weapon pivot up at {weapon.position}");

        // The fake (the empty spot), beside where the box was
        var fakePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BoxImporter.FakePrefabPath);
        if (fakePrefab != null)
        {
            box.SetActive(false);
            GameObject fake = Object.Instantiate(fakePrefab);
            GameObject bakedFake = BakePose(fake);
            Shoot(cam, rt, bounds, dist * 1.3f, new Vector3(0f, 0.5f, -1f), "box_fake_front");
            Shoot(cam, rt, bounds, dist * 1.3f, new Vector3(0.8f, 0.5f, -0.8f), "box_fake_corner");
            Object.DestroyImmediate(bakedFake);
            Object.DestroyImmediate(fake);
            box.SetActive(true);
        }
        Object.DestroyImmediate(box);

        // The barricade: from the outside (the front, where zombies come from) and a corner
        var windowPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BoxImporter.BarricadePrefabPath);
        if (windowPrefab != null)
        {
            GameObject window = Object.Instantiate(windowPrefab);
            Bounds wb = new Bounds(window.transform.position, Vector3.zero);
            bool first = true;
            foreach (Renderer r in window.GetComponentsInChildren<Renderer>())
            {
                if (first) { wb = r.bounds; first = false; }
                else wb.Encapsulate(r.bounds);
            }
            float wd = wb.extents.magnitude * 2.6f;
            Shoot(cam, rt, wb, wd, new Vector3(0f, 0.1f, 1f), "window_front");
            Shoot(cam, rt, wb, wd, new Vector3(0.9f, 0.3f, 0.6f), "window_corner");
            Shoot(cam, rt, wb, wd, new Vector3(1f, 0.05f, 0f), "window_side");
            Debug.Log($"[Zombies] Window bounds {wb.size} centre {wb.center}");
            Object.DestroyImmediate(window);
        }
    }

    private static void Sample(GameObject go, string clipName, float t)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>($"Assets/Zombies/Generated/{clipName}.anim");
        if (clip == null)
        {
            Debug.LogWarning($"[Zombies] Preview: no clip {clipName}");
            return;
        }
        clip.SampleAnimation(go, t);
    }

    /// <summary>
    /// Batch mode doesn't re-skin a SkinnedMeshRenderer between sampling and Render: draw a baked copy of the posed
    /// mesh instead (and say where the lid's hinge points).
    /// </summary>
    private static GameObject BakePose(GameObject box)
    {
        Transform hinge = box.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == "j_hinge");
        if (hinge != null)
            Debug.Log($"[Zombies] Preview: j_hinge local rotation {hinge.localRotation.eulerAngles}");
        var baked = new GameObject("baked");
        foreach (SkinnedMeshRenderer smr in box.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mesh = new Mesh();
            smr.BakeMesh(mesh);
            var go = new GameObject(smr.name);
            go.transform.SetParent(baked.transform, false);
            go.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials;
            smr.enabled = false;
        }
        return baked;
    }

    private static void Unbake(GameObject box, GameObject baked)
    {
        Object.DestroyImmediate(baked);
        foreach (SkinnedMeshRenderer smr in box.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            smr.enabled = true;
    }

    private static void Shoot(Camera cam, RenderTexture rt, Bounds b, float dist, Vector3 dir, string name)
    {
        cam.transform.position = b.center + dir.normalized * dist;
        cam.transform.LookAt(b.center);
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes($"../logs/preview_{name}.png", tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
    }
}
