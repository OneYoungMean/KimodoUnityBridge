using System;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(KimodoBVHLoader))]
public class KimodoBVHLoaderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        KimodoBVHLoader loader = (KimodoBVHLoader)target;

        GUILayout.Space(8);
        if (GUILayout.Button("Build Preview From BVH"))
        {
            try
            {
                loader.BuildPreviewFromFile();
                Debug.Log("[KimodoBVHLoader] Preview built successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[KimodoBVHLoader] Build failed: " + ex.Message + "\n" + ex);
            }
        }

        if (GUILayout.Button("Play"))
        {
            if (loader.builtRoot != null)
            {
                Animation anim = loader.builtRoot.GetComponent<Animation>();
                if (anim != null && anim.clip != null)
                {
                    anim.Play(anim.clip.name);
                }
            }
        }

        if (GUILayout.Button("Stop"))
        {
            if (loader.builtRoot != null)
            {
                Animation anim = loader.builtRoot.GetComponent<Animation>();
                if (anim != null)
                {
                    anim.Stop();
                }
            }
        }

        if (GUILayout.Button("Clear Preview"))
        {
            if (loader.builtRoot != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(loader.builtRoot);
                }
                else
                {
                    DestroyImmediate(loader.builtRoot);
                }
                loader.builtRoot = null;
                loader.builtClip = null;
            }
        }
    }
}
