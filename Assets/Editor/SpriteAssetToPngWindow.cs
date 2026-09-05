using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

/// <summary>
/// Converts standalone Sprite .asset files into cropped PNG Sprite assets while retaining their GUID.
/// </summary>
public sealed class SpriteAssetToPngWindow : EditorWindow
{
    private const string DefaultFolder = "Assets";
    // Keep temporary imports in a short, stable path.  Some source folders are
    // already close to Windows' MAX_PATH limit; putting the temporary filename
    // next to the source would make Unity fail to create its .meta file.
    private const string TemporaryImportFolder = "Assets/Editor/SpriteAssetToPngTemp";
    private const long SpriteLocalId = 21300000L;
    private static readonly Regex GuidLineRegex = new Regex(
        "^guid:\\s*[0-9a-fA-F]{32}\\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex SerializedGuidRegex = new Regex(
        "guid:\\s*([0-9a-fA-F]{32})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> TextExtensions = new HashSet<string>(
        new[] { ".asset", ".prefab", ".unity", ".mat", ".anim", ".controller", ".overridecontroller", ".playable",
            ".cs", ".json", ".xml", ".txt", ".shader", ".compute", ".uss", ".uxml", ".bytes" },
        StringComparer.OrdinalIgnoreCase);

    private string folderPath = DefaultFolder;
    private DefaultAsset folderObject;
    private Vector2 scrollPosition;
    private readonly Dictionary<string, Texture2D> previewCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ConversionCandidate> previewQueue = new Queue<ConversionCandidate>();
    private readonly HashSet<string> previewQueued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConversionCandidate> candidates = new List<ConversionCandidate>();
    private readonly List<ScanProblemItem> problemItems = new List<ScanProblemItem>();
    private readonly List<AtlasInfo> atlases = new List<AtlasInfo>();
    private readonly List<string> scanProblems = new List<string>();
    private readonly List<string> specialAssetReports = new List<string>();
    private int scannedAssetCount;
    private int pivotNormalizationCount;
    private int layoutApproximationCount;
    private int existingReferenceRepairCount;
    private bool hasScanned;
    private bool isBusy;
    private string[] scanPaths = new string[0];
    private int scanIndex;
    private string scanFolderLabel = string.Empty;
    private ConversionRun conversionRun;
    private string progressMessage = string.Empty;
    private float progressValue;

    [MenuItem("Tools/Sprite Asset To PNG")]
    public static void Open()
    {
        GetWindow<SpriteAssetToPngWindow>("Sprite Asset To PNG");
    }

    private void OnEnable()
    {
        SetFolderPath(folderPath);
    }

    private void OnDisable()
    {
        EditorApplication.update -= ScanStep;
        EditorApplication.update -= ConversionStep;
        EditorApplication.update -= PreviewStep;
        ClearPreviewCache();
        if (isBusy)
        {
            isBusy = false;
            EditorUtility.ClearProgressBar();
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Sprite .asset 批量转 PNG", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "按图集区域裁切独立 PNG，保留原 GUID/fileID、Border、PPU，并将 Pivot 归一化到 0-1。序列化引用会从 type:2 迁移为 type:3；转换前自动备份。旋转项和 TMP SpriteAsset 仅报告，图集删除必须通过二次确认和引用检查。",
            MessageType.Info);
        EditorGUI.BeginDisabledGroup(isBusy);
        DrawFolderSelection();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("预检", GUILayout.Height(28f))) Scan();
            EditorGUI.BeginDisabledGroup(!hasScanned || candidates.All(candidate => candidate.Blocked));
            if (GUILayout.Button("转换全部可转换项", GUILayout.Height(28f))) ConvertAll();
            EditorGUI.EndDisabledGroup();
        }
        EditorGUI.EndDisabledGroup();
        if (isBusy)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(progressMessage, EditorStyles.miniLabel);
            var progressRect = GUILayoutUtility.GetRect(1f, 18f);
            EditorGUI.ProgressBar(progressRect, progressValue, Mathf.RoundToInt(progressValue * 100f) + "%");
        }
        DrawScanResultReadable();
    }

    private void OnGUIOld()
    {
        EditorGUILayout.LabelField("Sprite .asset 批量转 PNG", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "只处理指定目录中主对象为 Sprite、且源纹理为 PNG 的 .asset。工具会裁切独立 PNG，保留 Unity 序列化引用使用的 GUID/fileID、Border 和 PPU；有效 Pivot 保持不变，越界 Pivot 会归一化到 0～1。原文件备份在 Library 中，字符串形式的路径引用无法自动迁移。",
            MessageType.Info);

        EditorGUI.BeginDisabledGroup(isBusy);
        DrawFolderSelection();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("预检", GUILayout.Height(28f)))
            {
                Scan();
            }

            EditorGUI.BeginDisabledGroup(!hasScanned || candidates.Count == 0);
            if (GUILayout.Button("转换全部可转换项", GUILayout.Height(28f)))
            {
                ConvertAll();
            }
            EditorGUI.EndDisabledGroup();
        }
        EditorGUI.EndDisabledGroup();

        DrawScanResultReadable();
    }

    private void DrawFolderSelection()
    {
        EditorGUI.BeginChangeCheck();
        var selectedFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("目录", "拖入 Assets 下的目录"),
            folderObject,
            typeof(DefaultAsset),
            false);
        if (EditorGUI.EndChangeCheck())
        {
            var selectedPath = selectedFolder == null ? string.Empty : AssetDatabase.GetAssetPath(selectedFolder);
            if (AssetDatabase.IsValidFolder(selectedPath))
            {
                folderObject = selectedFolder;
                folderPath = selectedPath;
                ClearScan();
            }
        }

        EditorGUI.BeginChangeCheck();
        var enteredPath = EditorGUILayout.TextField(
            new GUIContent("路径", "支持 Assets/... 或项目 Assets 目录内的绝对路径"),
            folderPath);
        if (EditorGUI.EndChangeCheck())
        {
            folderPath = enteredPath.Trim();
            folderObject = null;
            ClearScan();
        }
    }

    private void DrawScanResultReadable()
    {
        if (!hasScanned) return;
        EditorGUILayout.Space();
        var itemWarningCount = problemItems.Count + candidates.Sum(candidate => candidate.Warnings.Count);
        var itemNoteCount = candidates.Sum(candidate => candidate.Notes.Count);
        EditorGUILayout.LabelField(
            string.Format("扫描 .asset：{0}    可转换 Sprite：{1}    已存在可续跑：{2}    复用 PNG：{3}    Pivot 归一化：{4}    图集组：{5}    问题：{6}    条目警告：{7}    条目说明：{8}",
                scannedAssetCount, candidates.Count(c => !c.Blocked), candidates.Count(c => c.AlreadyConverted),
                candidates.Count(c => c.ReuseExistingPng && !c.Blocked), pivotNormalizationCount,
                atlases.Count, scanProblems.Count + specialAssetReports.Count, itemWarningCount, itemNoteCount),
            EditorStyles.miniBoldLabel);
        if (existingReferenceRepairCount > 0)
        {
            EditorGUILayout.HelpBox("发现 " + existingReferenceRepairCount + " 个已生成 PNG 仍有 type:2 引用，转换时会自动修复。", MessageType.Info);
        }
        if (scanProblems.Count > 0)
        {
            EditorGUILayout.HelpBox(string.Join("\n", scanProblems.Take(10).ToArray()), MessageType.Warning);
        }
        if (layoutApproximationCount > 0)
        {
            EditorGUILayout.HelpBox("有 " + layoutApproximationCount + " 个 Sprite 带 m_Offset；输出保留裁切图和 Pivot，但布局只做近似，建议转换后抽查 UI。", MessageType.Warning);
        }
        if (specialAssetReports.Count > 0)
        {
            EditorGUILayout.HelpBox("特殊资源（仅报告并保留）：\n" + string.Join("\n", specialAssetReports.Take(10).ToArray()), MessageType.Info);
        }
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        foreach (var problem in problemItems)
        {
            DrawProblemItem(problem);
        }
        foreach (var candidate in candidates)
        {
            DrawCandidatePreview(candidate);
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(candidate.AssetPath);
            var size = sprite == null ? "无法读取" : string.Format("{0} x {1}{2}",
                Mathf.RoundToInt(sprite.rect.width), Mathf.RoundToInt(sprite.rect.height),
                candidate.ApproximatePivot ? "，Pivot 将归一化" : string.Empty);
            EditorGUILayout.LabelField(Path.GetFileName(candidate.AssetPath), size, EditorStyles.miniLabel);
            var mode = candidate.ReuseExistingPng ? "（复用现有 PNG）" : string.Empty;
            EditorGUILayout.LabelField(candidate.AssetPath + " -> " + candidate.OutputPath + mode, EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawCandidatePreview(ConversionCandidate candidate)
    {
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(candidate.AssetPath);
        var preview = GetPreview(candidate, sprite);
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(78f)))
        {
            if (GUILayout.Button(new GUIContent(preview, preview == null ? "预览加载中" : string.Empty),
                GUILayout.Width(72f), GUILayout.Height(72f)))
            {
                LocateCandidate(candidate);
            }
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.LabelField("点击缩略图定位图片", EditorStyles.miniLabel);
            }
        }
        if (candidate.Notes.Count > 0)
        {
            EditorGUILayout.HelpBox(string.Join("\n", candidate.Notes.ToArray()), MessageType.Info);
        }
        if (candidate.Warnings.Count > 0)
        {
            EditorGUILayout.HelpBox(string.Join("\n", candidate.Warnings.ToArray()), MessageType.Warning);
        }
    }

    private static void DrawProblemItem(ScanProblemItem problem)
    {
        using (new EditorGUILayout.HorizontalScope(GUILayout.MinHeight(34f)))
        {
            if (GUILayout.Button("⚠", GUILayout.Width(28f), GUILayout.Height(24f)))
            {
                LocateAssetPath(problem.AssetPath);
            }
            using (new EditorGUILayout.VerticalScope())
            {
                if (GUILayout.Button(Path.GetFileName(problem.AssetPath), EditorStyles.linkLabel))
                {
                    LocateAssetPath(problem.AssetPath);
                }
                EditorGUILayout.LabelField(problem.AssetPath, EditorStyles.miniLabel);
            }
        }
        EditorGUILayout.HelpBox(problem.Message, MessageType.Warning);
    }

    private Texture2D GetPreview(ConversionCandidate candidate, Sprite sprite)
    {
        if (sprite == null) return null;
        Texture2D preview;
        if (previewCache.TryGetValue(candidate.AssetPath, out preview) && preview != null) return preview;
        if (previewQueued.Add(candidate.AssetPath))
        {
            previewQueue.Enqueue(candidate);
            EditorApplication.update -= PreviewStep;
            EditorApplication.update += PreviewStep;
        }
        return preview;
    }

    private void PreviewStep()
    {
        if (previewQueue.Count == 0)
        {
            EditorApplication.update -= PreviewStep;
            return;
        }

        var candidate = previewQueue.Dequeue();
        previewQueued.Remove(candidate.AssetPath);
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(candidate.AssetPath);
        if (sprite != null)
        {
            try
            {
                var preview = CreatePreviewTexture(sprite);
                if (preview != null)
                {
                    previewCache[candidate.AssetPath] = preview;
                }
                else
                {
                    AddCandidateWarning(candidate, "预览生成失败：无法解码裁切后的像素。");
                }
            }
            catch (Exception exception)
            {
                AddCandidateWarning(candidate, "预览生成失败：" + exception.Message);
                Debug.LogWarning("[Sprite Asset To PNG] 预览生成失败：" + candidate.AssetPath + "，" + exception.Message);
            }
        }
        Repaint();
    }

    private static void AddCandidateWarning(ConversionCandidate candidate, string warning)
    {
        if (candidate == null || string.IsNullOrEmpty(warning) || candidate.Warnings.Contains(warning)) return;
        candidate.Warnings.Add(warning);
    }

    private static void AddCandidateNote(ConversionCandidate candidate, string note)
    {
        if (candidate == null || string.IsNullOrEmpty(note) || candidate.Notes.Contains(note)) return;
        candidate.Notes.Add(note);
    }

    private static void AddConversionWarnings(ConversionCandidate candidate)
    {
        if (candidate.ReuseExistingPng)
        {
            AddCandidateNote(candidate, "检测到同名 PNG 是源 Sprite 的 backing texture，将直接复用，不生成副本。转换时会迁移引用和 Sprite 几何数据。");
        }
        if (candidate.ApproximatePivot)
        {
            AddCandidateWarning(candidate, "Pivot 超出 0~1，转换时会归一化到 0~1。");
        }
        if (candidate.HasOffset)
        {
            AddCandidateWarning(candidate, "检测到 m_Offset，裁切后的布局只做近似，请转换后抽查 UI。");
        }
        if (candidate.Blocked)
        {
            AddCandidateWarning(candidate, "该条目已被预检阻断，不会自动合并或删除原 .asset。");
        }
    }

    private static void ConfigureExistingPngCandidate(ConversionCandidate candidate)
    {
        if (candidate == null) return;

        var desired = Path.ChangeExtension(candidate.AssetPath, ".png").Replace('\\', '/');
        var desiredAbsolute = AssetPathToAbsolute(desired);
        if (!File.Exists(desiredAbsolute)) return;

        // A same-name PNG is safe to reuse only when it is the source texture of
        // the Sprite asset.  A coincidental pixel duplicate from another atlas
        // must not be silently merged.
        if (!string.Equals(candidate.SourceTexturePath, desired, StringComparison.OrdinalIgnoreCase))
        {
            AddCandidateWarning(candidate, "发现同名 PNG，但它不是该 Sprite 的 backing texture；按规则不自动合并，将按普通转换处理。");
            return;
        }

        string existingGuid = string.Empty;
        var desiredMetaPath = desiredAbsolute + ".meta";
        if (File.Exists(desiredMetaPath))
        {
            try { existingGuid = ReadMetaGuid(desiredMetaPath); } catch (Exception) { }
        }
        if (string.IsNullOrEmpty(existingGuid))
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, "同名 backing PNG 缺少有效 meta/GUID，无法安全迁移引用。");
            return;
        }

        var sourceSprite = AssetDatabase.LoadAssetAtPath<Sprite>(candidate.AssetPath);
        var pngSprite = AssetDatabase.LoadAssetAtPath<Sprite>(desired);
        if (sourceSprite == null || pngSprite == null)
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, "同名 backing PNG 无法作为 Sprite 导入，已阻断自动合并。");
            return;
        }

        string pixelDifference;
        if (!TryCompareSpritePixels(sourceSprite, desired, out pixelDifference))
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, "同名 backing PNG 像素或尺寸不一致：" + pixelDifference);
            return;
        }

        candidate.OutputPath = desired;
        candidate.ReuseExistingPng = true;
        candidate.ExistingPngGuid = existingGuid;
        var stringReferencePaths = FindRuntimeStringReferencePaths(candidate.Guid, candidate.AssetPath).Take(4).ToArray();
        if (stringReferencePaths.Length > 0)
        {
            AddCandidateWarning(candidate,
                "发现疑似运行时字符串引用（只报告、不自动修改）：" + string.Join("、", stringReferencePaths));
        }

        var settingDifferences = CompareSpriteImportSettings(sourceSprite, desired);
        if (settingDifferences.Count > 0)
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, "同名 backing PNG 导入参数不一致：" + string.Join("、", settingDifferences.ToArray()));
            return;
        }

        string[] fr2Referencers;
        string fr2Error;
        if (!TryGetFindReference2DirectReferencers(candidate.Guid, out fr2Referencers, out fr2Error))
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, "依赖 FindReference2 缓存：" + fr2Error);
            return;
        }
        var nonTextReferencers = fr2Referencers
            .Where(path => !IsUnitySerializedTextAsset(path))
            .Take(4)
            .ToArray();
        if (nonTextReferencers.Length > 0)
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate,
                "FindReference2 检出无法安全文本迁移的直接引用：" + string.Join("、", nonTextReferencers));
            return;
        }
        AddCandidateNote(candidate, "FindReference2 检出 " + fr2Referencers.Length + " 个直接引用；转换时会逐项迁移并再次扫描旧 GUID。");
    }

    private static bool TryCompareSpritePixels(Sprite sprite, string pngPath, out string difference)
    {
        difference = string.Empty;
        var absolutePngPath = AssetPathToAbsolute(pngPath);
        Texture2D actual = null;
        Texture2D expected = null;
        try
        {
            var pngBytes = File.ReadAllBytes(absolutePngPath);
            actual = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!actual.LoadImage(pngBytes, false))
            {
                difference = "PNG 无法解码";
                return false;
            }

            var rect = sprite.packed ? sprite.textureRect : sprite.rect;
            var width = Mathf.RoundToInt(rect.width);
            var height = Mathf.RoundToInt(rect.height);
            if (actual.width != width || actual.height != height)
            {
                difference = string.Format("尺寸为 {0}x{1}，期望 {2}x{3}", actual.width, actual.height, width, height);
                return false;
            }

            // When the PNG is the exact backing texture and the Sprite covers
            // the full image, the source bytes are the pixels being compared.
            if (string.Equals(sprite.texture == null ? string.Empty : AssetDatabase.GetAssetPath(sprite.texture), pngPath,
                    StringComparison.OrdinalIgnoreCase) &&
                Mathf.RoundToInt(rect.x) == 0 && Mathf.RoundToInt(rect.y) == 0 &&
                actual.width == sprite.texture.width && actual.height == sprite.texture.height)
            {
                return true;
            }

            var expectedBytes = ExtractPng(sprite);
            expected = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!expected.LoadImage(expectedBytes, false) || expected.width != actual.width || expected.height != actual.height)
            {
                difference = "无法解码 Sprite 裁切结果";
                return false;
            }

            var actualPixels = actual.GetPixels32();
            var expectedPixels = expected.GetPixels32();
            if (actualPixels.Length != expectedPixels.Length)
            {
                difference = "像素数量不一致";
                return false;
            }
            for (var i = 0; i < actualPixels.Length; i++)
            {
                if (!actualPixels[i].Equals(expectedPixels[i]))
                {
                    difference = "存在像素差异";
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception)
        {
            difference = exception.Message;
            return false;
        }
        finally
        {
            if (actual != null) DestroyImmediate(actual);
            if (expected != null) DestroyImmediate(expected);
        }
    }

    private static List<string> CompareSpriteImportSettings(Sprite sourceSprite, string pngPath)
    {
        var differences = new List<string>();
        var importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
        if (importer == null)
        {
            differences.Add("TextureImporter 不存在");
            return differences;
        }
        if (importer.textureType != TextureImporterType.Sprite) differences.Add("TextureType");
        if (importer.spriteImportMode != SpriteImportMode.Single) differences.Add("SpriteImportMode");
        if (!Mathf.Approximately(importer.spritePixelsPerUnit, sourceSprite.pixelsPerUnit)) differences.Add("PPU");
        if (!Approximately(importer.spriteBorder, sourceSprite.border)) differences.Add("Border");
        if (sourceSprite.texture != null)
        {
            if (importer.filterMode != sourceSprite.texture.filterMode) differences.Add("FilterMode");
            if (importer.wrapMode != sourceSprite.texture.wrapMode) differences.Add("WrapMode");
            if (importer.anisoLevel != sourceSprite.texture.anisoLevel) differences.Add("AnisoLevel");
        }
        var sourceImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sourceSprite));
        if (sourceImporter != null)
        {
            if (!string.Equals(importer.assetBundleName, sourceImporter.assetBundleName, StringComparison.Ordinal) ||
                !string.Equals(importer.assetBundleVariant, sourceImporter.assetBundleVariant, StringComparison.Ordinal))
            {
                differences.Add("AssetBundle");
            }
        }

        var textureSettings = new TextureImporterSettings();
        importer.ReadTextureSettings(textureSettings);
        var sourcePivot = GetNormalizedPivot(sourceSprite);
        if (textureSettings.spriteAlignment != (int)SpriteAlignment.Custom ||
            !Approximately(textureSettings.spritePivot, sourcePivot))
        {
            differences.Add("Pivot/Alignment");
        }
        return differences;
    }

    private static bool Approximately(Vector2 a, Vector2 b)
    {
        return Mathf.Abs(a.x - b.x) <= 0.0001f && Mathf.Abs(a.y - b.y) <= 0.0001f;
    }

    private static bool Approximately(Vector4 a, Vector4 b)
    {
        return Mathf.Abs(a.x - b.x) <= 0.0001f && Mathf.Abs(a.y - b.y) <= 0.0001f &&
               Mathf.Abs(a.z - b.z) <= 0.0001f && Mathf.Abs(a.w - b.w) <= 0.0001f;
    }

    private static IEnumerable<string> FindRuntimeStringReferencePaths(string guid, string assetPath)
    {
        foreach (var absolutePath in EnumerateTextFiles())
        {
            if (IsUnitySerializedTextFile(absolutePath)) continue;
            var path = AbsoluteToAssetPath(absolutePath);
            if (string.Equals(path, assetPath, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase)) continue;
            string content;
            try { content = File.ReadAllText(absolutePath); } catch (Exception) { continue; }
            if ((!string.IsNullOrEmpty(guid) && content.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0) ||
                content.IndexOf(assetPath, StringComparison.OrdinalIgnoreCase) >= 0 ||
                content.IndexOf(assetPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                yield return path;
            }
        }
    }

    private static Texture2D CreatePreviewTexture(Sprite sprite)
    {
        var pngBytes = ExtractPng(sprite);
        var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        RenderTexture renderTexture = null;
        Texture2D preview = null;
        var previous = RenderTexture.active;
        try
        {
            if (!decoded.LoadImage(pngBytes, false)) return null;
            const int maxPreviewSize = 128;
            var scale = Mathf.Min(1f, Mathf.Min((float)maxPreviewSize / decoded.width, (float)maxPreviewSize / decoded.height));
            var width = Math.Max(1, Mathf.RoundToInt(decoded.width * scale));
            var height = Math.Max(1, Mathf.RoundToInt(decoded.height * scale));
            renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(decoded, renderTexture);
            RenderTexture.active = renderTexture;
            preview = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            preview.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            preview.Apply(false, true);
            preview.hideFlags = HideFlags.HideAndDontSave;
            return preview;
        }
        finally
        {
            RenderTexture.active = previous;
            if (renderTexture != null) RenderTexture.ReleaseTemporary(renderTexture);
            DestroyImmediate(decoded);
        }
    }

    private void ClearPreviewCache()
    {
        foreach (var preview in previewCache.Values)
        {
            if (preview != null) DestroyImmediate(preview);
        }
        previewCache.Clear();
        previewQueue.Clear();
        previewQueued.Clear();
    }

    private static void LocateCandidate(ConversionCandidate candidate)
    {
        var target = AssetDatabase.LoadMainAssetAtPath(candidate.OutputPath);
        if (target == null) target = AssetDatabase.LoadMainAssetAtPath(candidate.AssetPath);
        if (target == null) return;
        Selection.activeObject = target;
        EditorGUIUtility.PingObject(target);
    }

    private static void LocateAssetPath(string assetPath)
    {
        var target = AssetDatabase.LoadMainAssetAtPath(assetPath);
        if (target == null) return;
        Selection.activeObject = target;
        EditorGUIUtility.PingObject(target);
    }

    private void DrawScanResult()
    {
        if (!hasScanned)
        {
            return;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(
            string.Format("扫描 .asset：{0}    可转换 Sprite：{1}    Pivot 将归一化：{2}    问题：{3}",
                scannedAssetCount, candidates.Count, candidates.Count(c => c.AlreadyConverted), pivotNormalizationCount, atlases.Count, scanProblems.Count + specialAssetReports.Count),
            EditorStyles.miniBoldLabel);

        if (existingReferenceRepairCount > 0)
        {
            EditorGUILayout.HelpBox("发现 " + existingReferenceRepairCount + " 个已生成 PNG 仍有 type:2 引用，转换时会自动修复。", MessageType.Info);
        }

        if (scanProblems.Count > 0)
        {
            EditorGUILayout.HelpBox(string.Join("\n", scanProblems.Take(8).ToArray()), MessageType.Warning);
        }

        if (specialAssetReports.Count > 0)
        {
            EditorGUILayout.HelpBox("特殊资源（仅报告并保留）：\n" + string.Join("\n", specialAssetReports.Take(10).ToArray()), MessageType.Info);
        }

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        foreach (var candidate in candidates)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(candidate.AssetPath);
            var size = sprite == null
                ? "无法读取"
                : string.Format("{0} x {1}", Mathf.RoundToInt(sprite.rect.width), Mathf.RoundToInt(sprite.rect.height));
            EditorGUILayout.LabelField(Path.GetFileName(candidate.AssetPath), size, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(candidate.AssetPath + " -> " + candidate.OutputPath, EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    private void Scan()
    {
        if (isBusy) return;
        ClearScan();
        string normalizedFolder;
        string error;
        if (!TryNormalizeFolder(folderPath, out normalizedFolder, out error))
        {
            scanProblems.Add(error);
            hasScanned = true;
            return;
        }

        SetFolderPath(normalizedFolder);
        scanFolderLabel = normalizedFolder;
        string[] paths;
        try
        {
            paths = Directory.GetFiles(AssetPathToAbsolute(normalizedFolder), "*.asset", SearchOption.AllDirectories)
                .Select(AbsoluteToAssetPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            scanProblems.Add("扫描目录失败：" + exception.Message);
            hasScanned = true;
            return;
        }

        scanPaths = paths;
        scanIndex = 0;
        scannedAssetCount = paths.Length;
        existingReferenceRepairCount = 0;
        hasScanned = false;
        isBusy = true;
        EditorApplication.update -= ScanStep;
        EditorApplication.update += ScanStep;
        progressMessage = "准备扫描 " + normalizedFolder;
        progressValue = 0f;
        EditorUtility.DisplayProgressBar("Sprite 预检", "准备扫描 " + normalizedFolder, 0f);
        Repaint();
    }

    private void ScanStep()
    {
        if (!isBusy || scanPaths == null)
        {
            EditorApplication.update -= ScanStep;
            return;
        }

        try
        {
            if (scanIndex < scanPaths.Length)
            {
                var path = scanPaths[scanIndex++];
                ProcessScanPath(path);
                var progress = scanPaths.Length == 0 ? 1f : (float)scanIndex / scanPaths.Length;
                progressMessage = "预检：正在检查 " + path;
                progressValue = progress;
                if (EditorUtility.DisplayCancelableProgressBar("Sprite 预检", "正在检查 " + path, progress))
                {
                    scanProblems.Add("用户取消，剩余预检项目未处理。");
                    scanIndex = scanPaths.Length;
                }
                Repaint();
                return;
            }

            BuildAtlasGroups();
            progressMessage = "预检完成";
            progressValue = 1f;
            hasScanned = true;
            isBusy = false;
            EditorApplication.update -= ScanStep;
            EditorUtility.ClearProgressBar();
            Repaint();
        }
        catch (Exception exception)
        {
            scanProblems.Add("预检失败：" + exception.Message);
            hasScanned = true;
            isBusy = false;
            EditorApplication.update -= ScanStep;
            EditorUtility.ClearProgressBar();
            Repaint();
        }
    }

    private void ProcessScanPath(string path)
    {
        var sprite = AssetDatabase.LoadMainAssetAtPath(path) as Sprite;
        if (sprite == null)
        {
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            if (mainAsset != null && string.Equals(mainAsset.GetType().Name, "TMP_SpriteAsset", StringComparison.Ordinal))
            {
                specialAssetReports.Add(path + "（TMP SpriteAsset，图集与字符映射不自动拆分）");
            }
            return;
        }
        if (sprite.texture == null)
        {
            AddScanProblemItem(path, "Sprite 没有源纹理，已跳过。");
            return;
        }
        var sourceTexturePath = AssetDatabase.GetAssetPath(sprite.texture);
        if (!sourceTexturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            AddScanProblemItem(path, "源纹理不是 PNG，已跳过。");
            return;
        }
        if (sprite.packed && sprite.packingRotation != SpritePackingRotation.None)
        {
            AddScanProblemItem(path, "Sprite 使用了旋转图集，已跳过以避免方向错误。");
            return;
        }
        string guid;
        long localId;
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out guid, out localId) || string.IsNullOrEmpty(guid))
        {
            AddScanProblemItem(path, "无法读取资源 GUID，已跳过。");
            return;
        }
        if (localId != SpriteLocalId)
        {
            AddScanProblemItem(path, "fileID 不是 21300000，无法保证引用不变，已跳过。");
            return;
        }
        var normalizedPivot = GetNormalizedPivot(sprite);
        if (RequiresPivotNormalization(normalizedPivot)) pivotNormalizationCount++;
        var hasOffset = HasSerializedOffset(path);
        if (hasOffset) layoutApproximationCount++;
        bool alreadyConverted;
        var outputPath = GetOutputPath(path, guid, out alreadyConverted);
        var candidate = new ConversionCandidate
        {
            AssetPath = path,
            Guid = guid,
            LocalId = localId,
            SourceTexturePath = sourceTexturePath,
            SourceTextureGuid = AssetDatabase.AssetPathToGUID(sourceTexturePath),
            OutputPath = outputPath,
            NormalizedPivot = normalizedPivot,
            ApproximatePivot = RequiresPivotNormalization(normalizedPivot),
            HasOffset = hasOffset,
            AlreadyConverted = alreadyConverted,
            Packed = sprite.packed
        };
        ConfigureExistingPngCandidate(candidate);
        AddConversionWarnings(candidate);
        candidates.Add(candidate);
    }

    private void ScanSynchronously()
    {
        ClearScan();

        string normalizedFolder;
        string error;
        if (!TryNormalizeFolder(folderPath, out normalizedFolder, out error))
        {
            scanProblems.Add(error);
            hasScanned = true;
            return;
        }

        SetFolderPath(normalizedFolder);
        var absoluteFolder = AssetPathToAbsolute(normalizedFolder);
        string[] paths;
        try
        {
            paths = Directory.GetFiles(absoluteFolder, "*.asset", SearchOption.AllDirectories)
                .Select(AbsoluteToAssetPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            scanProblems.Add("扫描目录失败：" + exception.Message);
            hasScanned = true;
            return;
        }
        scannedAssetCount = paths.Length;

        foreach (var path in paths)
        {
            var sprite = AssetDatabase.LoadMainAssetAtPath(path) as Sprite;
            if (sprite == null)
            {
                var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
                if (mainAsset != null && string.Equals(mainAsset.GetType().Name, "TMP_SpriteAsset", StringComparison.Ordinal))
                {
                    specialAssetReports.Add(path + "（TMP SpriteAsset，图集与字符映射不自动拆分）");
                }
                continue;
            }

            if (sprite.texture == null)
            {
                scanProblems.Add(path + "：Sprite 没有源纹理，已跳过。");
                continue;
            }

            var sourceTexturePath = AssetDatabase.GetAssetPath(sprite.texture);
            if (!sourceTexturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                scanProblems.Add(path + "：源纹理不是 PNG，已跳过。");
                continue;
            }

            if (sprite.packed && sprite.packingRotation != SpritePackingRotation.None)
            {
                scanProblems.Add(path + "：Sprite 使用了旋转图集打包，已跳过以避免导出方向错误。");
                continue;
            }

            string guid;
            long localId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out guid, out localId) ||
                string.IsNullOrEmpty(guid))
            {
                scanProblems.Add(path + "：无法读取资源 GUID，已跳过。");
                continue;
            }

            if (localId != 21300000)
            {
                scanProblems.Add(path + "：fileID 不是 21300000，无法保证引用不变，已跳过。");
                continue;
            }

            var normalizedPivot = GetNormalizedPivot(sprite);
            if (RequiresPivotNormalization(normalizedPivot))
            {
                pivotNormalizationCount++;
            }
            if (HasSerializedOffset(path))
            {
                layoutApproximationCount++;
            }

            bool alreadyConverted;
            var outputPath = GetOutputPath(path, guid, out alreadyConverted);
            var candidate = new ConversionCandidate
            {
                AssetPath = path,
                Guid = guid,
                LocalId = localId,
                SourceTexturePath = sourceTexturePath,
                SourceTextureGuid = AssetDatabase.AssetPathToGUID(sourceTexturePath),
                OutputPath = outputPath,
                NormalizedPivot = normalizedPivot,
                ApproximatePivot = RequiresPivotNormalization(normalizedPivot),
                HasOffset = HasSerializedOffset(path),
                AlreadyConverted = alreadyConverted,
                Packed = sprite.packed
            };
            ConfigureExistingPngCandidate(candidate);
            AddConversionWarnings(candidate);
            candidates.Add(candidate);
        }

        BuildAtlasGroups();
        existingReferenceRepairCount = CountExistingReferenceRepairs();
        hasScanned = true;
        Repaint();
    }

    private void ConvertAll()
    {
        if (isBusy || !hasScanned || candidates.Count == 0) return;
        var convertibleCount = candidates.Count(candidate => !candidate.Blocked);
        if (convertibleCount == 0) return;
        if (!EditorUtility.DisplayDialog(
                "确认批量转换",
                string.Format("将处理 {0} 个 Sprite .asset（另有 {1} 个条目因预检冲突而保留）。工具会逐帧执行，每项完成后更新进度；原文件备份到 Library/SpriteAssetToPngBackups。转换完成后，未被引用的图集会单独二次确认删除。是否继续？", convertibleCount, candidates.Count - convertibleCount),
                "转换", "取消"))
        {
            return;
        }

        var batchName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        conversionRun = new ConversionRun
        {
            BackupRoot = Path.Combine(ProjectRoot, "Library", "SpriteAssetToPngBackups", batchName),
            Pending = candidates.Where(candidate => !candidate.Blocked).ToArray(),
            Successes = new List<string>(),
            Failures = new List<string>(),
            DeletedAtlases = new List<string>(),
            KeptAtlases = new List<string>()
        };
        isBusy = true;
        EditorApplication.update -= ConversionStep;
        EditorApplication.update += ConversionStep;
        progressMessage = "准备转换 " + conversionRun.Pending.Length + " 个资源";
        progressValue = 0f;
        EditorUtility.DisplayProgressBar("Sprite 转 PNG", "准备转换 " + conversionRun.Pending.Length + " 个资源", 0f);
        Repaint();
    }

    private void ConversionStep()
    {
        var run = conversionRun;
        if (!isBusy || run == null)
        {
            EditorApplication.update -= ConversionStep;
            return;
        }

        try
        {
            if (run.Index < run.Pending.Length)
            {
                var candidate = run.Pending[run.Index];
                var progress = run.Pending.Length == 0 ? 1f : (float)run.Index / run.Pending.Length;
                progressMessage = "转换：正在处理 " + candidate.AssetPath;
                progressValue = progress;
                if (EditorUtility.DisplayCancelableProgressBar("Sprite 转 PNG",
                    "正在转换 " + candidate.AssetPath + "（" + (run.Index + 1) + "/" + run.Pending.Length + "）", progress))
                {
                    run.Failures.Add("用户取消，剩余项目未处理。");
                    run.Index = run.Pending.Length;
                }
                else
                {
                    string outputPath;
                    string error;
                    if (TryConvert(candidate, run.BackupRoot, out outputPath, out error))
                    {
                        run.Successes.Add(candidate.AssetPath + " -> " + outputPath);
                        if (candidate.ReuseExistingPng) run.ReusedPng++;
                    }
                    else
                    {
                        AddCandidateWarning(candidate, "转换失败：" + error);
                        run.Failures.Add(candidate.AssetPath + "：" + error);
                    }
                    run.Index++;
                }
                Repaint();
                return;
            }

            if (!run.Refreshed)
            {
                progressMessage = "转换：刷新 Unity 资源数据库";
                progressValue = 0.92f;
                EditorUtility.DisplayProgressBar("Sprite 转 PNG", "刷新 Unity 资源数据库", 0.92f);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                run.Refreshed = true;
                return;
            }
            if (!run.Repaired)
            {
                progressMessage = "转换：修复已生成 PNG 的旧引用";
                progressValue = 0.95f;
                EditorUtility.DisplayProgressBar("Sprite 转 PNG", "修复已生成 PNG 的旧引用", 0.95f);
                run.RepairedReferences = RepairExistingConvertedReferences(run.BackupRoot);
                run.Repaired = true;
                return;
            }
            if (!run.AtlasChecked)
            {
                progressMessage = "转换：检查图集引用并等待删除确认";
                progressValue = 0.98f;
                EditorUtility.DisplayProgressBar("Sprite 转 PNG", "检查图集引用并准备删除确认", 0.98f);
                DeleteUnusedAtlases(run.BackupRoot, run.Successes, run.Pending, run.DeletedAtlases, run.KeptAtlases);
                run.AtlasChecked = true;
                FinishConversion(run);
            }
        }
        catch (Exception exception)
        {
            run.Failures.Add("转换流程异常：" + exception.Message);
            FinishConversion(run);
        }
    }

    private void FinishConversion(ConversionRun run)
    {
        EditorApplication.update -= ConversionStep;
        EditorUtility.ClearProgressBar();
        isBusy = false;
        progressMessage = string.Empty;
        progressValue = 0f;
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

        var summary = new StringBuilder();
        summary.AppendLine("转换成功：" + run.Successes.Count);
        summary.AppendLine("复用现有 PNG 并删除旧 .asset：" + run.ReusedPng);
        summary.AppendLine("失败/未处理：" + run.Failures.Count);
        summary.AppendLine("修复既有 PNG 引用：" + run.RepairedReferences);
        summary.AppendLine("删除未引用图集：" + run.DeletedAtlases.Count);
        summary.AppendLine("保留图集：" + run.KeptAtlases.Count);
        summary.AppendLine("备份：" + run.BackupRoot);
        if (run.Failures.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine(string.Join("\n", run.Failures.Take(12).ToArray()));
        }
        Debug.Log("[Sprite Asset To PNG]\n" + summary);
        EditorUtility.DisplayDialog("转换完成", summary.ToString(), "确定");
        conversionRun = null;
        Scan();
    }

    private void ConvertAllSynchronously()
    {
        if (!EditorUtility.DisplayDialog(
                "确认批量转换",
                string.Format(
                    "将转换 {0} 个 Sprite .asset。\n\n原文件会备份到项目 Library/SpriteAssetToPngBackups，转换成功后原 .asset 会被删除。Unity 序列化引用保持不变，但代码中的字符串路径不会自动更新。是否继续？",
                    candidates.Count),
                "转换",
                "取消"))
        {
            return;
        }

        isBusy = true;
        var batchName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupRoot = Path.Combine(ProjectRoot, "Library", "SpriteAssetToPngBackups", batchName);
        var successes = new List<string>();
        var failures = new List<string>();
        var pending = candidates.Where(candidate => !candidate.Blocked).ToArray();
        var deletedAtlases = new List<string>();
        var keptAtlases = new List<string>();
        var repairedReferences = 0;

        try
        {
            for (var index = 0; index < pending.Length; index++)
            {
                var candidate = pending[index];
                var path = candidate.AssetPath;
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Sprite .asset 转 PNG",
                        candidate.AssetPath,
                        (float)index / pending.Length))
                {
                    failures.Add("用户取消，剩余项目未处理。");
                    break;
                }

                string outputPath;
                string error;
                if (TryConvert(candidate, backupRoot, out outputPath, out error))
                {
                    successes.Add(candidate.AssetPath + " -> " + outputPath);
                }
                else
                {
                    AddCandidateWarning(candidate, "转换失败：" + error);
                    failures.Add(path + "：" + error);
                }
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            repairedReferences = RepairExistingConvertedReferences(backupRoot);
            if (repairedReferences > 0)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
            DeleteUnusedAtlases(backupRoot, successes, pending, deletedAtlases, keptAtlases);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            isBusy = false;
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Scan();
        }

        var summary = new StringBuilder();
        summary.AppendLine("成功：" + successes.Count);
        summary.AppendLine("失败/未处理：" + failures.Count);
        summary.AppendLine("备份：" + backupRoot);
        summary.AppendLine("修复既有 PNG 引用：" + repairedReferences);
        summary.AppendLine("删除未引用图集：" + deletedAtlases.Count);
        summary.AppendLine("保留图集：" + keptAtlases.Count);
        if (failures.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine(string.Join("\n", failures.Take(10).ToArray()));
        }

        Debug.Log("[Sprite Asset To PNG]\n" + summary +
                  (successes.Count > 0 ? "\n" + string.Join("\n", successes.ToArray()) : string.Empty));
        EditorUtility.DisplayDialog("转换完成", summary.ToString(), "确定");
    }

    private static bool TryConvert(ConversionCandidate candidate, string backupRoot, out string outputPath, out string error)
    {
        var assetPath = candidate.AssetPath;
        outputPath = candidate.OutputPath;
        error = string.Empty;

        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        if (sprite == null || sprite.texture == null)
        {
            error = "无法读取 Sprite 或源纹理。";
            return false;
        }

        string oldGuid;
        long oldLocalId;
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out oldGuid, out oldLocalId))
        {
            error = "无法读取原 GUID/fileID。";
            return false;
        }

        var settings = SpriteSettings.Capture(sprite, AssetImporter.GetAtPath(assetPath));
        var labels = AssetDatabase.GetLabels(sprite);
        if (candidate.ReuseExistingPng)
        {
            return TryReuseExistingPng(candidate, sprite, labels, backupRoot, oldGuid, oldLocalId,
                out outputPath, out error);
        }
        byte[] pngBytes;
        try
        {
            pngBytes = ExtractPng(sprite);
        }
        catch (Exception exception)
        {
            error = "提取像素失败：" + exception.Message;
            return false;
        }

        var absoluteAssetPath = AssetPathToAbsolute(assetPath);
        var absoluteOutputPath = AssetPathToAbsolute(outputPath);
        var tempOutputPath = CreateTemporaryImportPath();
        var absoluteTempOutputPath = AssetPathToAbsolute(tempOutputPath);
        var backupAssetPath = Path.Combine(backupRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        var backupMetaPath = backupAssetPath + ".meta";
        var referenceEdits = new List<ReferenceEdit>();
        var outputBackupPath = Path.Combine(backupRoot, "Outputs", outputPath.Replace('/', Path.DirectorySeparatorChar));
        var outputBackupMetaPath = outputBackupPath + ".meta";
        var hadOutput = File.Exists(absoluteOutputPath);
        var hadOutputMeta = File.Exists(absoluteOutputPath + ".meta");
        var transactionActive = false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absoluteOutputPath) ?? ProjectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(backupAssetPath) ?? backupRoot);
            File.Copy(absoluteAssetPath, backupAssetPath, true);
            File.Copy(absoluteAssetPath + ".meta", backupMetaPath, true);
            if (hadOutput || hadOutputMeta)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputBackupPath) ?? backupRoot);
                if (hadOutput) File.Copy(absoluteOutputPath, outputBackupPath, true);
                if (hadOutputMeta) File.Copy(absoluteOutputPath + ".meta", outputBackupMetaPath, true);
            }

            File.WriteAllBytes(absoluteTempOutputPath, pngBytes);
            AssetDatabase.ImportAsset(tempOutputPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(tempOutputPath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("Unity 没有为输出文件创建 TextureImporter。");
            }

            settings.Apply(importer);
            ApplySpriteGeometry(sprite, importer);
            importer.SaveAndReimport();

            var temporarySprite = AssetDatabase.LoadAssetAtPath<Sprite>(tempOutputPath);
            string geometryError;
            if (!TryValidateSpriteGeometry(sprite, temporarySprite, out geometryError))
            {
                throw new InvalidOperationException("Sprite 几何迁移校验失败：" + geometryError);
            }

            var tempOutputMetaPath = absoluteTempOutputPath + ".meta";
            var outputMeta = File.ReadAllText(tempOutputMetaPath);
            var outputMetaPath = absoluteOutputPath + ".meta";
            if (!GuidLineRegex.IsMatch(outputMeta))
            {
                throw new InvalidOperationException("输出 PNG 的 meta 中没有有效 GUID。");
            }

            outputMeta = GuidLineRegex.Replace(outputMeta, "guid: " + oldGuid, 1);

            // Keep both unregister operations inside the asset-editing transaction so a
            // batch does not trigger a full AssetDatabase refresh for every candidate.
            AssetDatabase.StartAssetEditing();
            AssetDatabase.DisallowAutoRefresh();
            transactionActive = true;

            // Remove the temporary import and any previous target registration before
            // registering the target path with the original Sprite GUID.
            if (!AssetDatabase.DeleteAsset(tempOutputPath))
            {
                throw new InvalidOperationException("无法清理临时 PNG 导入记录。");
            }

            if (hadOutput || hadOutputMeta)
            {
                if (!AssetDatabase.DeleteAsset(outputPath))
                {
                    throw new InvalidOperationException("无法清理已有 PNG 导入记录。");
                }
            }

            referenceEdits = PrepareReferenceEdits(oldGuid, oldLocalId, oldGuid, SpriteLocalId, assetPath, backupRoot);

            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                throw new InvalidOperationException("无法通过 AssetDatabase 删除原 Sprite .asset。");
            }

            try
            {
                DeleteFileIfExists(absoluteOutputPath);
                DeleteFileIfExists(outputMetaPath);
                DeleteFileIfExists(absoluteAssetPath);
                DeleteFileIfExists(absoluteAssetPath + ".meta");
                DeleteFileIfExists(absoluteTempOutputPath);
                DeleteFileIfExists(tempOutputMetaPath);
                File.WriteAllBytes(absoluteOutputPath, pngBytes);
                File.WriteAllText(outputMetaPath, outputMeta, new UTF8Encoding(false));
                foreach (var edit in referenceEdits)
                {
                    File.WriteAllText(edit.AbsolutePath, edit.UpdatedContent, new UTF8Encoding(false));
                }
            }
            finally
            {
                try
                {
                    AssetDatabase.StopAssetEditing();
                }
                finally
                {
                    AssetDatabase.AllowAutoRefresh();
                }
                transactionActive = false;
            }

            AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var converted = AssetDatabase.LoadAssetAtPath<Sprite>(outputPath);
            string convertedGuid = string.Empty;
            long convertedLocalId = 0L;
            var hasConvertedIdentity = converted != null &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(converted, out convertedGuid, out convertedLocalId);
            if (!hasConvertedIdentity ||
                !string.Equals(convertedGuid, oldGuid, StringComparison.OrdinalIgnoreCase) ||
                convertedLocalId != oldLocalId)
            {
                throw new InvalidOperationException(string.Format(
                    "转换后的 GUID/fileID 与原资源不一致，已回滚。期望 {0}/{1}，实际 {2}/{3}。",
                    oldGuid, oldLocalId,
                    hasConvertedIdentity ? convertedGuid : "<无法读取>",
                    hasConvertedIdentity ? convertedLocalId.ToString() : "<无法读取>"));
            }

            AssetDatabase.SetLabels(converted, labels);
            EditorUtility.SetDirty(converted);
            return true;
        }
        catch (Exception exception)
        {
            if (transactionActive)
            {
                try { AssetDatabase.StopAssetEditing(); } catch (Exception) { }
                try { AssetDatabase.AllowAutoRefresh(); } catch (Exception) { }
            }
            try
            {
                if (File.Exists(absoluteTempOutputPath) || File.Exists(absoluteTempOutputPath + ".meta"))
                {
                    AssetDatabase.DeleteAsset(tempOutputPath);
                }
            }
            catch (Exception) { }
            DeleteFileIfExists(absoluteTempOutputPath);
            DeleteFileIfExists(absoluteTempOutputPath + ".meta");
            RollBack(assetPath, outputPath, backupAssetPath, backupMetaPath,
                outputBackupPath, outputBackupMetaPath, hadOutput, hadOutputMeta, referenceEdits);
            error = exception.Message;
            return false;
        }
    }

    private static bool TryReuseExistingPng(
        ConversionCandidate candidate,
        Sprite sourceSprite,
        string[] labels,
        string backupRoot,
        string oldGuid,
        long oldLocalId,
        out string outputPath,
        out string error)
    {
        outputPath = candidate.OutputPath;
        error = string.Empty;
        var assetPath = candidate.AssetPath;
        var pngPath = candidate.OutputPath;
        var absoluteAssetPath = AssetPathToAbsolute(assetPath);
        var absolutePngPath = AssetPathToAbsolute(pngPath);
        var backupAssetPath = Path.Combine(backupRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        var backupMetaPath = backupAssetPath + ".meta";
        var outputBackupPath = Path.Combine(backupRoot, "Outputs", pngPath.Replace('/', Path.DirectorySeparatorChar));
        var outputBackupMetaPath = outputBackupPath + ".meta";
        var referenceEdits = new List<ReferenceEdit>();
        var transactionActive = false;
        var hadOutput = File.Exists(absolutePngPath);
        var hadOutputMeta = File.Exists(absolutePngPath + ".meta");

        try
        {
            if (!hadOutput || !hadOutputMeta || string.IsNullOrEmpty(candidate.ExistingPngGuid))
            {
                throw new InvalidOperationException("复用目标 PNG 或其 meta/GUID 已不存在，请重新预检。");
            }

            string[] fr2Referencers;
            string fr2Error;
            if (!TryGetFindReference2DirectReferencers(oldGuid, out fr2Referencers, out fr2Error))
            {
                throw new InvalidOperationException("依赖 FindReference2 缓存：" + fr2Error);
            }
            var nonTextReferencers = fr2Referencers.Where(path => !IsUnitySerializedTextAsset(path)).Take(4).ToArray();
            if (nonTextReferencers.Length > 0)
            {
                throw new InvalidOperationException(
                    "FindReference2 检出无法安全文本迁移的直接引用：" + string.Join("、", nonTextReferencers));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absoluteAssetPath) ?? ProjectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(backupAssetPath) ?? backupRoot);
            File.Copy(absoluteAssetPath, backupAssetPath, true);
            File.Copy(absoluteAssetPath + ".meta", backupMetaPath, true);
            Directory.CreateDirectory(Path.GetDirectoryName(outputBackupPath) ?? backupRoot);
            File.Copy(absolutePngPath, outputBackupPath, true);
            File.Copy(absolutePngPath + ".meta", outputBackupMetaPath, true);

            var importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("复用目标 PNG 没有 TextureImporter。");
            ApplySpriteGeometry(sourceSprite, importer);
            importer.SaveAndReimport();

            var geometryTarget = AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
            string geometryError;
            if (!TryValidateSpriteGeometry(sourceSprite, geometryTarget, out geometryError))
            {
                throw new InvalidOperationException("Sprite 几何迁移校验失败：" + geometryError);
            }

            referenceEdits = PrepareReferenceEdits(
                oldGuid, oldLocalId, candidate.ExistingPngGuid, SpriteLocalId, assetPath, backupRoot);
            foreach (var edit in referenceEdits)
            {
                File.WriteAllText(edit.AbsolutePath, edit.UpdatedContent, new UTF8Encoding(false));
            }

            var remainingReferences = CountSerializedGuidReferences(oldGuid, assetPath);
            if (remainingReferences > 0)
            {
                throw new InvalidOperationException("迁移后仍发现 " + remainingReferences + " 处旧 GUID 序列化引用，已阻断删除。");
            }

            AssetDatabase.StartAssetEditing();
            AssetDatabase.DisallowAutoRefresh();
            transactionActive = true;
            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                throw new InvalidOperationException("无法删除已无引用的原 Sprite .asset。");
            }
            AssetDatabase.StopAssetEditing();
            AssetDatabase.AllowAutoRefresh();
            transactionActive = false;

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var converted = AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
            string convertedGuid;
            long convertedLocalId;
            if (converted == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(converted, out convertedGuid, out convertedLocalId) ||
                !string.Equals(convertedGuid, candidate.ExistingPngGuid, StringComparison.OrdinalIgnoreCase) ||
                convertedLocalId != SpriteLocalId)
            {
                throw new InvalidOperationException("复用后的 PNG GUID/fileID 校验失败，已回滚。");
            }
            AssetDatabase.SetLabels(converted, labels);
            EditorUtility.SetDirty(converted);
            return true;
        }
        catch (Exception exception)
        {
            if (transactionActive)
            {
                try { AssetDatabase.StopAssetEditing(); } catch (Exception) { }
                try { AssetDatabase.AllowAutoRefresh(); } catch (Exception) { }
            }
            RollBack(assetPath, pngPath, backupAssetPath, backupMetaPath,
                outputBackupPath, outputBackupMetaPath, hadOutput, hadOutputMeta, referenceEdits);
            error = exception.Message;
            return false;
        }
    }

    private static void ApplySpriteGeometry(Sprite sourceSprite, TextureImporter importer)
    {
        if (sourceSprite == null || importer == null) throw new ArgumentNullException();

        var factories = new SpriteDataProviderFactories();
        factories.Init();
        var provider = factories.GetSpriteEditorDataProviderFromObject(importer);
        if (provider == null)
        {
            throw new InvalidOperationException("目标 PNG 不支持 Unity 2D Sprite DataProvider，无法迁移 Sprite 几何数据。");
        }
        provider.InitSpriteEditorDataProvider();
        var spriteRects = provider.GetSpriteRects();
        if (spriteRects == null || spriteRects.Length == 0)
        {
            throw new InvalidOperationException("目标 PNG 没有可写入的 SpriteRect。");
        }

        var targetRect = spriteRects[0];
        var sourceRect = sourceSprite.packed ? sourceSprite.textureRect : sourceSprite.rect;
        var width = Mathf.RoundToInt(sourceRect.width);
        var height = Mathf.RoundToInt(sourceRect.height);
        targetRect.name = sourceSprite.name;
        targetRect.rect = new Rect(0f, 0f, width, height);
        targetRect.border = sourceSprite.border;
        targetRect.pivot = GetNormalizedPivot(sourceSprite);
        targetRect.alignment = SpriteAlignment.Custom;
        provider.SetSpriteRects(spriteRects);

        var spriteId = targetRect.spriteID;
        var pixelOffset = new Vector2(
            sourceSprite.pivot.x - sourceRect.width * 0.5f,
            sourceSprite.pivot.y - sourceRect.height * 0.5f);
        var pixelsPerUnit = sourceSprite.pixelsPerUnit <= 0f ? 1f : sourceSprite.pixelsPerUnit;

        var meshProvider = provider.GetDataProvider<ISpriteMeshDataProvider>();
        if (meshProvider != null)
        {
            var sourceVertices = sourceSprite.vertices ?? new Vector2[0];
            var vertices = new Vertex2DMetaData[sourceVertices.Length];
            for (var i = 0; i < sourceVertices.Length; i++)
            {
                vertices[i] = new Vertex2DMetaData
                {
                    position = sourceVertices[i] * pixelsPerUnit + pixelOffset,
                    boneWeight = new BoneWeight()
                };
            }
            var sourceTriangles = sourceSprite.triangles;
            var indices = sourceTriangles == null
                ? new int[0]
                : sourceTriangles.Select(index => (int)index).ToArray();
            var edges = BuildMeshEdges(indices, sourceVertices.Length);
            meshProvider.SetVertices(spriteId, vertices);
            meshProvider.SetIndices(spriteId, indices);
            meshProvider.SetEdges(spriteId, edges);

            var outlineProvider = provider.GetDataProvider<ISpriteOutlineDataProvider>();
            if (outlineProvider != null && edges.Length > 0)
            {
                outlineProvider.SetOutlines(spriteId,
                    BuildMeshOutlines(vertices.Select(vertex => vertex.position).ToArray(), edges));
            }
        }

        var physicsProvider = provider.GetDataProvider<ISpritePhysicsOutlineDataProvider>();
        if (physicsProvider != null)
        {
            var physicsShapes = new List<Vector2[]>();
            var shapeCount = sourceSprite.GetPhysicsShapeCount();
            for (var shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                var shape = new List<Vector2>();
                sourceSprite.GetPhysicsShape(shapeIndex, shape);
                if (shape.Count == 0) continue;
                var converted = new Vector2[shape.Count];
                for (var pointIndex = 0; pointIndex < shape.Count; pointIndex++)
                {
                    converted[pointIndex] = shape[pointIndex] * pixelsPerUnit + pixelOffset;
                }
                physicsShapes.Add(converted);
            }
            physicsProvider.SetOutlines(spriteId, physicsShapes);
        }

        provider.Apply();
    }

    private static Vector2Int[] BuildMeshEdges(int[] triangles, int vertexCount)
    {
        if (triangles == null || triangles.Length < 3 || vertexCount <= 0) return new Vector2Int[0];
        var edgeCounts = new Dictionary<long, int>();
        var edgeValues = new Dictionary<long, Vector2Int>();
        for (var i = 0; i + 2 < triangles.Length; i += 3)
        {
            AddMeshEdge(triangles[i], triangles[i + 1], vertexCount, edgeCounts, edgeValues);
            AddMeshEdge(triangles[i + 1], triangles[i + 2], vertexCount, edgeCounts, edgeValues);
            AddMeshEdge(triangles[i + 2], triangles[i], vertexCount, edgeCounts, edgeValues);
        }
        return edgeCounts.Where(pair => pair.Value == 1).Select(pair => edgeValues[pair.Key]).ToArray();
    }

    private static List<Vector2[]> BuildMeshOutlines(Vector2[] vertices, Vector2Int[] edges)
    {
        var result = new List<Vector2[]>();
        if (vertices == null || vertices.Length == 0 || edges == null || edges.Length == 0) return result;

        var neighbors = new Dictionary<int, List<int>>();
        var remaining = new HashSet<long>();
        foreach (var edge in edges)
        {
            if (edge.x < 0 || edge.y < 0 || edge.x >= vertices.Length || edge.y >= vertices.Length || edge.x == edge.y)
            {
                throw new InvalidOperationException("Sprite 网格边界包含无效顶点。");
            }
            AddOutlineNeighbor(neighbors, edge.x, edge.y);
            AddOutlineNeighbor(neighbors, edge.y, edge.x);
            remaining.Add(GetMeshEdgeKey(edge.x, edge.y));
        }
        if (neighbors.Any(pair => pair.Value.Distinct().Count() != 2))
        {
            throw new InvalidOperationException("Sprite 网格边界不是可可靠迁移的闭合轮廓。");
        }

        while (remaining.Count > 0)
        {
            var firstKey = remaining.First();
            var start = (int)(firstKey >> 32);
            var current = start;
            var previous = -1;
            var points = new List<Vector2>();
            for (var step = 0; step <= edges.Length; step++)
            {
                points.Add(vertices[current]);
                var nextCandidates = neighbors[current].Where(neighbor =>
                    neighbor != previous && remaining.Contains(GetMeshEdgeKey(current, neighbor))).ToArray();
                if (nextCandidates.Length == 0)
                {
                    throw new InvalidOperationException("Sprite 网格边界无法组成闭合轮廓。");
                }
                var next = nextCandidates[0];
                if (!remaining.Remove(GetMeshEdgeKey(current, next)))
                {
                    throw new InvalidOperationException("Sprite 网格边界无法组成闭合轮廓。");
                }
                previous = current;
                current = next;
                if (current == start) break;
            }
            if (current != start || points.Count < 3)
            {
                throw new InvalidOperationException("Sprite 网格边界无法组成有效轮廓。");
            }
            result.Add(points.ToArray());
        }
        return result;
    }

    private static void AddOutlineNeighbor(Dictionary<int, List<int>> neighbors, int vertex, int neighbor)
    {
        List<int> values;
        if (!neighbors.TryGetValue(vertex, out values))
        {
            values = new List<int>();
            neighbors.Add(vertex, values);
        }
        values.Add(neighbor);
    }

    private static long GetMeshEdgeKey(int first, int second)
    {
        var min = Math.Min(first, second);
        var max = Math.Max(first, second);
        return ((long)min << 32) | (uint)max;
    }

    private static bool TryValidateSpriteGeometry(Sprite source, Sprite target, out string error)
    {
        error = string.Empty;
        if (source == null || target == null)
        {
            error = "源或目标 Sprite 无法读取";
            return false;
        }

        var sourceVertices = source.vertices ?? new Vector2[0];
        var targetVertices = target.vertices ?? new Vector2[0];
        if (sourceVertices.Length != targetVertices.Length)
        {
            error = "网格顶点数量不一致（" + sourceVertices.Length + " -> " + targetVertices.Length + "）";
            return false;
        }
        var sourceVertexKeys = sourceVertices.Select(GeometryPointKey).OrderBy(value => value).ToArray();
        var targetVertexKeys = targetVertices.Select(GeometryPointKey).OrderBy(value => value).ToArray();
        if (!sourceVertexKeys.SequenceEqual(targetVertexKeys))
        {
            error = "网格顶点位置不一致";
            return false;
        }

        string[] sourceTriangles;
        string[] targetTriangles;
        if (!TryBuildTriangleKeys(source, out sourceTriangles) || !TryBuildTriangleKeys(target, out targetTriangles) ||
            !sourceTriangles.SequenceEqual(targetTriangles))
        {
            error = "网格三角形数据不一致";
            return false;
        }

        var sourcePhysics = GetPhysicsShapeKeys(source);
        var targetPhysics = GetPhysicsShapeKeys(target);
        if (!sourcePhysics.SequenceEqual(targetPhysics))
        {
            error = "PhysicsShape 数据不一致";
            return false;
        }
        return true;
    }

    private static bool TryBuildTriangleKeys(Sprite sprite, out string[] keys)
    {
        var vertices = sprite.vertices ?? new Vector2[0];
        var triangles = sprite.triangles ?? new ushort[0];
        if (triangles.Length % 3 != 0)
        {
            keys = new string[0];
            return false;
        }
        var result = new List<string>();
        for (var i = 0; i < triangles.Length; i += 3)
        {
            var first = triangles[i];
            var second = triangles[i + 1];
            var third = triangles[i + 2];
            if (first < 0 || second < 0 || third < 0 ||
                first >= vertices.Length || second >= vertices.Length || third >= vertices.Length)
            {
                keys = new string[0];
                return false;
            }
            var points = new[]
            {
                GeometryPointKey(vertices[first]),
                GeometryPointKey(vertices[second]),
                GeometryPointKey(vertices[third])
            };
            Array.Sort(points, StringComparer.Ordinal);
            result.Add(string.Join("|", points));
        }
        keys = result.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return true;
    }

    private static string[] GetPhysicsShapeKeys(Sprite sprite)
    {
        var shapes = new List<string>();
        for (var shapeIndex = 0; shapeIndex < sprite.GetPhysicsShapeCount(); shapeIndex++)
        {
            var points = new List<Vector2>();
            sprite.GetPhysicsShape(shapeIndex, points);
            shapes.Add(string.Join("|", points.Select(GeometryPointKey).OrderBy(value => value, StringComparer.Ordinal).ToArray()));
        }
        return shapes.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static string GeometryPointKey(Vector2 point)
    {
        return Mathf.RoundToInt(point.x * 10000f) + "," + Mathf.RoundToInt(point.y * 10000f);
    }

    private static void AddMeshEdge(
        int first,
        int second,
        int vertexCount,
        Dictionary<long, int> edgeCounts,
        Dictionary<long, Vector2Int> edgeValues)
    {
        if (first < 0 || second < 0 || first >= vertexCount || second >= vertexCount || first == second) return;
        var key = GetMeshEdgeKey(first, second);
        var min = Math.Min(first, second);
        var max = Math.Max(first, second);
        int count;
        edgeCounts.TryGetValue(key, out count);
        edgeCounts[key] = count + 1;
        if (!edgeValues.ContainsKey(key)) edgeValues[key] = new Vector2Int(min, max);
    }

    private static bool TryEnsureFindReference2Ready(out string error)
    {
        error = string.Empty;
        Type cacheType = null;
        Type refType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (cacheType == null) cacheType = assembly.GetType("vietlabs.fr2.FR2_Cache", false);
            if (refType == null) refType = assembly.GetType("vietlabs.fr2.FR2_Ref", false);
        }
        if (cacheType == null || refType == null)
        {
            error = "未找到 FindReference2 程序集。";
            return false;
        }

        try
        {
            var readyProperty = cacheType.GetProperty("isReady", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (readyProperty == null || !(bool)readyProperty.GetValue(null, null))
            {
                error = "FindReference2 缓存尚未就绪，请先打开 Find Reference 2 并完成缓存扫描。";
                return false;
            }

            var apiProperty = cacheType.GetProperty("Api", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var api = apiProperty == null ? null : apiProperty.GetValue(null, null);
            if (api == null)
            {
                error = "FindReference2 缓存对象不可用，请先刷新缓存。";
                return false;
            }
            var disabledProperty = api.GetType().GetProperty("disabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (disabledProperty != null && (bool)disabledProperty.GetValue(api, null))
            {
                error = "FindReference2 当前已禁用，请启用并刷新缓存。";
                return false;
            }
            var findUsedBy = refType.GetMethod("FindUsedBy", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string[]) }, null);
            if (findUsedBy == null)
            {
                error = "FindReference2 缺少引用查询接口，请更新或刷新缓存。";
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            error = "读取 FindReference2 缓存状态失败：" + exception.Message;
            return false;
        }
    }

    private static bool TryGetFindReference2DirectReferencers(string guid, out string[] paths, out string error)
    {
        paths = new string[0];
        if (string.IsNullOrEmpty(guid))
        {
            error = "资源 GUID 为空。";
            return false;
        }
        if (!TryEnsureFindReference2Ready(out error)) return false;

        Type refType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            refType = assembly.GetType("vietlabs.fr2.FR2_Ref", false);
            if (refType != null) break;
        }
        if (refType == null)
        {
            error = "未找到 FindReference2 引用查询类型。";
            return false;
        }

        try
        {
            var method = refType.GetMethod("FindUsedBy", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string[]) }, null);
            var result = method == null ? null : method.Invoke(null, new object[] { new[] { guid } }) as IDictionary;
            if (result == null)
            {
                error = "FindReference2 没有返回可用的引用结果。";
                return false;
            }

            var directPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in result)
            {
                var value = entry.Value;
                if (value == null) continue;
                var valueType = value.GetType();
                var depthField = valueType.GetField("depth", BindingFlags.Public | BindingFlags.Instance);
                if (depthField == null || (int)depthField.GetValue(value) != 1) continue;
                var assetField = valueType.GetField("asset", BindingFlags.Public | BindingFlags.Instance);
                var asset = assetField == null ? null : assetField.GetValue(value);
                if (asset == null) continue;
                var pathProperty = asset.GetType().GetProperty("assetPath", BindingFlags.Public | BindingFlags.Instance);
                var path = pathProperty == null ? null : pathProperty.GetValue(asset, null) as string;
                if (!string.IsNullOrEmpty(path)) directPaths.Add(path.Replace('\\', '/'));
            }
            paths = directPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            error = string.Empty;
            return true;
        }
        catch (TargetInvocationException exception)
        {
            error = "FindReference2 引用查询失败：" +
                    (exception.InnerException == null ? exception.Message : exception.InnerException.Message);
            return false;
        }
        catch (Exception exception)
        {
            error = "FindReference2 引用查询失败：" + exception.Message;
            return false;
        }
    }

    private static int CountSerializedGuidReferences(string guid, string excludedPath)
    {
        if (string.IsNullOrEmpty(guid)) return 0;
        var count = 0;
        foreach (var absolutePath in EnumerateAssetTextFiles())
        {
            var path = AbsoluteToAssetPath(absolutePath);
            if (string.Equals(path, excludedPath, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase)) continue;
            string content;
            try { content = File.ReadAllText(absolutePath); } catch (Exception) { continue; }
            count += SerializedGuidRegex.Matches(content).Cast<Match>()
                .Count(match => string.Equals(match.Groups[1].Value, guid, StringComparison.OrdinalIgnoreCase));
        }
        return count;
    }

    private static byte[] ExtractPng(Sprite sprite)
    {
        var source = sprite.texture;
        var rect = sprite.packed ? sprite.textureRect : sprite.rect;
        var x = Mathf.RoundToInt(rect.x);
        var y = Mathf.RoundToInt(rect.y);
        var width = Mathf.RoundToInt(rect.width);
        var height = Mathf.RoundToInt(rect.height);
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x + width > source.width || y + height > source.height)
        {
            throw new InvalidOperationException("Sprite 裁切区域超出源纹理范围。");
        }

        var temporary = RenderTexture.GetTemporary(
            source.width,
            source.height,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        Texture2D cropped = null;
        try
        {
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            cropped = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            cropped.ReadPixels(new Rect(x, y, width, height), 0, 0, false);
            cropped.Apply(false, false);
            return cropped.EncodeToPNG();
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
            if (cropped != null)
            {
                DestroyImmediate(cropped);
            }
        }
    }

    private static void RollBack(
        string assetPath,
        string outputPath,
        string backupAssetPath,
        string backupMetaPath,
        string outputBackupPath,
        string outputBackupMetaPath,
        bool hadOutput,
        bool hadOutputMeta,
        IEnumerable<ReferenceEdit> referenceEdits)
    {
        AssetDatabase.DisallowAutoRefresh();
        try
        {
            var absoluteOutput = AssetPathToAbsolute(outputPath);
            DeleteFileIfExists(absoluteOutput);
            DeleteFileIfExists(absoluteOutput + ".meta");

            var absoluteAsset = AssetPathToAbsolute(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absoluteAsset) ?? ProjectRoot);
            if (File.Exists(backupAssetPath)) File.Copy(backupAssetPath, absoluteAsset, true);
            if (File.Exists(backupMetaPath)) File.Copy(backupMetaPath, absoluteAsset + ".meta", true);

            if (hadOutput && File.Exists(outputBackupPath)) File.Copy(outputBackupPath, absoluteOutput, true);
            if (hadOutputMeta && File.Exists(outputBackupMetaPath)) File.Copy(outputBackupMetaPath, absoluteOutput + ".meta", true);

            foreach (var edit in referenceEdits)
            {
                if (File.Exists(edit.BackupPath)) File.Copy(edit.BackupPath, edit.AbsolutePath, true);
            }
        }
        finally
        {
            AssetDatabase.AllowAutoRefresh();
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
    }

    private static List<ReferenceEdit> PrepareReferenceEdits(
        string guid,
        long localId,
        string targetGuid,
        long targetLocalId,
        string sourceAssetPath,
        string backupRoot)
    {
        var result = new List<ReferenceEdit>();
        var pattern = new Regex(
            "(\\{fileID:\\s*" + localId + ",\\s*guid:\\s*" + Regex.Escape(guid) + ",\\s*type:\\s*)2(\\s*\\})",
            RegexOptions.CultureInvariant);
        var replacement = "{fileID: " + targetLocalId + ", guid: " + targetGuid + ", type: 3}";

        foreach (var absolutePath in EnumerateAssetTextFiles())
        {
            var assetPath = AbsoluteToAssetPath(absolutePath);
            if (string.Equals(assetPath, sourceAssetPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (assetPath.EndsWith("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(absolutePath);
            }
            catch (Exception)
            {
                continue;
            }

            if (!pattern.IsMatch(content))
            {
                continue;
            }

            var backupPath = Path.Combine(
                backupRoot,
                "ReferenceEdits",
                guid,
                assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath) ?? backupRoot);
            File.Copy(absolutePath, backupPath, true);
            result.Add(new ReferenceEdit
            {
                AbsolutePath = absolutePath,
                BackupPath = backupPath,
                UpdatedContent = pattern.Replace(content, replacement)
            });
        }

        return result;
    }

    private void BuildAtlasGroups()
    {
        atlases.Clear();
        foreach (var group in candidates.Where(candidate => !candidate.ReuseExistingPng)
                     .GroupBy(c => c.SourceTexturePath, StringComparer.OrdinalIgnoreCase))
        {
            atlases.Add(new AtlasInfo
            {
                SourceTexturePath = group.Key,
                SourceTextureGuid = group.First().SourceTextureGuid,
                CandidateCount = group.Count()
            });
        }
        atlases.Sort((a, b) => string.Compare(a.SourceTexturePath, b.SourceTexturePath, StringComparison.OrdinalIgnoreCase));
    }

    private static int RepairExistingConvertedReferences(string backupRoot)
    {
        var repaired = 0;
        foreach (var absolutePath in Directory.GetFiles(Application.dataPath, "*.png", SearchOption.AllDirectories))
        {
            var metaPath = absolutePath + ".meta";
            if (!File.Exists(metaPath)) continue;
            string guid;
            try { guid = ReadMetaGuid(metaPath); } catch (Exception) { continue; }
            if (string.IsNullOrEmpty(guid)) continue;
            var assetPath = AbsoluteToAssetPath(absolutePath);
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (sprite == null) continue;
            long localId;
            string actualGuid;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out actualGuid, out localId) || localId != SpriteLocalId) continue;
            var edits = PrepareReferenceEdits(guid, localId, guid, SpriteLocalId, assetPath, backupRoot);
            foreach (var edit in edits)
            {
                File.WriteAllText(edit.AbsolutePath, edit.UpdatedContent, new UTF8Encoding(false));
                repaired++;
            }
        }
        return repaired;
    }

    private static int CountExistingReferenceRepairs()
    {
        var count = 0;
        foreach (var absolutePath in Directory.GetFiles(Application.dataPath, "*.png", SearchOption.AllDirectories))
        {
            var metaPath = absolutePath + ".meta";
            if (!File.Exists(metaPath)) continue;
            string guid;
            try { guid = ReadMetaGuid(metaPath); } catch (Exception) { continue; }
            if (string.IsNullOrEmpty(guid)) continue;
            var assetPath = AbsoluteToAssetPath(absolutePath);
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (sprite == null) continue;
            long localId;
            string actualGuid;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out actualGuid, out localId)) continue;
            count += CountType2References(guid, localId, assetPath);
        }
        return count;
    }

    private static int CountType2References(string guid, long localId, string excludedPath)
    {
        var pattern = new Regex("\\{fileID:\\s*" + localId + ",\\s*guid:\\s*" + Regex.Escape(guid) + ",\\s*type:\\s*2\\s*\\}", RegexOptions.Compiled);
        var count = 0;
        foreach (var absolutePath in EnumerateAssetTextFiles())
        {
            var path = AbsoluteToAssetPath(absolutePath);
            if (string.Equals(path, excludedPath, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase)) continue;
            string content;
            try { content = File.ReadAllText(absolutePath); } catch (Exception) { continue; }
            count += pattern.Matches(content).Count;
        }
        return count;
    }

    private static void DeleteUnusedAtlases(
        string backupRoot,
        List<string> successes,
        IEnumerable<ConversionCandidate> pending,
        List<string> deleted,
        List<string> kept)
    {
        if (successes.Count == 0) return;
        var successPaths = new HashSet<string>(
            successes.Select(s => s.Split(new[] { " -> " }, StringSplitOptions.None)[0]),
            StringComparer.OrdinalIgnoreCase);
        var sourcePaths = pending.Where(c => successPaths.Contains(c.AssetPath) && !c.ReuseExistingPng)
            .Select(c => c.SourceTexturePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var safe = new List<AtlasUsage>();
        foreach (var sourcePath in sourcePaths)
        {
            var usage = AnalyzeAtlasUsage(sourcePath);
            if (usage.CanDelete) safe.Add(usage);
            else kept.Add(sourcePath + "（" + string.Join("；", usage.Blockers.Take(4).ToArray()) + "）");
        }
        if (safe.Count == 0) return;

        var message = "以下图集当前未发现有效引用，删除前会备份：\n\n" +
                      string.Join("\n", safe.Select(a => a.Path).ToArray()) + "\n\n是否删除？";
        if (!EditorUtility.DisplayDialog("二次确认：删除未引用图集", message, "删除图集", "保留"))
        {
            kept.AddRange(safe.Select(a => a.Path + "（用户选择保留）"));
            return;
        }

        AssetDatabase.DisallowAutoRefresh();
        try
        {
            foreach (var atlas in safe)
            {
                var absolute = AssetPathToAbsolute(atlas.Path);
                var backup = Path.Combine(backupRoot, "Atlases", atlas.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(backup) ?? backupRoot);
                if (File.Exists(absolute)) File.Copy(absolute, backup, true);
                if (File.Exists(absolute + ".meta")) File.Copy(absolute + ".meta", backup + ".meta", true);
                DeleteFileIfExists(absolute);
                DeleteFileIfExists(absolute + ".meta");
                deleted.Add(atlas.Path);
            }
        }
        finally
        {
            AssetDatabase.AllowAutoRefresh();
        }
    }

    private static AtlasUsage AnalyzeAtlasUsage(string sourcePath)
    {
        var usage = new AtlasUsage { Path = sourcePath, Guid = AssetDatabase.AssetPathToGUID(sourcePath) };
        if (string.IsNullOrEmpty(usage.Guid))
        {
            usage.Blockers.Add("无法读取图集 GUID");
            return usage;
        }
        string fr2Error;
        if (!TryEnsureFindReference2Ready(out fr2Error))
        {
            usage.Blockers.Add("依赖 FindReference2 缓存：" + fr2Error);
        }

        foreach (var absolutePath in EnumerateAssetTextFiles())
        {
            var path = AbsoluteToAssetPath(absolutePath);
            if (string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, sourcePath + ".meta", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase)) continue;
            string content;
            try { content = File.ReadAllText(absolutePath); } catch (Exception) { continue; }
            if (ContainsGuidReference(content, usage.Guid)) usage.Blockers.Add(path + "（GUID 引用）");
        }

        foreach (var absolutePath in EnumerateTextFiles())
        {
            var path = AbsoluteToAssetPath(absolutePath);
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase)) continue;
            string content;
            try { content = File.ReadAllText(absolutePath); } catch (Exception) { continue; }
            if (content.IndexOf(sourcePath, StringComparison.OrdinalIgnoreCase) >= 0 ||
                content.IndexOf(sourcePath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                usage.Blockers.Add(path + "（字符串路径引用）");
            }
        }

        foreach (var absolutePath in Directory.GetFiles(Application.dataPath, "*.asset", SearchOption.AllDirectories))
        {
            var path = AbsoluteToAssetPath(absolutePath);
            var sprite = AssetDatabase.LoadMainAssetAtPath(path) as Sprite;
            if (sprite == null || sprite.texture == null) continue;
            if (string.Equals(AssetDatabase.GetAssetPath(sprite.texture), sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                usage.Blockers.Add(path + "（仍有 Sprite .asset）");
            }
        }

        string[] fr2Referencers;
        if (TryGetFindReference2DirectReferencers(usage.Guid, out fr2Referencers, out fr2Error))
        {
            foreach (var pluginPath in fr2Referencers)
            {
                if (string.Equals(pluginPath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                    pluginPath.EndsWith("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase)) continue;
                usage.Blockers.Add(pluginPath + "（FindReference2）");
            }
        }
        else
        {
            usage.Blockers.Add("FindReference2 引用查询失败：" + fr2Error);
        }
        usage.Blockers = usage.Blockers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        usage.CanDelete = usage.Blockers.Count == 0;
        return usage;
    }

    private static bool ContainsGuidReference(string content, string guid)
    {
        return SerializedGuidRegex.Matches(content).Cast<Match>()
            .Any(match => string.Equals(match.Groups[1].Value, guid, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetOutputPath(string assetPath, string guid, out bool alreadyConverted)
    {
        var desired = Path.ChangeExtension(assetPath, ".png").Replace('\\', '/');
        alreadyConverted = false;
        var desiredAbsolute = AssetPathToAbsolute(desired);
        if (!File.Exists(desiredAbsolute) && !File.Exists(desiredAbsolute + ".meta"))
        {
            return desired;
        }

        string existingGuid = string.Empty;
        if (File.Exists(desiredAbsolute + ".meta"))
        {
            try { existingGuid = ReadMetaGuid(desiredAbsolute + ".meta"); } catch (Exception) { }
        }
        if (string.Equals(existingGuid, guid, StringComparison.OrdinalIgnoreCase) &&
            AssetDatabase.LoadAssetAtPath<Sprite>(desired) != null)
        {
            alreadyConverted = true;
            return desired;
        }

        var directory = Path.GetDirectoryName(assetPath).Replace('\\', '/');
        var conflictDirectory = directory + "/ConvertedPng";
        var baseName = Path.GetFileNameWithoutExtension(assetPath) + ".png";
        return AssetDatabase.GenerateUniqueAssetPath(conflictDirectory + "/" + baseName);
    }

    private static string CreateTemporaryImportPath()
    {
        Directory.CreateDirectory(AssetPathToAbsolute(TemporaryImportFolder));
        return TemporaryImportFolder + "/SpriteAssetToPngTemp_" + Guid.NewGuid().ToString("N") + ".png";
    }

    private static string ReadMetaGuid(string metaPath)
    {
        var content = File.ReadAllText(metaPath);
        var match = GuidLineRegex.Match(content);
        return match.Success ? match.Value.Substring(match.Value.IndexOf(':') + 1).Trim() : string.Empty;
    }

    private static IEnumerable<string> EnumerateAssetTextFiles()
    {
        foreach (var path in Directory.GetFiles(Application.dataPath, "*", SearchOption.AllDirectories))
        {
            if (IsUnitySerializedTextFile(path)) yield return path;
        }
    }

    private static bool IsUnitySerializedTextAsset(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return IsUnitySerializedTextFile(AssetPathToAbsolute(assetPath));
    }

    private static bool IsUnitySerializedTextFile(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath) ||
            absolutePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(absolutePath)) return false;
        try
        {
            var header = new byte[5];
            using (var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Read(header, 0, header.Length) != header.Length) return false;
            }
            return header[0] == (byte)'%' && header[1] == (byte)'Y' && header[2] == (byte)'A' &&
                   header[3] == (byte)'M' && header[4] == (byte)'L';
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateTextFiles()
    {
        return Directory.GetFiles(Application.dataPath, "*", SearchOption.AllDirectories)
            .Where(path => TextExtensions.Contains(Path.GetExtension(path)));
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private void SetFolderPath(string path)
    {
        folderPath = string.IsNullOrWhiteSpace(path) ? DefaultFolder : path.Replace('\\', '/').TrimEnd('/');
        folderObject = AssetDatabase.LoadAssetAtPath<DefaultAsset>(folderPath);
    }

    private void AddScanProblemItem(string assetPath, string message)
    {
        scanProblems.Add(assetPath + "：" + message);
        problemItems.Add(new ScanProblemItem
        {
            AssetPath = assetPath,
            Message = message
        });
    }

    private void ClearScan()
    {
        ClearPreviewCache();
        candidates.Clear();
        problemItems.Clear();
        atlases.Clear();
        scanProblems.Clear();
        specialAssetReports.Clear();
        scannedAssetCount = 0;
        pivotNormalizationCount = 0;
        layoutApproximationCount = 0;
        existingReferenceRepairCount = 0;
        hasScanned = false;
    }

    private static bool TryNormalizeFolder(string value, out string assetPath, out string error)
    {
        assetPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "请输入 Assets 下的目录路径。";
            return false;
        }

        var candidate = value.Trim().Trim('"').Replace('\\', '/').TrimEnd('/');
        if (Path.IsPathRooted(candidate))
        {
            var absolute = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dataPath = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!absolute.Equals(dataPath, StringComparison.OrdinalIgnoreCase) &&
                !absolute.StartsWith(dataPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = "目录必须位于当前项目的 Assets 内。";
                return false;
            }

            candidate = "Assets" + absolute.Substring(dataPath.Length).Replace('\\', '/');
        }

        if (!candidate.Equals("Assets", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            error = "路径必须是 Assets 或 Assets/...。";
            return false;
        }

        candidate = "Assets" + candidate.Substring(6);
        if (!AssetDatabase.IsValidFolder(candidate))
        {
            error = "目录不存在或不是 Unity 项目目录：" + candidate;
            return false;
        }

        assetPath = candidate;
        return true;
    }

    private static string AbsoluteToAssetPath(string absolutePath)
    {
        var relative = Path.GetFullPath(absolutePath).Substring(ProjectRoot.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Replace('\\', '/');
    }

    private static string AssetPathToAbsolute(string assetPath)
    {
        return Path.GetFullPath(Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static Vector2 GetNormalizedPivot(Sprite sprite)
    {
        return new Vector2(
            sprite.rect.width <= 0f ? 0.5f : sprite.pivot.x / sprite.rect.width,
            sprite.rect.height <= 0f ? 0.5f : sprite.pivot.y / sprite.rect.height);
    }

    private static bool RequiresPivotNormalization(Vector2 pivot)
    {
        return float.IsNaN(pivot.x) || float.IsInfinity(pivot.x) ||
               float.IsNaN(pivot.y) || float.IsInfinity(pivot.y) ||
               pivot.x < 0f || pivot.x > 1f || pivot.y < 0f || pivot.y > 1f;
    }

    private static float NormalizePivotAxis(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value) ? 0.5f : Mathf.Clamp01(value);
    }

    private static bool HasSerializedOffset(string assetPath)
    {
        try
        {
            var content = File.ReadAllText(AssetPathToAbsolute(assetPath));
            var match = Regex.Match(content, @"m_Offset:\s*\{x:\s*([-+0-9.eE]+),\s*y:\s*([-+0-9.eE]+)\}");
            if (!match.Success) return false;
            float x;
            float y;
            return float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out x) &&
                   float.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out y) &&
                   (Math.Abs(x) > 0.0001f || Math.Abs(y) > 0.0001f);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ProjectRoot
    {
        get { return Directory.GetParent(Application.dataPath).FullName; }
    }

    private sealed class ConversionCandidate
    {
        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public string AssetPath;
        public string Guid;
        public long LocalId;
        public string SourceTexturePath;
        public string SourceTextureGuid;
        public string OutputPath;
        public Vector2 NormalizedPivot;
        public bool ApproximatePivot;
        public bool HasOffset;
        public bool AlreadyConverted;
        public bool Packed;
        public bool ReuseExistingPng;
        public string ExistingPngGuid;
        public bool Blocked;
    }

    private sealed class ScanProblemItem
    {
        public string AssetPath;
        public string Message;
    }

    private sealed class AtlasInfo
    {
        public string SourceTexturePath;
        public string SourceTextureGuid;
        public int CandidateCount;
    }

    private sealed class AtlasUsage
    {
        public string Path;
        public string Guid;
        public bool CanDelete;
        public List<string> Blockers = new List<string>();
    }

    private sealed class ConversionRun
    {
        public string BackupRoot;
        public ConversionCandidate[] Pending;
        public List<string> Successes;
        public List<string> Failures;
        public List<string> DeletedAtlases;
        public List<string> KeptAtlases;
        public int Index;
        public bool Refreshed;
        public bool Repaired;
        public bool AtlasChecked;
        public int RepairedReferences;
        public int ReusedPng;
    }

    private sealed class SpriteSettings
    {
        public float PixelsPerUnit;
        public Vector4 Border;
        public Vector2 Pivot;
        public FilterMode FilterMode;
        public TextureWrapMode WrapMode;
        public int AnisoLevel;
        public string UserData;
        public string AssetBundleName;
        public string AssetBundleVariant;

        public static SpriteSettings Capture(Sprite sprite, AssetImporter originalImporter)
        {
            return new SpriteSettings
            {
                PixelsPerUnit = sprite.pixelsPerUnit,
                Border = sprite.border,
                Pivot = GetNormalizedPivot(sprite),
                FilterMode = sprite.texture.filterMode,
                WrapMode = sprite.texture.wrapMode,
                AnisoLevel = sprite.texture.anisoLevel,
                UserData = originalImporter == null ? string.Empty : originalImporter.userData,
                AssetBundleName = originalImporter == null ? string.Empty : originalImporter.assetBundleName,
                AssetBundleVariant = originalImporter == null ? string.Empty : originalImporter.assetBundleVariant
            };
        }

        public void Apply(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = PixelsPerUnit;
            importer.spriteBorder = Border;
            var textureSettings = new TextureImporterSettings();
            importer.ReadTextureSettings(textureSettings);
            textureSettings.spriteAlignment = (int)SpriteAlignment.Custom;
            textureSettings.spritePivot = new Vector2(
                NormalizePivotAxis(Pivot.x),
                NormalizePivotAxis(Pivot.y));
            importer.SetTextureSettings(textureSettings);
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.filterMode = FilterMode;
            importer.wrapMode = WrapMode;
            importer.anisoLevel = AnisoLevel;
            importer.userData = UserData;
            importer.assetBundleName = AssetBundleName;
            importer.assetBundleVariant = AssetBundleVariant;
        }
    }

    private sealed class ReferenceEdit
    {
        public string AbsolutePath;
        public string BackupPath;
        public string UpdatedContent;
    }
}
