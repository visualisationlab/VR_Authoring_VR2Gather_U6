using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Persists AI-generated C# behaviours to disk and re-attaches them on scene load.
/// Compiles source at runtime in the Unity Editor using Roslyn loaded from a local folder.
/// </summary>
public class RuntimeBehaviourRegistry : MonoBehaviour
{
    public static RuntimeBehaviourRegistry Instance { get; private set; }

    string RegistryPath => Path.Combine(Application.persistentDataPath, "runtime_behaviours.json");
    string ScriptsDir => Path.Combine(Application.persistentDataPath, "GeneratedScripts");

    [Header("Debug")]
    public bool logResults = true;

    [Header("Roslyn")]
    [Tooltip("Folder containing Microsoft.CodeAnalysis*.dll and related Roslyn dependencies.")]
    public string roslynFolder = @"C:\Users\Ashutosh\Desktop\Work\0_UvA\0_Unity_Projects\Unity-Roslyn";

    [Serializable]
    class BehaviourRecord
    {
        public string id;
        public string targetName;
        public string className;
        public string scriptFileName;
        public string createdAt;
    }

    [Serializable]
    class Registry
    {
        public List<BehaviourRecord> items = new List<BehaviourRecord>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        Directory.CreateDirectory(ScriptsDir);
    }

    void Start()
    {
        StartCoroutine(ReattachNextFrame());
    }

    // Required namespaces every AI-generated MonoBehaviour needs.
    // Prepended automatically if missing so Roslyn never hits CS0246.
    static readonly string[] kRequiredUsings =
    {
        "using System;",
        "using System.Collections;",
        "using System.Collections.Generic;",
        "using UnityEngine;",
        "using UnityEngine.Networking;",
        "using System.IO;",
    };

    static string InjectUsings(string code)
    {
        // Normalise the AI-generated code to Windows line endings (CR LF)
        // so Visual Studio doesn't show the "Inconsistent Line Endings" dialog.
        code = code.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        var sb = new StringBuilder();
        foreach (var u in kRequiredUsings)
            if (!code.Contains(u))
                sb.Append(u + "\r\n");
        sb.Append(code);
        return sb.ToString();
    }

    public bool RegisterAndAttach(GameObject target, string csharpCode, out string compileError)
    {
        compileError = null;

        if (target == null || string.IsNullOrWhiteSpace(csharpCode))
            return false;

        // Always attach to the root of the hierarchy, not a child mesh/collider.
        // This prevents scripts landing on Mesh1.0 or similar child objects.
        target = ResolveRootTarget(target);

        // Ensure all required namespaces are present before handing to Roslyn
        csharpCode = InjectUsings(csharpCode);

        string className = ExtractClassName(csharpCode);
        if (string.IsNullOrEmpty(className))
        {
            Debug.LogError("[Registry] No class name found.");
            return false;
        }

        // Remove any stale component with this class name already on the object
        // (e.g. the old broken script that was attached without a prefab)
        RemoveStaleComponentByName(target, className);

        Type t = CompileType(csharpCode, className, out string errors);
        if (t == null)
        {
            Debug.LogError("[Registry] Compile failed:\n" + errors);
            return false;
        }

        AttachType(target, t);

        if (logResults)
            Debug.Log($"[Registry] Attached '{className}' to '{target.name}'");

        string id = Guid.NewGuid().ToString("N");
        string fileName = id + "_" + SanitiseFilename(className) + ".cs";
        // Save the injected source so re-compilation on reload also works
        // Write with UTF-8 BOM and Windows line endings — keeps Visual Studio happy
        File.WriteAllText(Path.Combine(ScriptsDir, fileName), csharpCode, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var reg = LoadRegistry();
        reg.items.Add(new BehaviourRecord
        {
            id = id,
            targetName = target.name,
            className = className,
            scriptFileName = fileName,
            createdAt = DateTime.Now.ToString("o")
        });
        SaveRegistry(reg);

        return true;
    }

    public void ClearBehavioursFor(string targetName)
    {
        var reg = LoadRegistry();
        reg.items.RemoveAll(r => r.targetName == targetName);
        SaveRegistry(reg);
    }

    IEnumerator ReattachNextFrame()
    {
        yield return null;

        var reg = LoadRegistry();
        if (reg.items.Count == 0)
            yield break;

        Debug.Log($"[Registry] Re-attaching {reg.items.Count} saved behaviour(s)...");

        foreach (var rec in reg.items)
        {
            string fullPath = Path.Combine(ScriptsDir, rec.scriptFileName);
            if (!File.Exists(fullPath))
            {
                Debug.LogWarning($"[Registry] Missing script file: {fullPath}");
                continue;
            }

            string code = File.ReadAllText(fullPath, Encoding.UTF8);
            GameObject go = GameObject.Find(rec.targetName);
            if (go == null)
            {
                Debug.LogWarning($"[Registry] Target '{rec.targetName}' not found in scene.");
                continue;
            }

            Type t = CompileType(code, rec.className, out string errors);
            if (t == null)
            {
                Debug.LogError($"[Registry] Re-compile failed for '{rec.className}':\n{errors}");
                continue;
            }

            AttachType(go, t);

            if (logResults)
                Debug.Log($"[Registry] Re-attached '{rec.className}' to '{rec.targetName}'");
        }
    }

    Type CompileType(string source, string className, out string errors)
    {
        errors = "";

#if UNITY_EDITOR
        return RoslynCompile(source, className, ref errors);
#else
        errors = "Runtime compilation is only supported in the Unity Editor.";
        return null;
#endif
    }

#if UNITY_EDITOR
    Type RoslynCompile(string source, string className, ref string errors)
    {
        try
        {
            if (!EnsureRoslynLoaded(ref errors))
                return null;

            Assembly roslynCore = FindLoadedAssembly("Microsoft.CodeAnalysis");
            Assembly roslynCSharp = FindLoadedAssembly("Microsoft.CodeAnalysis.CSharp");

            if (roslynCore == null || roslynCSharp == null)
            {
                errors = "Roslyn assemblies could not be loaded.";
                return null;
            }

            Type tSyntaxTree = roslynCSharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
            Type tCompilation = roslynCSharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
            Type tCompileOpts = roslynCSharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
            Type tOutputKind = roslynCore.GetType("Microsoft.CodeAnalysis.OutputKind");
            Type tMetaRef = roslynCore.GetType("Microsoft.CodeAnalysis.MetadataReference");
            Type tSyntaxTreeBase = roslynCore.GetType("Microsoft.CodeAnalysis.SyntaxTree");

            if (tSyntaxTree == null || tCompilation == null || tCompileOpts == null || tOutputKind == null || tMetaRef == null)
            {
                errors = "Could not resolve Roslyn types.";
                return null;
            }

            object syntaxTree = ParseSourceToSyntaxTree(tSyntaxTree, source);
            if (syntaxTree == null)
            {
                errors = "Failed to parse generated C# into a syntax tree.";
                return null;
            }

            MethodInfo createFromFile = FindCreateFromFileMethod(tMetaRef);
            if (createFromFile == null)
            {
                errors = "MetadataReference.CreateFromFile not found.";
                return null;
            }

            List<object> references = BuildMetadataReferences(createFromFile);
            if (references.Count == 0)
            {
                errors = "No assembly references were collected for Roslyn compilation.";
                return null;
            }

            Array refsArray = Array.CreateInstance(tMetaRef, references.Count);
            for (int i = 0; i < references.Count; i++)
                refsArray.SetValue(references[i], i);

            object outputKindDll = Enum.Parse(tOutputKind, "DynamicallyLinkedLibrary");
            object compileOptions = CreateCompilationOptions(tCompileOpts, tOutputKind, outputKindDll);
            if (compileOptions == null)
            {
                errors = "Could not create CSharpCompilationOptions.";
                return null;
            }

            MethodInfo createMethod = FindCompilationCreateMethod(tCompilation);
            if (createMethod == null)
            {
                errors = "CSharpCompilation.Create not found.";
                return null;
            }

            Array treesArray = Array.CreateInstance(tSyntaxTreeBase, 1);
            treesArray.SetValue(syntaxTree, 0);

            object compilation = createMethod.Invoke(null, new object[]
            {
                "RuntimeBehaviour_" + Guid.NewGuid().ToString("N"),
                treesArray,
                refsArray,
                compileOptions
            });

            if (compilation == null)
            {
                errors = "CSharpCompilation.Create returned null.";
                return null;
            }

            using (var ms = new MemoryStream())
            {
                MethodInfo emitMethod = FindEmitMethod(compilation.GetType());
                if (emitMethod == null)
                {
                    errors = "Emit(Stream) method not found.";
                    return null;
                }

                object[] emitArgs = new object[emitMethod.GetParameters().Length];
                emitArgs[0] = ms;

                object emitResult = emitMethod.Invoke(compilation, emitArgs);
                if (emitResult == null)
                {
                    errors = "Emit returned null.";
                    return null;
                }

                PropertyInfo successProp = emitResult.GetType().GetProperty("Success");
                PropertyInfo diagnosticsProp = emitResult.GetType().GetProperty("Diagnostics");

                bool success = successProp != null && (bool)successProp.GetValue(emitResult);
                if (!success)
                {
                    var sb = new StringBuilder();

                    if (diagnosticsProp != null)
                    {
                        var diagnostics = diagnosticsProp.GetValue(emitResult) as System.Collections.IEnumerable;
                        if (diagnostics != null)
                        {
                            foreach (var d in diagnostics)
                                sb.AppendLine(d.ToString());
                        }
                    }

                    errors = sb.Length > 0 ? sb.ToString() : "Roslyn emit failed with unknown diagnostics.";
                    return null;
                }

                ms.Seek(0, SeekOrigin.Begin);
                Assembly compiledAssembly = Assembly.Load(ms.ToArray());

                Type resolvedType = compiledAssembly.GetType(className);
                if (resolvedType != null)
                    return resolvedType;

                Type fallbackType = compiledAssembly
                    .GetTypes()
                    .FirstOrDefault(t => typeof(MonoBehaviour).IsAssignableFrom(t) && !t.IsAbstract);

                if (fallbackType == null)
                {
                    errors = $"Compiled assembly loaded, but no MonoBehaviour type was found. Expected class: {className}";
                    return null;
                }

                return fallbackType;
            }
        }
        catch (Exception ex)
        {
            errors = ex.ToString();
            return null;
        }
    }

    bool EnsureRoslynLoaded(ref string errors)
    {
        string[] requiredDlls =
        {
            "Microsoft.CodeAnalysis.dll",
            "Microsoft.CodeAnalysis.CSharp.dll",
            "System.Collections.Immutable.dll",
            "System.Reflection.Metadata.dll",
            "netstandard.dll"
        };

        foreach (string dllName in requiredDlls)
        {
            string assemblySimpleName = Path.GetFileNameWithoutExtension(dllName);
            if (FindLoadedAssembly(assemblySimpleName) != null)
                continue;

            string fullPath = Path.Combine(roslynFolder, dllName);
            if (!File.Exists(fullPath))
            {
                errors = $"Missing Roslyn dependency: {fullPath}";
                return false;
            }

            try
            {
                Assembly.LoadFrom(fullPath);
            }
            catch (Exception ex)
            {
                errors = $"Failed loading Roslyn DLL '{fullPath}':\n{ex}";
                return false;
            }
        }

        return true;
    }

    static Assembly FindLoadedAssembly(string simpleName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm.GetName().Name == simpleName)
                    return asm;
            }
            catch
            {
            }
        }

        return null;
    }

    static object ParseSourceToSyntaxTree(Type tSyntaxTree, string source)
    {
        MethodInfo parseText = tSyntaxTree.GetMethod(
            "ParseText",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string) },
            null
        );

        if (parseText != null)
            return parseText.Invoke(null, new object[] { source });

        foreach (var m in tSyntaxTree.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "ParseText")
                continue;

            var parameters = m.GetParameters();
            if (parameters.Length == 0)
                continue;

            if (parameters[0].ParameterType != typeof(string))
                continue;

            object[] args = new object[parameters.Length];
            args[0] = source;
            return m.Invoke(null, args);
        }

        return null;
    }

    static MethodInfo FindCreateFromFileMethod(Type tMetaRef)
    {
        foreach (var m in tMetaRef.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "CreateFromFile")
                continue;

            var p = m.GetParameters();
            if (p.Length >= 1 && p[0].ParameterType == typeof(string))
                return m;
        }

        return null;
    }

    static object[] BuildCreateFromFileArgs(MethodInfo method, string path)
    {
        var parameters = method.GetParameters();
        var args = new object[parameters.Length];
        args[0] = path;

        for (int i = 1; i < parameters.Length; i++)
        {
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        }

        return args;
    }

    List<object> BuildMetadataReferences(MethodInfo createFromFile)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<object>();

        void TryAddReference(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!File.Exists(path)) return;
                if (!seen.Add(path)) return;

                object r = createFromFile.Invoke(null, BuildCreateFromFileArgs(createFromFile, path));
                if (r != null)
                    refs.Add(r);
            }
            catch
            {
            }
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm.IsDynamic) continue;
                if (string.IsNullOrWhiteSpace(asm.Location)) continue;
                TryAddReference(asm.Location);
            }
            catch
            {
            }
        }

        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (!string.IsNullOrEmpty(projectRoot))
        {
            string scriptAssemblies = Path.Combine(projectRoot, "Library", "ScriptAssemblies");
            TryAddReference(Path.Combine(scriptAssemblies, "Assembly-CSharp.dll"));
            TryAddReference(Path.Combine(scriptAssemblies, "Unity.ML-Agents.dll"));
        }

        string editorManaged = Path.Combine(EditorApplication.applicationContentsPath, "Managed");
        TryAddReference(Path.Combine(editorManaged, "UnityEngine", "UnityEngine.CoreModule.dll"));
        TryAddReference(Path.Combine(editorManaged, "UnityEngine", "UnityEngine.PhysicsModule.dll"));
        TryAddReference(Path.Combine(editorManaged, "UnityEngine", "UnityEngine.InputLegacyModule.dll"));
        TryAddReference(Path.Combine(editorManaged, "UnityEngine", "UnityEngine.InputModule.dll"));
        TryAddReference(Path.Combine(editorManaged, "netstandard.dll"));

        TryAddReference(typeof(object).Assembly.Location);
        TryAddReference(typeof(MonoBehaviour).Assembly.Location);

        return refs;
    }

    static object CreateCompilationOptions(Type tCompileOpts, Type tOutputKind, object outputKindDll)
    {
        foreach (var ctor in tCompileOpts.GetConstructors())
        {
            var p = ctor.GetParameters();
            if (p.Length == 0) continue;
            if (p[0].ParameterType != tOutputKind) continue;

            object[] args = new object[p.Length];
            args[0] = outputKindDll;
            return ctor.Invoke(args);
        }

        return null;
    }

    static MethodInfo FindCompilationCreateMethod(Type tCompilation)
    {
        foreach (var m in tCompilation.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Create")
                continue;

            var p = m.GetParameters();
            if (p.Length >= 4)
                return m;
        }

        return null;
    }

    static MethodInfo FindEmitMethod(Type compilationType)
    {
        foreach (var m in compilationType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "Emit")
                continue;

            var p = m.GetParameters();
            if (p.Length > 0 && p[0].ParameterType == typeof(Stream))
                return m;
        }

        return null;
    }
#endif

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Walks up the hierarchy to find the most sensible root to attach to.
    /// Prefers a PersistableAIObject ancestor; otherwise uses the topmost
    /// non-scene-root parent so we never land on a child mesh like Mesh1.0.
    /// </summary>
    static GameObject ResolveRootTarget(GameObject go)
    {
        if (go == null) return go;

        // Prefer PersistableAIObject in parent chain
        var persist = go.GetComponentInParent<PersistableAIObject>();
        if (persist != null) return persist.gameObject;

        // Otherwise walk to the topmost parent that isn't the scene root
        Transform t = go.transform;
        while (t.parent != null)
            t = t.parent;

        // t is now the scene-root object — return our original go's top-level child
        // i.e. the direct child of the scene root, or go itself if it is that child.
        Transform target = go.transform;
        while (target.parent != null && target.parent.parent != null)
            target = target.parent;

        return target.gameObject;
    }

    /// <summary>
    /// Removes any component on the GameObject (or its children) whose
    /// type name matches className — cleans up stale AI-generated scripts.
    /// </summary>
    static void RemoveStaleComponentByName(GameObject go, string className)
    {
        foreach (var comp in go.GetComponentsInChildren<Component>(true))
        {
            if (comp == null) continue;
            if (comp.GetType().Name == className)
            {
                Debug.Log($"[Registry] Removing stale '{className}' from '{comp.gameObject.name}'");
                Destroy(comp);
            }
        }
    }

    static void AttachType(GameObject target, Type t)
    {
        var existing = target.GetComponent(t);
        if (existing != null)
            Destroy(existing);

        target.AddComponent(t);
    }

    public static string ParseCodeFromGPTResponse(string gptResponse)
    {
        foreach (var pattern in new[]
        {
            @"```csharp([\s\S]*?)```",
            @"```cs([\s\S]*?)```",
            @"```c#([\s\S]*?)```",
            @"```([\s\S]*?)```"
        })
        {
            var m = Regex.Match(gptResponse, pattern, RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Groups[1].Value.Trim();
        }

        return gptResponse.Contains("MonoBehaviour") ? gptResponse.Trim() : null;
    }

    static string ExtractClassName(string code)
    {
        var m = Regex.Match(code, @"\bclass\s+(\w+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    static string SanitiseFilename(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    Registry LoadRegistry()
    {
        if (!File.Exists(RegistryPath))
            return new Registry();

        try
        {
            return JsonUtility.FromJson<Registry>(File.ReadAllText(RegistryPath)) ?? new Registry();
        }
        catch
        {
            return new Registry();
        }
    }

    void SaveRegistry(Registry reg)
    {
        File.WriteAllText(RegistryPath, JsonUtility.ToJson(reg, true));
    }
}