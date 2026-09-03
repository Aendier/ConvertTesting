using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Lists Image/RawImage references in a prefab and creates a copy with Sprite replacements.
/// </summary>
public sealed class PrefabImageReplacerWindow : EditorWindow
{
    private const string OutputSuffix = "_Replaced";
    private const float PreviewSize = 44f;

    private GameObject prefab;
    private string searchText = string.Empty;
    private Vector2 scrollPosition;
    private List<ImageSlot> slots = new List<ImageSlot>();
    private List<ScanProblem> scanProblems = new List<ScanProblem>();
    private Hash128 scannedDependencyHash;
    private int lastReviewIssueCount;
    private bool hasScanned;
    private bool isBusy;

    [MenuItem("Tools/Prefab Image Replacer")]
    public static void Open()
    {
        GetWindow<PrefabImageReplacerWindow>("Prefab Image Replacer");
    }

    private void OnInspectorUpdate()
    {
        if (prefab == null || isBusy)
        {
            return;
        }

        var path = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dependencyHash = AssetDatabase.GetAssetDependencyHash(path);
        if (hasScanned && dependencyHash != scannedDependencyHash)
        {
            ScanPrefab();
        }

        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Prefab 图片引用替换", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "按原始 Sprite/Texture 聚合列出 Image 和 RawImage 引用。配置替换 Sprite 后，将生成同目录下的 *_Replaced.prefab。",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();
        var selectedPrefab = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Prefab", "要扫描的 Prefab 资源"), prefab, typeof(GameObject), false);
        if (EditorGUI.EndChangeCheck())
        {
            prefab = selectedPrefab;
            ScanPrefab();
        }

        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            var newSearch = EditorGUILayout.TextField(
                searchText,
                GUI.skin.FindStyle("ToolbarSeachTextField") ?? GUI.skin.textField,
                GUILayout.ExpandWidth(true));
            if (!string.Equals(newSearch, searchText, StringComparison.Ordinal))
            {
                searchText = newSearch;
                Repaint();
            }

            if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(52f)))
            {
                ScanPrefab();
            }
        }

        if (prefab == null)
        {
            EditorGUILayout.HelpBox("请拖入一个 Prefab 资源。", MessageType.Warning);
            return;
        }

        var prefabPath = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            EditorGUILayout.HelpBox("当前对象不是项目中的 Prefab 资源。", MessageType.Error);
            return;
        }

        var outputPath = GetNextOutputPath(prefabPath);
        EditorGUILayout.LabelField("输出", outputPath, EditorStyles.miniLabel);

        if (scanProblems.Count > 0)
        {
            EditorGUILayout.HelpBox(
                string.Format("发现 {0} 个问题项。执行时需要逐项确认，问题引用会被跳过。", scanProblems.Count),
                MessageType.Warning);
        }

        EditorGUILayout.LabelField(
            string.Format("图片槽位：{0}    引用节点：{1}", slots.Count, slots.Sum(s => s.Usages.Count)),
            EditorStyles.miniBoldLabel);

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        foreach (var slot in slots)
        {
            if (!MatchesSearch(slot))
            {
                continue;
            }

            DrawSlot(slot);
        }

        if (slots.Count == 0 && scanProblems.Count == 0)
        {
            EditorGUILayout.HelpBox("没有找到 Image 或 RawImage 图片引用。", MessageType.Info);
        }

        EditorGUILayout.EndScrollView();

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(isBusy || slots.Count == 0))
            {
                if (GUILayout.Button("生成替换 Prefab", GUILayout.Width(150f), GUILayout.Height(28f)))
                {
                    PrepareApply();
                }
            }
        }
    }

    private void DrawSlot(ImageSlot slot)
    {
        slot.Expanded = EditorGUILayout.Foldout(
            slot.Expanded,
            string.Format("{0}  ({1} 个引用)", slot.DisplayName, slot.Usages.Count),
            true);
        if (!slot.Expanded)
        {
            return;
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPreview(slot.Source);
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(slot.SourcePath, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(
                        slot.SourceKind == SourceKind.Sprite ? "Image.sprite" : "RawImage.texture",
                        EditorStyles.miniLabel);
                }

                GUILayout.FlexibleSpace();
                EditorGUI.BeginChangeCheck();
                var replacement = (Sprite)EditorGUILayout.ObjectField(
                    new GUIContent("替换 Sprite"), slot.Replacement, typeof(Sprite), false, GUILayout.Width(230f));
                if (EditorGUI.EndChangeCheck())
                {
                    slot.Replacement = replacement;
                }
                if (replacement != null)
                {
                    DrawPreview(replacement);
                }
            }

            foreach (var usage in slot.Usages)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8f);
                    var state = usage.Writable ? string.Empty : "（嵌套 Prefab，只读）";
                    EditorGUILayout.LabelField(
                        usage.Path + "  [" + usage.ComponentProperty + "] " + state,
                        EditorStyles.miniLabel);
                }
            }
        }
    }

    private static void DrawPreview(UnityEngine.Object source)
    {
        var preview = source == null ? null : AssetPreview.GetAssetPreview(source);
        if (preview != null)
        {
            GUILayout.Label(preview, GUILayout.Width(PreviewSize), GUILayout.Height(PreviewSize));
        }
        else
        {
            GUILayout.Box(GUIContent.none, GUILayout.Width(PreviewSize), GUILayout.Height(PreviewSize));
        }
    }

    private bool MatchesSearch(ImageSlot slot)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var query = searchText.Trim();
        return slot.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || slot.SourcePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || slot.Usages.Any(u => u.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void ScanPrefab()
    {
        slots = new List<ImageSlot>();
        scanProblems = new List<ScanProblem>();
        hasScanned = false;

        if (prefab == null)
        {
            return;
        }

        var path = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        isBusy = true;
        PrefabOperationContext context = null;
        try
        {
            context = PrefabOperationContext.Open(prefab, path);
            var root = context.Root;
            var byKey = new Dictionary<string, ImageSlot>(StringComparer.Ordinal);
            foreach (var image in root.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite == null)
                {
                    scanProblems.Add(new ScanProblem(
                        BuildTransformPath(image.transform, root.transform),
                        "Image.sprite 为空或丢失引用"));
                    continue;
                }

                AddUsage(byKey, image.sprite, SourceKind.Sprite, image.gameObject,
                    BuildTransformPath(image.transform, root.transform), "Image.sprite", root);
            }

            foreach (var rawImage in root.GetComponentsInChildren<RawImage>(true))
            {
                if (rawImage.texture == null)
                {
                    scanProblems.Add(new ScanProblem(
                        BuildTransformPath(rawImage.transform, root.transform),
                        "RawImage.texture 为空或丢失引用"));
                    continue;
                }

                AddUsage(byKey, rawImage.texture, SourceKind.Texture, rawImage.gameObject,
                    BuildTransformPath(rawImage.transform, root.transform), "RawImage.texture", root);
            }

            slots = byKey.Values.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            scannedDependencyHash = AssetDatabase.GetAssetDependencyHash(path);
            hasScanned = true;
        }
        catch (Exception exception)
        {
            scanProblems.Add(new ScanProblem(path, "扫描失败：" + exception.Message));
            Debug.LogException(exception);
        }
        finally
        {
            if (context != null)
            {
                context.Dispose();
            }

            isBusy = false;
            Repaint();
        }
    }

    private static void AddUsage(
        Dictionary<string, ImageSlot> byKey,
        UnityEngine.Object source,
        SourceKind sourceKind,
        GameObject gameObject,
        string path,
        string componentProperty,
        GameObject prefabRoot)
    {
        var key = GetAssetKey(source);
        ImageSlot slot;
        if (!byKey.TryGetValue(key, out slot))
        {
            slot = new ImageSlot(key, source, sourceKind);
            byKey.Add(key, slot);
        }

        slot.Usages.Add(new ImageUsage(
            path,
            componentProperty,
            IsDirectlyWritable(gameObject, prefabRoot)));
    }

    private void PrepareApply()
    {
        if (prefab == null || slots.Count == 0)
        {
            return;
        }

        var issues = new List<ReviewIssue>();
        foreach (var problem in scanProblems)
        {
            issues.Add(new ReviewIssue(problem.Path, problem.Message));
        }

        foreach (var slot in slots)
        {
            if (slot.Replacement == null)
            {
                issues.Add(new ReviewIssue(slot.DisplayName, "未配置替换 Sprite（该槽位将跳过）"));
            }

            foreach (var usage in slot.Usages.Where(u => !u.Writable))
            {
                issues.Add(new ReviewIssue(
                    usage.Path,
                    usage.ComponentProperty + " 位于嵌套 Prefab，无法直接写回（该引用将跳过）"));
            }
        }

        lastReviewIssueCount = issues.Count;

        if (issues.Count > 0)
        {
            IssueReviewWindow.Show(issues, acknowledged =>
            {
                if (acknowledged)
                {
                    PerformApply();
                }
            });
        }
        else
        {
            PerformApply();
        }
    }

    private void PerformApply()
    {
        if (isBusy)
        {
            return;
        }

        var prefabPath = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(prefabPath))
        {
            return;
        }

        var replacementByKey = slots
            .Where(s => s.Replacement != null)
            .ToDictionary(s => s.Key, s => s.Replacement, StringComparer.Ordinal);

        isBusy = true;
        PrefabOperationContext context = null;
        try
        {
            context = PrefabOperationContext.Open(prefab, prefabPath);
            var root = context.Root;
            var replacedSlots = new HashSet<string>(StringComparer.Ordinal);
            var replacedNodes = 0;

            foreach (var image in root.GetComponentsInChildren<Image>(true))
            {
                var original = image.sprite;
                Sprite replacement;
                if (original == null || !replacementByKey.TryGetValue(GetAssetKey(original), out replacement)
                    || replacement == null || !IsDirectlyWritable(image.gameObject, root))
                {
                    continue;
                }

                image.sprite = replacement;
                EditorUtility.SetDirty(image);
                replacedSlots.Add(GetAssetKey(original));
                replacedNodes++;
            }

            foreach (var rawImage in root.GetComponentsInChildren<RawImage>(true))
            {
                var original = rawImage.texture;
                Sprite replacement;
                if (original == null || !replacementByKey.TryGetValue(GetAssetKey(original), out replacement)
                    || replacement == null || replacement.texture == null
                    || !IsDirectlyWritable(rawImage.gameObject, root))
                {
                    continue;
                }

                rawImage.texture = replacement.texture;
                EditorUtility.SetDirty(rawImage);
                replacedSlots.Add(GetAssetKey(original));
                replacedNodes++;
            }

            if (replacedNodes == 0)
            {
                EditorUtility.DisplayDialog("没有可替换的引用", "没有配置有效替换图，或所有引用都属于只读嵌套 Prefab。", "确定");
                return;
            }

            var outputPath = GetNextOutputPath(prefabPath);
            bool saveSucceeded;
            var savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, outputPath, out saveSucceeded);
            if (!saveSucceeded || savedPrefab == null)
            {
                EditorUtility.DisplayDialog("生成失败", "Unity 未能保存新 Prefab。请检查输出路径和资源状态。", "确定");
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = savedPrefab;
            EditorGUIUtility.PingObject(savedPrefab);
            EditorUtility.DisplayDialog(
                "生成完成",
                string.Format("输出：{0}\n替换槽位：{1}\n受影响节点：{2}\n问题项：{3}",
                    outputPath, replacedSlots.Count, replacedNodes, lastReviewIssueCount),
                "确定");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("生成失败", exception.Message, "确定");
        }
        finally
        {
            if (context != null)
            {
                context.Dispose();
            }

            isBusy = false;
            ScanPrefab();
        }
    }

    private static bool IsDirectlyWritable(GameObject gameObject, GameObject prefabRoot)
    {
        if (gameObject == null)
        {
            return false;
        }

        var nearestInstanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
        if (nearestInstanceRoot == null)
        {
            return true;
        }

        return nearestInstanceRoot == prefabRoot;
    }

    private static string BuildTransformPath(Transform transform, Transform root)
    {
        var names = new Stack<string>();
        var current = transform;
        while (current != null)
        {
            names.Push(current.name);
            if (current == root)
            {
                break;
            }

            current = current.parent;
        }

        return string.Join("/", names.ToArray());
    }

    private static string GetAssetKey(UnityEngine.Object source)
    {
        if (source == null)
        {
            return "<missing>";
        }

        string guid;
        long localId;
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out guid, out localId))
        {
            return guid + ":" + localId;
        }

        try
        {
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(source);
            if (globalId.identifierType != 0)
            {
                return "global:" + globalId;
            }
        }
        catch (Exception)
        {
            // Some built-in objects do not expose a GlobalObjectId.
        }

        var path = AssetDatabase.GetAssetPath(source);
        return path + "#" + source.GetInstanceID();
    }

    private static string GetNextOutputPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = "Assets";
        }

        directory = directory.Replace('\\', '/');
        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = string.Format("{0}/{1}{2}.prefab", directory, sourceName, OutputSuffix);
        var index = 1;
        while (File.Exists(candidate))
        {
            candidate = string.Format("{0}/{1}{2}_{3:00}.prefab", directory, sourceName, OutputSuffix, index++);
        }

        return candidate;
    }

    private sealed class PrefabOperationContext : IDisposable
    {
        public readonly GameObject Root;
        private readonly Scene scene;
        private readonly Scene previousActiveScene;
        private readonly bool isIsolatedContents;

        private PrefabOperationContext(
            GameObject root,
            Scene scene,
            Scene previousActiveScene,
            bool isIsolatedContents)
        {
            Root = root;
            this.scene = scene;
            this.previousActiveScene = previousActiveScene;
            this.isIsolatedContents = isIsolatedContents;
        }

        public static PrefabOperationContext Open(GameObject prefabAsset, string assetPath)
        {
            if (PrefabUtility.GetPrefabAssetType(prefabAsset) == PrefabAssetType.Variant)
            {
                var previousActiveScene = SceneManager.GetActiveScene();
                var tempScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                var instance = PrefabUtility.InstantiatePrefab(prefabAsset, tempScene) as GameObject;
                if (instance == null)
                {
                    EditorSceneManager.CloseScene(tempScene, true);
                    throw new InvalidOperationException("无法实例化 Prefab Variant。");
                }

                return new PrefabOperationContext(instance, tempScene, previousActiveScene, false);
            }

            return new PrefabOperationContext(
                PrefabUtility.LoadPrefabContents(assetPath),
                default(Scene),
                default(Scene),
                true);
        }

        public void Dispose()
        {
            if (isIsolatedContents)
            {
                if (Root != null)
                {
                    PrefabUtility.UnloadPrefabContents(Root);
                }

                return;
            }

            if (Root != null)
            {
                UnityEngine.Object.DestroyImmediate(Root);
            }

            if (scene.IsValid())
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            if (previousActiveScene.IsValid())
            {
                SceneManager.SetActiveScene(previousActiveScene);
            }
        }
    }

    private enum SourceKind
    {
        Sprite,
        Texture
    }

    private sealed class ImageSlot
    {
        public readonly string Key;
        public readonly UnityEngine.Object Source;
        public readonly SourceKind SourceKind;
        public readonly List<ImageUsage> Usages = new List<ImageUsage>();
        public Sprite Replacement;
        public bool Expanded = true;

        public ImageSlot(string key, UnityEngine.Object source, SourceKind sourceKind)
        {
            Key = key;
            Source = source;
            SourceKind = sourceKind;
        }

        public string DisplayName { get { return Source == null ? "<missing>" : Source.name; } }

        public string SourcePath
        {
            get
            {
                var path = Source == null ? string.Empty : AssetDatabase.GetAssetPath(Source);
                return string.IsNullOrEmpty(path) ? "（非项目资源）" : path;
            }
        }
    }

    private sealed class ImageUsage
    {
        public readonly string Path;
        public readonly string ComponentProperty;
        public readonly bool Writable;

        public ImageUsage(string path, string componentProperty, bool writable)
        {
            Path = path;
            ComponentProperty = componentProperty;
            Writable = writable;
        }
    }

    private sealed class ScanProblem
    {
        public readonly string Path;
        public readonly string Message;

        public ScanProblem(string path, string message)
        {
            Path = path;
            Message = message;
        }
    }

    private sealed class ReviewIssue
    {
        public readonly string Path;
        public readonly string Message;
        public bool Acknowledged;

        public ReviewIssue(string path, string message)
        {
            Path = path;
            Message = message;
        }
    }

    private sealed class IssueReviewWindow : EditorWindow
    {
        private static Action<bool> completion;
        private List<ReviewIssue> issues;
        private Vector2 scroll;

        public static void Show(List<ReviewIssue> reviewIssues, Action<bool> onComplete)
        {
            var window = CreateInstance<IssueReviewWindow>();
            window.issues = reviewIssues;
            completion = onComplete;
            window.titleContent = new GUIContent("确认问题项");
            window.minSize = new Vector2(520f, 320f);
            window.ShowModalUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("执行前确认问题项", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("勾选每一项表示已知悉并跳过该问题引用；可写入的有效槽位仍会继续替换。", MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var issue in issues)
            {
                issue.Acknowledged = EditorGUILayout.ToggleLeft(
                    issue.Path + "：" + issue.Message,
                    issue.Acknowledged,
                    GUILayout.ExpandWidth(true));
            }

            EditorGUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("取消", GUILayout.Width(90f)))
                {
                    Finish(false);
                }

                using (new EditorGUI.DisabledScope(issues.Any(i => !i.Acknowledged)))
                {
                    if (GUILayout.Button("确认并生成", GUILayout.Width(110f)))
                    {
                        Finish(true);
                    }
                }
            }
        }

        private void Finish(bool accepted)
        {
            var callback = completion;
            completion = null;
            Close();
            if (callback != null)
            {
                callback(accepted);
            }
        }
    }
}
