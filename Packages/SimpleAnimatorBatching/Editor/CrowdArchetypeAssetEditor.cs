using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;

namespace SimpleAnimatorBatching.Editor
{
    /// <summary>
    /// Guided inspector ("wizard") for CrowdArchetypeAsset: walks the user through assigning a source
    /// prefab, validates it (Animator present, at least one SkinnedMeshRenderer, URP active, pool size),
    /// helps create/assign a CrowdLit material, and bakes — with a clear gate so you can't bake an
    /// invalid setup and silently get nothing on screen.
    /// </summary>
    [CustomEditor(typeof(CrowdArchetypeAsset))]
    public class CrowdArchetypeAssetEditor : UnityEditor.Editor
    {
        const string CrowdLitShaderName = "Simple Animator Batching/Crowd Lit";

        SerializedProperty sourcePrefabProp;
        SerializedProperty materialProp;
        SerializedProperty maxInstancesProp;

        void OnEnable()
        {
            sourcePrefabProp = serializedObject.FindProperty("sourcePrefab");
            materialProp = serializedObject.FindProperty("material");
            maxInstancesProp = serializedObject.FindProperty("maxInstances");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var archetype = (CrowdArchetypeAsset)target;

            EditorGUILayout.LabelField("Crowd Archetype", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Bakes one animated prefab into data the batched crowd renderer can draw in a single " +
                "draw call. Assign the source prefab, pick a pool size and material, then Bake.",
                MessageType.None);

            // --- 1. Source ---------------------------------------------------------------------
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("1. Source", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(sourcePrefabProp,
                new GUIContent("Source Prefab", "A prefab/FBX with an Animator and a SkinnedMeshRenderer."));

            var messages = Validate(archetype);

            // --- 2. Pool -----------------------------------------------------------------------
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("2. Pool", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(maxInstancesProp,
                new GUIContent("Max Instances", "Fixed pool capacity. Size for your peak concurrent count."));

            // --- 3. Material -------------------------------------------------------------------
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("3. Material", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(materialProp,
                new GUIContent("Material", "A material using a Crowd Lit/Unlit shader (DOTS-instancing capable)."));
            if (materialProp.objectReferenceValue == null)
            {
                if (GUILayout.Button("Create & Assign CrowdLit Material"))
                    CreateAndAssignMaterial(archetype);
            }
            else if (!IsCrowdMaterial(materialProp.objectReferenceValue as Material))
            {
                EditorGUILayout.HelpBox(
                    "The assigned material's shader is not a Simple Animator Batching shader. It likely " +
                    "won't skin or batch correctly. Use a Crowd Lit/Unlit material.", MessageType.Warning);
            }

            serializedObject.ApplyModifiedProperties();

            // --- Validation summary ------------------------------------------------------------
            EditorGUILayout.Space();
            bool hasError = false;
            foreach (var m in messages)
            {
                EditorGUILayout.HelpBox(m.text, m.type);
                if (m.type == MessageType.Error) hasError = true;
            }

            // --- 4. Bake -----------------------------------------------------------------------
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("4. Bake", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(hasError))
            {
                if (GUILayout.Button(archetype.IsBaked ? "Re-bake From Source Prefab" : "Bake From Source Prefab",
                        GUILayout.Height(28)))
                {
                    try
                    {
                        CrowdArchetypeBaker.Bake(archetype);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"[Simple Animator Batching] Bake failed: {e.Message}");
                        EditorUtility.DisplayDialog("Bake failed", e.Message, "OK");
                    }
                }
            }
            if (hasError)
                EditorGUILayout.HelpBox("Fix the errors above before baking.", MessageType.None);

            // --- Status ------------------------------------------------------------------------
            EditorGUILayout.Space();
            if (archetype.IsBaked)
            {
                string entry = GetAnimatorEntryState(archetype.RuntimePrefab);
                string entryLine = entry != null
                    ? $"\nAnimator entry state: \"{entry}\" — set this as CrowdSpawner's Default State Name."
                    : "";
                EditorGUILayout.HelpBox(
                    $"Baked: {archetype.BoneCount} bones, mesh '{archetype.BakedMesh.name}', " +
                    $"runtime prefab '{archetype.RuntimePrefab.name}'." + entryLine,
                    MessageType.Info);
                if (archetype.Material == null)
                    EditorGUILayout.HelpBox(
                        "No material assigned — instances will animate but not render.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox("Not baked yet.", MessageType.None);
            }
        }

        struct Msg { public MessageType type; public string text; }

        static List<Msg> Validate(CrowdArchetypeAsset archetype)
        {
            var list = new List<Msg>();

            if (!IsUniversalPipelineActive())
                list.Add(new Msg { type = MessageType.Warning,
                    text = "The active render pipeline does not look like URP. This package targets URP; " +
                           "rendering may not work on other pipelines." });

            var src = archetype.sourcePrefab;
            if (src == null)
            {
                list.Add(new Msg { type = MessageType.Error, text = "Assign a Source Prefab to bake." });
                return list;
            }

            if (src.GetComponentInChildren<Animator>(true) == null)
                list.Add(new Msg { type = MessageType.Error,
                    text = $"'{src.name}' has no Animator. Each instance needs an Animator to drive it." });

            var smrs = src.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs.Length == 0)
                list.Add(new Msg { type = MessageType.Error,
                    text = $"'{src.name}' has no SkinnedMeshRenderer to bake." });
            else if (smrs.Length > 1)
            {
                var main = smrs[0];
                for (int i = 1; i < smrs.Length; i++)
                    if (smrs[i].sharedMesh != null &&
                        (main.sharedMesh == null || smrs[i].sharedMesh.vertexCount > main.sharedMesh.vertexCount))
                        main = smrs[i];
                list.Add(new Msg { type = MessageType.Info,
                    text = $"'{src.name}' has {smrs.Length} SkinnedMeshRenderers. v1 bakes a single mesh: " +
                           $"'{main.name}' (most vertices) will be baked, the rest stripped." });
            }

            if (archetype.maxInstances <= 0)
                list.Add(new Msg { type = MessageType.Error, text = "Max Instances must be at least 1." });

            return list;
        }

        static bool IsUniversalPipelineActive()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            return rp != null && rp.GetType().Name.Contains("Universal");
        }

        static bool IsCrowdMaterial(Material mat)
        {
            return mat != null && mat.shader != null &&
                   mat.shader.name.StartsWith("Simple Animator Batching/");
        }

        static void CreateAndAssignMaterial(CrowdArchetypeAsset archetype)
        {
            var shader = Shader.Find(CrowdLitShaderName);
            if (shader == null)
            {
                EditorUtility.DisplayDialog("Shader missing",
                    $"Could not find shader '{CrowdLitShaderName}'.", "OK");
                return;
            }

            string archetypePath = AssetDatabase.GetAssetPath(archetype);
            string folder = Path.GetDirectoryName(archetypePath);
            string baseName = Path.GetFileNameWithoutExtension(archetypePath);
            string matPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.Combine(folder, baseName + "_Material.mat").Replace('\\', '/'));

            var mat = new Material(shader) { name = baseName + "_Material" };
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.SaveAssets();

            var so = new SerializedObject(archetype);
            so.FindProperty("material").objectReferenceValue = mat;
            so.ApplyModifiedProperties();

            Debug.Log($"[Simple Animator Batching] Created material '{matPath}' and assigned it to '{archetype.name}'.");
        }

        static string GetAnimatorEntryState(GameObject prefab)
        {
            if (prefab == null) return null;
            var animator = prefab.GetComponentInChildren<Animator>(true);
            var controller = animator != null ? animator.runtimeAnimatorController as AnimatorController : null;
            if (controller == null || controller.layers.Length == 0) return null;
            var sm = controller.layers[0].stateMachine;
            return sm != null && sm.defaultState != null ? sm.defaultState.name : null;
        }
    }
}
