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
    private Vector2 problemScrollPosition;
    private List<ImageSlot> slots = new List<ImageSlot>();
    private List<ScanProblem> scanProblems = new List<ScanProblem>();
    private Hash128 scannedDependencyHash;
    private int lastReviewIssueCount;
    private bool problemDetailsExpanded;
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

        var currentIssues = BuildReviewIssues();
        if (currentIssues.Count > 0)
        {
            EditorGUILayout.HelpBox(
                BuildIssueSummary(currentIssues) + "\n执行时需要逐项确认，问题引用会被跳过。",
                MessageType.Warning);
            DrawProblemDetails(currentIssues);
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
                    EditorGUILayout.LabelField("原图分辨率", slot.OriginalResolutionText, EditorStyles.miniLabel);
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

            if (slot.Replacement != null)
            {
                EditorGUILayout.LabelField("槽位图分辨率", slot.ReplacementResolutionText, EditorStyles.miniLabel);
                if (slot.HasResolutionMismatch)
                {
                    EditorGUILayout.HelpBox(
                        string.Format(
                            "分辨率不一致：原图 {0}，槽位图 {1}。请确认缩放、裁剪和显示效果符合预期。",
                            slot.OriginalResolutionText,
                            slot.ReplacementResolutionText),
                        MessageType.Warning);
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
                        IssueKind.ImageMissingReference,
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
                        IssueKind.RawImageMissingReference,
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
            scanProblems.Add(new ScanProblem(
                IssueKind.ScanFailure,
                path,
                "扫描失败：" + exception.Message));
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

        var issues = BuildReviewIssues();
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

    private List<ReviewIssue> BuildReviewIssues()
    {
        var issues = new List<ReviewIssue>();
        foreach (var problem in scanProblems)
        {
            issues.Add(new ReviewIssue(problem.Kind, problem.Path, problem.Message));
        }

        foreach (var slot in slots)
        {
            if (slot.Replacement == null)
            {
                issues.Add(new ReviewIssue(
                    IssueKind.MissingReplacement,
                    slot.DisplayName,
                    "未配置替换 Sprite（该槽位将跳过）"));
            }
            else if (slot.HasResolutionMismatch)
            {
                issues.Add(new ReviewIssue(
                    IssueKind.ResolutionMismatch,
                    slot.DisplayName,
                    string.Format(
                        "原图分辨率 {0}，槽位图分辨率 {1}（分辨率不一致，请确认后继续）",
                        slot.OriginalResolutionText,
                        slot.ReplacementResolutionText)));
            }

            foreach (var usage in slot.Usages.Where(u => !u.Writable))
            {
                issues.Add(new ReviewIssue(
                    IssueKind.NestedPrefabReadOnly,
                    usage.Path,
                    usage.ComponentProperty + " 位于嵌套 Prefab，无法直接写回（该引用将跳过）"));
            }
        }

        return issues;
    }

    private void DrawProblemDetails(List<ReviewIssue> issues)
    {
        problemDetailsExpanded = EditorGUILayout.Foldout(
            problemDetailsExpanded,
            string.Format("问题明细（{0}）", issues.Count),
            true);
        if (!problemDetailsExpanded)
        {
            return;
        }

        var height = Mathf.Min(220f, Mathf.Max(64f, issues.Count * 34f));
        problemScrollPosition = EditorGUILayout.BeginScrollView(
            problemScrollPosition,
            EditorStyles.helpBox,
            GUILayout.Height(height));
        foreach (var issue in issues)
        {
            EditorGUILayout.LabelField(
                string.Format("[{0}] {1}\n{2}", GetIssueKindLabel(issue.Kind), issue.Path, issue.Message),
                EditorStyles.wordWrappedMiniLabel);
            GUILayout.Space(4f);
        }

        EditorGUILayout.EndScrollView();
    }

    private static string BuildIssueSummary(List<ReviewIssue> issues)
    {
        return string.Format(
            "当前共 {0} 个问题项：Image 空/丢失 {1}，RawImage 空/丢失 {2}，未配置槽位 {3}，分辨率不一致 {4}，嵌套只读引用 {5}，扫描失败 {6}。",
            issues.Count,
            CountIssues(issues, IssueKind.ImageMissingReference),
            CountIssues(issues, IssueKind.RawImageMissingReference),
            CountIssues(issues, IssueKind.MissingReplacement),
            CountIssues(issues, IssueKind.ResolutionMismatch),
            CountIssues(issues, IssueKind.NestedPrefabReadOnly),
            CountIssues(issues, IssueKind.ScanFailure));
    }

    private static int CountIssues(List<ReviewIssue> issues, IssueKind kind)
    {
        return issues.Count(issue => issue.Kind == kind);
    }

    private static string GetIssueKindLabel(IssueKind kind)
    {
        switch (kind)
        {
            case IssueKind.ImageMissingReference:
                return "Image 空/丢失";
            case IssueKind.RawImageMissingReference:
                return "RawImage 空/丢失";
            case IssueKind.MissingReplacement:
                return "未配置槽位";
            case IssueKind.ResolutionMismatch:
                return "分辨率不一致";
            case IssueKind.NestedPrefabReadOnly:
                return "嵌套只读";
            case IssueKind.ScanFailure:
                return "扫描失败";
            default:
                return "其它";
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

    private enum IssueKind
    {
        ImageMissingReference,
        RawImageMissingReference,
        MissingReplacement,
        ResolutionMismatch,
        NestedPrefabReadOnly,
        ScanFailure
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

        public string OriginalResolutionText
        {
            get { return FormatResolution(GetResolution(Source, SourceKind == SourceKind.Sprite)); }
        }

        public string ReplacementResolutionText
        {
            get
            {
                if (Replacement == null)
                {
                    return "未配置";
                }

                return FormatResolution(GetResolution(Replacement, SourceKind == SourceKind.Sprite));
            }
        }

        public bool HasResolutionMismatch
        {
            get
            {
                if (Replacement == null)
                {
                    return false;
                }

                return GetResolution(Source, SourceKind == SourceKind.Sprite) != GetResolution(
                    Replacement,
                    SourceKind == SourceKind.Sprite);
            }
        }

        private static Vector2Int GetResolution(UnityEngine.Object source, bool useSpriteRegion)
        {
            if (source == null)
            {
                return Vector2Int.zero;
            }

            if (useSpriteRegion)
            {
                var sprite = source as Sprite;
                if (sprite != null)
                {
                    return new Vector2Int(
                        Mathf.RoundToInt(sprite.rect.width),
                        Mathf.RoundToInt(sprite.rect.height));
                }
            }

            var sourceSprite = source as Sprite;
            if (sourceSprite != null)
            {
                var spriteTexture = sourceSprite.texture;
                return spriteTexture == null
                    ? Vector2Int.zero
                    : new Vector2Int(spriteTexture.width, spriteTexture.height);
            }

            var texture = source as Texture;
            return texture == null ? Vector2Int.zero : new Vector2Int(texture.width, texture.height);
        }

        private static string FormatResolution(Vector2Int resolution)
        {
            return resolution == Vector2Int.zero
                ? "未知"
                : string.Format("{0} x {1}", resolution.x, resolution.y);
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
        public readonly IssueKind Kind;
        public readonly string Path;
        public readonly string Message;

        public ScanProblem(IssueKind kind, string path, string message)
        {
            Kind = kind;
            Path = path;
            Message = message;
        }
    }

    private sealed class ReviewIssue
    {
        public readonly IssueKind Kind;
        public readonly string Path;
        public readonly string Message;
        public bool Acknowledged;

        public ReviewIssue(IssueKind kind, string path, string message)
        {
            Kind = kind;
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
            EditorGUILayout.HelpBox(
                BuildIssueSummary(issues)
                + "\n勾选每一项表示已知悉并跳过该问题引用；可写入的有效槽位仍会继续替换。",
                MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var issue in issues)
            {
                issue.Acknowledged = EditorGUILayout.ToggleLeft(
                    "[" + GetIssueKindLabel(issue.Kind) + "] " + issue.Path + "：" + issue.Message,
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
