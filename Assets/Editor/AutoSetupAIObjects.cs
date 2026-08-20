using System;
using UnityEngine;
using UnityEditor;
using VRT.Pilots.Common;

public static class AutoSetupAIObjects
{
    [MenuItem("Tools/AI/Setup Selected AI Objects")]
    public static void SetupSelectedObjects()
    {
        GameObject[] selectedObjects = Selection.gameObjects;

        if (selectedObjects.Length == 0)
        {
            Debug.LogWarning("Please select one or more GameObjects.");
            return;
        }

        // Common SceneStateStore used by all objects
        SceneStateStore sceneStateStore =
            UnityEngine.Object.FindFirstObjectByType<SceneStateStore>();

        if (sceneStateStore == null)
        {
            Debug.LogWarning(
                "No SceneStateStore found in the scene."
            );
        }


        foreach (GameObject go in selectedObjects)
        {
            Undo.RegisterCompleteObjectUndo(
                go,
                "Setup AI Object"
            );


            // =====================================================
            // 1. BOX COLLIDER
            // =====================================================

            BoxCollider boxCollider =
                go.GetComponent<BoxCollider>();

            if (boxCollider == null)
            {
                boxCollider =
                    Undo.AddComponent<BoxCollider>(go);
            }


            // =====================================================
            // 2. AI CONTROLLABLE
            // =====================================================

            AIControllable ai =
                go.GetComponent<AIControllable>();

            if (ai == null)
            {
                ai =
                    Undo.AddComponent<AIControllable>(go);
            }


            // Find renderer belonging to THIS GameObject
            Renderer renderer =
                go.GetComponent<Renderer>();

            if (renderer == null)
            {
                renderer =
                    go.GetComponentInChildren<Renderer>(true);
            }


            if (renderer != null)
            {
                // AI Controllable -> Target Renderer
                ai.targetRenderer = renderer;
            }
            else
            {
                Debug.LogWarning(
                    $"No Renderer found on {go.name}",
                    go
                );
            }


            // =====================================================
            // 3. PERSISTABLE AI OBJECT
            // =====================================================

            PersistableAIObject persistable =
                go.GetComponent<PersistableAIObject>();

            if (persistable == null)
            {
                persistable =
                    Undo.AddComponent<PersistableAIObject>(go);
            }


            // Controllable = AIControllable on SAME GameObject
            persistable.controllable = ai;


            // Generate unique object ID
            // ONLY if there isn't already one
            if (string.IsNullOrWhiteSpace(persistable.id))
            {
                persistable.id =
                    Guid.NewGuid().ToString("N");
            }


            // =====================================================
            // 4. NETWORKED AI OBJECT SYNC
            // =====================================================

            NetworkedAIObjectSync network =
                go.GetComponent<NetworkedAIObjectSync>();

            if (network == null)
            {
                network =
                    Undo.AddComponent<NetworkedAIObjectSync>(go);
            }


            // AI = same AIControllable
            network.ai = ai;

            // Target Renderer = renderer of same object
            network.targetRenderer = renderer;

            // Scene State Store = common global object
            network.sceneStateStore = sceneStateStore;


            // =====================================================
            // 5. SET "NO AUTO CREATE NETWORK ID" CHECKBOX
            // =====================================================

            SetNetworkIdCheckbox(network);


            // =====================================================
            // SAVE CHANGES
            // =====================================================

            EditorUtility.SetDirty(ai);
            EditorUtility.SetDirty(persistable);
            EditorUtility.SetDirty(network);
            EditorUtility.SetDirty(go);

            PrefabUtility.RecordPrefabInstancePropertyModifications(ai);
            PrefabUtility.RecordPrefabInstancePropertyModifications(persistable);
            PrefabUtility.RecordPrefabInstancePropertyModifications(network);


            Debug.Log(
                $"Configured: {go.name} | ID: {persistable.id}",
                go
            );
        }


        // Save scene
        UnityEditor.SceneManagement.EditorSceneManager
            .MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager
                    .GetActiveScene()
            );

        Debug.Log(
            $"Finished setting up {selectedObjects.Length} objects."
        );
    }



    // =============================================================
    // NETWORK ID CHECKBOX
    //
    // NetworkedAIObjectSync inherits NetworkIdBehaviour.
    // We don't have the source of NetworkIdBehaviour here,
    // so this searches its serialized fields safely.
    // =============================================================

    private static void SetNetworkIdCheckbox(
        NetworkedAIObjectSync network)
    {
        SerializedObject so =
            new SerializedObject(network);

        SerializedProperty property =
            so.GetIterator();

        bool enterChildren = true;

        while (property.NextVisible(enterChildren))
        {
            enterChildren = false;

            string display =
                property.displayName
                    .ToLowerInvariant()
                    .Replace(" ", "");

            /*
             * Looking for something displayed like:
             *
             * "No Auto Create NetworkId"
             *
             * which is what your screenshot appears to show.
             */
            if (
                display.Contains("noautocreate") &&
                display.Contains("network")
                &&
                property.propertyType ==
                SerializedPropertyType.Boolean
            )
            {
                // CHECK the checkbox
                property.boolValue = true;

                Debug.Log(
                    $"Checked network-ID option for {network.name}: " +
                    property.displayName
                );

                break;
            }
        }

        so.ApplyModifiedProperties();
    }
}