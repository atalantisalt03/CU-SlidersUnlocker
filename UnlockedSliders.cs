using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UnlockedSliders;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class UnlockedSlidersPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "kef.casualtiesunknown.unlockedsliders";
    public const string PluginName = "Unlocked Sliders";
    public const string PluginVersion = "0.2.14";

    internal const float ScanInterval = 0.5f;
    private const float DiagnosticLogInterval = 10f;
    private const string ResetEntryName = "UnlockedSlidersResetEntry";
    private const string OldResetButtonName = "UnlockedSlidersResetButton";

    private static UnlockedSlidersPlugin activePlugin;
    private static bool staticHooksInstalled;

    private readonly Dictionary<string, SliderLimitConfig> limitConfigs = new Dictionary<string, SliderLimitConfig>();
    private readonly List<UnlockedSliderControl> controls = new List<UnlockedSliderControl>();
    private ConfigEntry<bool> diagnosticsEnabled;
    private bool pendingConfigSave;
    private bool loggedFirstUpdate;
    private bool loggedFirstCanvasRender;
    private float nextScanTime;
    private float nextCanvasScanTime;
    private float nextDiagnosticLogTime;
    private string lastDiagnosticState;

    private void Awake()
    {
        diagnosticsEnabled = Config.Bind(
            "Diagnostics",
            "Enabled",
            true,
            "Writes low-volume scan diagnostics to BepInEx/LogOutput.log. Useful when the plugin loads but the UI is not enhanced.");
        activePlugin = this;
        EnsureStaticHooks();
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Diagnostics={diagnosticsEnabled.Value}.");
        Logger.LogInfo($"Runtime: Unity {Application.unityVersion}, product '{Application.productName}', plugin path '{Paths.PluginPath}'.");
        RunScan("awake");
    }

    private void Start()
    {
        Logger.LogInfo("Start callback fired.");
        RunScan("start");
    }

    private void OnDestroy()
    {
        Logger.LogInfo("Plugin component destroyed; static hooks remain active.");
    }

    private static void EnsureStaticHooks()
    {
        if (staticHooksInstalled)
        {
            return;
        }

        staticHooksInstalled = true;
        SceneManager.sceneLoaded += OnSceneLoadedStatic;
        Canvas.willRenderCanvases += OnCanvasWillRenderCanvasesStatic;
    }

    private static void OnSceneLoadedStatic(Scene scene, LoadSceneMode mode)
    {
        activePlugin?.OnSceneLoaded(scene, mode);
    }

    private static void OnCanvasWillRenderCanvasesStatic()
    {
        activePlugin?.OnCanvasWillRenderCanvases();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Logger.LogInfo($"Scene loaded: '{scene.name}' ({mode}); scanning.");
        RunScan("sceneLoaded");
    }

    private void Update()
    {
        if (!loggedFirstUpdate)
        {
            loggedFirstUpdate = true;
            Logger.LogInfo("Update callback fired.");
        }

        if (Time.unscaledTime < nextScanTime)
        {
            return;
        }

        nextScanTime = Time.unscaledTime + ScanInterval;
        RunScan("update");
    }

    private void OnCanvasWillRenderCanvases()
    {
        if (!loggedFirstCanvasRender)
        {
            loggedFirstCanvasRender = true;
            Logger.LogInfo("Canvas render callback fired.");
        }

        if (Time.unscaledTime < nextCanvasScanTime)
        {
            return;
        }

        nextCanvasScanTime = Time.unscaledTime + ScanInterval;
        RunScan("canvasRender");
    }

    internal void RunScan(string source)
    {
        try
        {
            EnhanceVisibleSettings(source);
        }
        catch (Exception exception)
        {
            Logger.LogError($"Unlocked Sliders scan failed during {source}: {exception}");
        }
    }

    internal SliderLimitConfig GetLimitConfig(string label, float originalMin, float originalMax)
    {
        string section = "Slider." + SanitizeKey(label);
        if (!limitConfigs.TryGetValue(section, out SliderLimitConfig sliderConfig))
        {
            sliderConfig = new SliderLimitConfig(
                Config.Bind(section, "Min", originalMin, $"Minimum slider limit for {label}."),
                Config.Bind(section, "Max", originalMax, $"Maximum slider limit for {label}."));
            limitConfigs[section] = sliderConfig;
            pendingConfigSave = true;
        }

        sliderConfig.OriginalMin = originalMin;
        sliderConfig.OriginalMax = originalMax;
        return sliderConfig;
    }

    internal void Register(UnlockedSliderControl control)
    {
        if (!controls.Contains(control))
        {
            controls.Add(control);
        }
    }

    internal void SaveConfig()
    {
        Config.Save();
    }

    private void EnhanceVisibleSettings(string source)
    {
        Transform customSettings = FindCustomSettingsPanel();
        Transform content = FindCustomSettingsContent(customSettings);
        if (customSettings != null)
        {
            RemoveOldHeaderResetButton(customSettings);
            EnsureResetEntry(content);
        }

        for (int i = controls.Count - 1; i >= 0; i--)
        {
            if (controls[i] == null)
            {
                controls.RemoveAt(i);
            }
        }

        Dictionary<string, int> initializationFailures = new Dictionary<string, int>();
        int displaysSeen = 0;
        int floatRowsSeen = 0;
        int newlyInitialized = 0;

        foreach (MonoBehaviour behaviour in FindRunSettingDisplays(content))
        {
            displaysSeen++;
            if (behaviour == null)
            {
                continue;
            }

            GameObject row = behaviour.gameObject;
            if (row == null || !row.name.StartsWith("RunSettingFloat", StringComparison.Ordinal))
            {
                continue;
            }

            floatRowsSeen++;
            UnlockedSliderControl control = row.GetComponent<UnlockedSliderControl>();
            if (control == null)
            {
                control = row.AddComponent<UnlockedSliderControl>();
            }

            bool wasInitialized = control.IsInitialized;
            if (control.TryInitialize(this, behaviour, out string failureReason))
            {
                if (!wasInitialized)
                {
                    newlyInitialized++;
                }
            }
            else if (!string.IsNullOrWhiteSpace(failureReason))
            {
                initializationFailures.TryGetValue(failureReason, out int count);
                initializationFailures[failureReason] = count + 1;
            }
        }

        if (pendingConfigSave)
        {
            Config.Save();
            pendingConfigSave = false;
        }

        LogDiagnostics(source, customSettings, content, displaysSeen, floatRowsSeen, newlyInitialized, initializationFailures);
    }

    private void RemoveOldHeaderResetButton(Transform customSettings)
    {
        Transform oldButton = customSettings.Find(OldResetButtonName);
        if (oldButton != null)
        {
            Destroy(oldButton.gameObject);
        }
    }

    private void EnsureResetEntry(Transform content)
    {
        if (content == null || content.Find(ResetEntryName) != null || content.childCount == 0)
        {
            return;
        }

        RectTransform contentRect = content.GetComponent<RectTransform>();
        RectTransform firstRowRect = content.GetChild(0).GetComponent<RectTransform>();
        Image firstRowImage = content.GetChild(0).GetComponent<Image>();
        TextMeshProUGUI sourceText = content.GetComponentInChildren<TextMeshProUGUI>(true);
        if (contentRect == null || firstRowRect == null)
        {
            return;
        }

        float rowHeight = Mathf.Max(1f, firstRowRect.rect.height);
        Vector2 firstPosition = firstRowRect.anchoredPosition;
        Vector2 firstSize = firstRowRect.sizeDelta;

        GameObject rowObject = new GameObject(ResetEntryName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        rowObject.transform.SetParent(content, false);
        rowObject.layer = content.gameObject.layer;

        RectTransform rowRect = rowObject.GetComponent<RectTransform>();
        rowRect.anchorMin = firstRowRect.anchorMin;
        rowRect.anchorMax = firstRowRect.anchorMax;
        rowRect.pivot = firstRowRect.pivot;
        rowRect.sizeDelta = firstSize;
        rowRect.anchoredPosition = firstPosition;

        Image rowImage = rowObject.GetComponent<Image>();
        rowImage.color = firstRowImage != null ? firstRowImage.color : new Color(0.42f, 0.42f, 0.42f, 1f);
        rowImage.raycastTarget = false;

        ShiftRowsDown(content, rowObject.transform, rowHeight);
        contentRect.sizeDelta = new Vector2(contentRect.sizeDelta.x, contentRect.sizeDelta.y + rowHeight);
        rowObject.transform.SetSiblingIndex(0);

        CreateResetEntryLabel(rowObject.transform, sourceText);
        CreateResetEntryButton(rowObject.transform, sourceText);
    }

    private static void ShiftRowsDown(Transform content, Transform resetEntry, float rowHeight)
    {
        for (int i = 0; i < content.childCount; i++)
        {
            Transform child = content.GetChild(i);
            if (child == resetEntry)
            {
                continue;
            }

            RectTransform rect = child.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, rect.anchoredPosition.y - rowHeight);
            }
        }
    }

    private static void CreateResetEntryLabel(Transform row, TextMeshProUGUI sourceText)
    {
        GameObject labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(row, false);
        labelObject.layer = row.gameObject.layer;

        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(0f, 0.5f);
        labelRect.anchorMax = new Vector2(0f, 0.5f);
        labelRect.pivot = new Vector2(0f, 0.5f);
        labelRect.sizeDelta = new Vector2(300f, 45f);
        labelRect.anchoredPosition = new Vector2(20f, 0f);

        TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
        CopyTextStyle(label, sourceText);
        label.text = "Slider limits";
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.raycastTarget = false;
    }

    private void CreateResetEntryButton(Transform row, TextMeshProUGUI sourceText)
    {
        GameObject buttonObject = new GameObject("ResetButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(Outline));
        buttonObject.transform.SetParent(row, false);
        buttonObject.layer = row.gameObject.layer;

        RectTransform rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0.5f);
        rect.anchorMax = new Vector2(1f, 0.5f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(180f, 48f);
        rect.anchoredPosition = new Vector2(-20f, 0f);

        Image image = buttonObject.GetComponent<Image>();
        image.color = Color.black;
        image.raycastTarget = true;

        Outline outline = buttonObject.GetComponent<Outline>();
        outline.effectColor = Color.white;
        outline.effectDistance = new Vector2(3f, -3f);

        Button button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(ResetAllLimits);

        GameObject labelObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(buttonObject.transform, false);
        labelObject.layer = buttonObject.layer;

        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(8f, 0f);
        labelRect.offsetMax = new Vector2(-8f, 0f);

        TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
        CopyTextStyle(label, sourceText);
        label.text = "Reset limits";
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
    }

    private static void CopyTextStyle(TextMeshProUGUI target, TextMeshProUGUI source)
    {
        if (source != null)
        {
            target.font = source.font;
            target.fontMaterial = source.fontMaterial;
            target.fontSize = source.fontSize;
            target.color = source.color;
        }
        else
        {
            target.color = Color.white;
            target.fontSize = 24f;
        }

        target.enableWordWrapping = false;
        target.overflowMode = TextOverflowModes.Overflow;
    }

    private void ResetAllLimits()
    {
        foreach (SliderLimitConfig sliderConfig in limitConfigs.Values)
        {
            sliderConfig.Min.Value = sliderConfig.OriginalMin;
            sliderConfig.Max.Value = sliderConfig.OriginalMax;
        }

        Config.Save();

        foreach (UnlockedSliderControl control in controls.ToArray())
        {
            if (control != null)
            {
                control.ResetToOriginalLimits();
            }
        }

        Logger.LogInfo("Unlocked slider limits reset to the game's default ranges.");
    }

    private void LogDiagnostics(
        string source,
        Transform customSettings,
        Transform content,
        int displaysSeen,
        int floatRowsSeen,
        int newlyInitialized,
        Dictionary<string, int> initializationFailures)
    {
        if (diagnosticsEnabled == null || !diagnosticsEnabled.Value)
        {
            return;
        }

        string failures = FormatFailureCounts(initializationFailures);
        string state =
            $"source={source}; " +
            $"panel={(customSettings != null ? GetTransformPath(customSettings) : "not found")}; " +
            $"content={(content != null ? GetTransformPath(content) : "not found")}; " +
            $"displays={displaysSeen}; floatRows={floatRowsSeen}; controls={controls.Count}; new={newlyInitialized}; failures={failures}";

        if (state == lastDiagnosticState && Time.unscaledTime < nextDiagnosticLogTime)
        {
            return;
        }

        lastDiagnosticState = state;
        nextDiagnosticLogTime = Time.unscaledTime + DiagnosticLogInterval;
        Logger.LogInfo($"Scan: {state}");
    }

    private static string FormatFailureCounts(Dictionary<string, int> failures)
    {
        if (failures == null || failures.Count == 0)
        {
            return "none";
        }

        List<string> parts = new List<string>();
        foreach (KeyValuePair<string, int> failure in failures)
        {
            parts.Add($"{failure.Key} x{failure.Value}");
        }

        return string.Join(", ", parts.ToArray());
    }

    private static IEnumerable<MonoBehaviour> FindRunSettingDisplays(Transform content)
    {
        if (content != null)
        {
            foreach (MonoBehaviour behaviour in content.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null && behaviour.GetType().Name == "RunSettingDisplay")
                {
                    yield return behaviour;
                }
            }

            yield break;
        }

        foreach (MonoBehaviour behaviour in FindObjectsOfType<MonoBehaviour>())
        {
            if (behaviour != null && behaviour.GetType().Name == "RunSettingDisplay")
            {
                yield return behaviour;
            }
        }
    }

    private static Transform FindCustomSettingsPanel()
    {
        GameObject customSettings = GameObject.Find("Canvas/RunSettings/CustomSettings");
        if (customSettings != null)
        {
            return customSettings.transform;
        }

        foreach (Transform transform in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (transform == null || !IsSceneObject(transform) || transform.name != "CustomSettings")
            {
                continue;
            }

            string path = GetTransformPath(transform);
            if (path.EndsWith("RunSettings/CustomSettings", StringComparison.Ordinal))
            {
                return transform;
            }
        }

        return null;
    }

    private static Transform FindCustomSettingsContent(Transform customSettings)
    {
        Transform content = customSettings?.Find("Scroll/Viewport/Content");
        if (content != null)
        {
            return content;
        }

        GameObject exact = GameObject.Find("Canvas/RunSettings/CustomSettings/Scroll/Viewport/Content");
        if (exact != null)
        {
            return exact.transform;
        }

        foreach (Transform transform in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (transform == null || !IsSceneObject(transform) || transform.name != "Content")
            {
                continue;
            }

            string path = GetTransformPath(transform);
            if (path.EndsWith("RunSettings/CustomSettings/Scroll/Viewport/Content", StringComparison.Ordinal))
            {
                return transform;
            }
        }

        return null;
    }

    private static bool IsSceneObject(Transform transform)
    {
        return transform != null && transform.gameObject.scene.IsValid();
    }

    private static string GetTransformPath(Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

        List<string> names = new List<string>();
        Transform current = transform;
        while (current != null)
        {
            names.Add(current.name);
            current = current.parent;
        }

        names.Reverse();
        return string.Join("/", names.ToArray());
    }

    private static string SanitizeKey(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "Unnamed";
        }

        char[] chars = label.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != ' ' && chars[i] != '_' && chars[i] != '-')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}

internal sealed class SliderLimitConfig
{
    internal SliderLimitConfig(ConfigEntry<float> min, ConfigEntry<float> max)
    {
        Min = min;
        Max = max;
    }

    internal ConfigEntry<float> Min { get; }
    internal ConfigEntry<float> Max { get; }
    internal float OriginalMin { get; set; }
    internal float OriginalMax { get; set; }
}

internal sealed class UnlockedSliderControl : MonoBehaviour
{
    private const float BoxWidth = 72f;
    private const float BoxRightInset = 8f;
    private const float OutlineThickness = 3f;
    private const float FocusGraceSeconds = 0.15f;

    private static readonly Color DisplayColor = new Color(0f, 0f, 0f, 0.001f);
    private static readonly Color EditingColor = new Color(0.43f, 0.43f, 0.43f, 1f);

    private UnlockedSlidersPlugin plugin;
    private SliderLimitConfig limitConfig;
    private Slider slider;
    private TextMeshProUGUI valueText;
    private TMP_InputField inputField;
    private TextMeshProUGUI inputText;
    private GameObject displayBox;
    private GameObject inputBox;
    private GameObject outlineBox;
    private MonoBehaviour runSettingDisplay;
    private object associatedSetting;
    private FieldInfo limitsField;
    private FieldInfo limitsMinField;
    private FieldInfo limitsMaxField;
    private string postfix = string.Empty;
    private string manualDisplayText;
    private float manualDisplayValue;
    private bool wholeNumbers;
    private bool initialized;
    private bool editing;
    private bool committing;
    private bool hasManualDisplayText;
    private float editStartedAt;

    internal bool IsInitialized => initialized;

    internal bool TryInitialize(UnlockedSlidersPlugin owner, MonoBehaviour display, out string failureReason)
    {
        failureReason = string.Empty;
        if (initialized)
        {
            return true;
        }

        slider = transform.Find("Slider")?.GetComponent<Slider>();
        valueText = transform.Find("Value")?.GetComponent<TextMeshProUGUI>();
        TextMeshProUGUI labelText = transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
        if (ReferenceEquals(owner, null))
        {
            failureReason = "missing owner";
            return false;
        }

        if (display == null)
        {
            failureReason = "missing RunSettingDisplay";
            return false;
        }

        if (slider == null)
        {
            failureReason = "missing Slider child";
            return false;
        }

        if (valueText == null)
        {
            failureReason = "missing Value child";
            return false;
        }

        if (labelText == null)
        {
            failureReason = "missing Label child";
            return false;
        }

        plugin = owner;
        runSettingDisplay = display;
        string label = StripRichText(labelText.text).Trim();

        float originalMin = slider.minValue;
        float originalMax = slider.maxValue;
        ReadRunSettingFloatMetadata(display, ref originalMin, ref originalMax);

        limitConfig = plugin.GetLimitConfig(label, originalMin, originalMax);
        ApplyConfiguredLimits();
        BuildDisplayAndEditor();
        SyncValueTextFromSlider();

        plugin.Register(this);
        initialized = true;
        return true;
    }

    internal void ResetToOriginalLimits()
    {
        if (!initialized || slider == null || limitConfig == null)
        {
            return;
        }

        FinishEditing();
        SetNativeLimits(limitConfig.OriginalMin, limitConfig.OriginalMax);

        float clamped = Mathf.Clamp(NormalizeValue(slider.value), slider.minValue, slider.maxValue);
        if (!Mathf.Approximately(slider.value, clamped))
        {
            SetSliderValue(clamped);
        }

        hasManualDisplayText = false;
        RefreshValueText(clamped);
    }

    private void Update()
    {
        if (!editing || committing || inputField == null || inputBox == null || !inputBox.activeSelf)
        {
            SyncValueTextFromSlider();
            return;
        }

        if (!inputField.isFocused && Time.unscaledTime - editStartedAt > FocusGraceSeconds)
        {
            CommitInput(inputField.text);
        }
    }

    private void OnDisable()
    {
        if (valueText != null)
        {
            valueText.enabled = true;
        }
    }

    private void ReadRunSettingFloatMetadata(MonoBehaviour display, ref float originalMin, ref float originalMax)
    {
        associatedSetting = display.GetType()
            .GetField("associated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(display);

        if (associatedSetting == null)
        {
            postfix = ExtractSuffix(valueText.text);
            wholeNumbers = slider.wholeNumbers;
            return;
        }

        Type settingType = associatedSetting.GetType();
        object wholeValue = settingType
            .GetField("wholeNum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(associatedSetting);
        object postfixValue = settingType
            .GetField("postfix", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(associatedSetting);

        postfix = postfixValue as string ?? ExtractSuffix(valueText.text);
        if (wholeValue is bool settingWholeNumbers)
        {
            wholeNumbers = settingWholeNumbers;
            slider.wholeNumbers = settingWholeNumbers;
        }
        else
        {
            wholeNumbers = slider.wholeNumbers;
        }

        limitsField = settingType.GetField("limits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object limits = limitsField?.GetValue(associatedSetting);
        if (limits == null)
        {
            return;
        }

        Type limitsType = limits.GetType();
        limitsMinField = limitsType.GetField("min", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        limitsMaxField = limitsType.GetField("max", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (TryReadFloat(limitsMinField, limits, out float min))
        {
            originalMin = min;
        }

        if (TryReadFloat(limitsMaxField, limits, out float max))
        {
            originalMax = max;
        }
    }

    private void ApplyConfiguredLimits()
    {
        float min = limitConfig.Min.Value;
        float max = limitConfig.Max.Value;
        if (float.IsNaN(min) || float.IsInfinity(min))
        {
            min = limitConfig.OriginalMin;
        }

        if (float.IsNaN(max) || float.IsInfinity(max))
        {
            max = limitConfig.OriginalMax;
        }

        if (max < min)
        {
            min = limitConfig.OriginalMin;
            max = limitConfig.OriginalMax;
        }

        float current = NormalizeValue(slider.value);
        if (current < min)
        {
            min = current;
        }

        if (current > max)
        {
            max = current;
        }

        SetNativeLimits(min, max);
        if (!Mathf.Approximately(limitConfig.Min.Value, min) || !Mathf.Approximately(limitConfig.Max.Value, max))
        {
            limitConfig.Min.Value = min;
            limitConfig.Max.Value = max;
            plugin.SaveConfig();
        }
    }

    private void BuildDisplayAndEditor()
    {
        LayoutValueText();

        displayBox = EnsureBox("UnlockedSliderValueBox", DisplayColor, true);
        Button button = displayBox.GetComponent<Button>();
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(BeginEditing);

        inputBox = EnsureBox("UnlockedSliderInputBox", EditingColor, false);
        inputText = EnsureInputText(inputBox.transform);
        inputField = inputBox.GetComponent<TMP_InputField>();
        inputField.textViewport = inputBox.GetComponent<RectTransform>();
        inputField.textComponent = inputText;
        inputField.targetGraphic = inputBox.GetComponent<Image>();
        inputField.lineType = TMP_InputField.LineType.SingleLine;
        inputField.contentType = TMP_InputField.ContentType.Standard;
        inputField.inputType = TMP_InputField.InputType.Standard;
        inputField.keyboardType = TouchScreenKeyboardType.NumbersAndPunctuation;
        inputField.characterLimit = 18;
        inputField.restoreOriginalTextOnEscape = true;
        inputField.selectionColor = new Color(1f, 1f, 1f, 0.25f);
        inputField.onEndEdit.RemoveAllListeners();
        inputField.onEndEdit.AddListener(CommitInput);

        outlineBox = EnsureOutlineBox();
        PlaceValueLayers();
        FinishEditing();
    }

    private void LayoutValueText()
    {
        RectTransform rect = valueText.rectTransform;
        rect.anchorMin = new Vector2(1f, rect.anchorMin.y);
        rect.anchorMax = new Vector2(1f, rect.anchorMax.y);
        rect.pivot = new Vector2(0.5f, rect.pivot.y);
        rect.sizeDelta = new Vector2(BoxWidth, rect.sizeDelta.y);
        rect.anchoredPosition = new Vector2(-(BoxRightInset + BoxWidth * 0.5f), rect.anchoredPosition.y);

        valueText.alignment = TextAlignmentOptions.Center;
        valueText.enableWordWrapping = false;
        valueText.overflowMode = TextOverflowModes.Overflow;
        valueText.raycastTarget = false;
    }

    private GameObject EnsureBox(string objectName, Color color, bool clickable)
    {
        Transform existing = transform.Find(objectName);
        GameObject box = existing != null ? existing.gameObject : new GameObject(objectName);
        if (existing == null)
        {
            box.transform.SetParent(transform, false);
            box.layer = gameObject.layer;
        }

        RectTransform rect = box.GetComponent<RectTransform>() ?? box.AddComponent<RectTransform>();
        if (box.GetComponent<CanvasRenderer>() == null)
        {
            box.AddComponent<CanvasRenderer>();
        }

        Image image = box.GetComponent<Image>() ?? box.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = true;

        RemoveOutline(box);

        if (clickable)
        {
            Button button = box.GetComponent<Button>() ?? box.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
        }
        else
        {
            if (box.GetComponent<TMP_InputField>() == null)
            {
                box.AddComponent<TMP_InputField>();
            }
        }

        CopyRect(rect, valueText.rectTransform);
        return box;
    }

    private GameObject EnsureOutlineBox()
    {
        const string objectName = "UnlockedSliderValueOutline";
        Transform existing = transform.Find(objectName);
        GameObject box = existing != null ? existing.gameObject : new GameObject(objectName);
        if (existing == null)
        {
            box.transform.SetParent(transform, false);
            box.layer = gameObject.layer;
        }

        RectTransform rect = box.GetComponent<RectTransform>() ?? box.AddComponent<RectTransform>();
        RemoveOutline(box);
        RemoveImage(box);
        CopyRect(rect, valueText.rectTransform);

        EnsureBorderSegment(box.transform, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, OutlineThickness));
        EnsureBorderSegment(box.transform, "Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, OutlineThickness));
        EnsureBorderSegment(box.transform, "Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(OutlineThickness, 0f));
        EnsureBorderSegment(box.transform, "Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(OutlineThickness, 0f));

        box.SetActive(true);
        return box;
    }

    private static void EnsureBorderSegment(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 sizeDelta)
    {
        Transform existing = parent.Find(name);
        GameObject segment = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        if (existing == null)
        {
            segment.transform.SetParent(parent, false);
            segment.layer = parent.gameObject.layer;
        }

        RectTransform rect = segment.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.sizeDelta = sizeDelta;
        rect.anchoredPosition = Vector2.zero;
        rect.localScale = Vector3.one;

        Image image = segment.GetComponent<Image>() ?? segment.AddComponent<Image>();
        image.color = Color.white;
        image.raycastTarget = false;
        segment.SetActive(true);
    }

    private static void RemoveOutline(GameObject box)
    {
        Outline outline = box.GetComponent<Outline>();
        if (outline != null)
        {
            outline.enabled = false;
            Destroy(outline);
        }
    }

    private static void RemoveImage(GameObject box)
    {
        Image image = box.GetComponent<Image>();
        if (image != null)
        {
            image.enabled = false;
            image.raycastTarget = false;
            Destroy(image);
        }
    }

    private TextMeshProUGUI EnsureInputText(Transform parent)
    {
        Transform existing = parent.Find("Text");
        if (existing == null)
        {
            GameObject textObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(parent, false);
            textObject.layer = parent.gameObject.layer;
            existing = textObject.transform;
        }

        RectTransform rect = existing.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(3f, 0f);
        rect.offsetMax = new Vector2(-3f, 0f);

        TextMeshProUGUI text = existing.GetComponent<TextMeshProUGUI>();
        text.font = valueText.font;
        text.fontMaterial = valueText.fontMaterial;
        text.color = valueText.color;
        text.fontSize = valueText.fontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        return text;
    }

    private void PlaceValueLayers()
    {
        Transform label = transform.Find("Label");
        Transform sliderTransform = slider.transform;

        if (label != null)
        {
            label.SetSiblingIndex(0);
        }

        sliderTransform.SetSiblingIndex(1);
        valueText.transform.SetSiblingIndex(2);
        inputBox.transform.SetSiblingIndex(3);
        displayBox.transform.SetSiblingIndex(4);
        outlineBox.transform.SetSiblingIndex(5);
    }

    private static void CopyRect(RectTransform target, RectTransform source)
    {
        target.anchorMin = source.anchorMin;
        target.anchorMax = source.anchorMax;
        target.pivot = source.pivot;
        target.sizeDelta = source.sizeDelta;
        target.anchoredPosition = source.anchoredPosition;
        target.localScale = source.localScale;
    }

    private void BeginEditing()
    {
        if (inputField == null || inputBox == null)
        {
            return;
        }

        editing = true;
        editStartedAt = Time.unscaledTime;

        string numberOnly = ExtractNumericPrefix(valueText.text);
        if (string.IsNullOrWhiteSpace(numberOnly))
        {
            numberOnly = FormatInputNumber(slider.value);
        }

        inputField.SetTextWithoutNotify(numberOnly);
        inputText.text = numberOnly;

        SetDisplayBoxClickable(false);

        valueText.enabled = false;
        inputBox.SetActive(true);
        inputField.ActivateInputField();
        inputField.Select();
    }

    private void CommitInput(string text)
    {
        if (!editing || committing)
        {
            return;
        }

        committing = true;
        try
        {
            bool parsed = TryParseValue(text, out float enteredValue);
            FinishEditing();

            if (parsed)
            {
                ApplyTypedValue(enteredValue, text);
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Unlocked Sliders] Failed to commit slider value: {exception}");
        }
        finally
        {
            FinishEditing();
            committing = false;
        }
    }

    private void FinishEditing()
    {
        editing = false;

        if (inputBox != null)
        {
            inputBox.SetActive(false);
        }

        if (displayBox != null)
        {
            displayBox.SetActive(true);
        }

        SetDisplayBoxClickable(true);

        if (valueText != null)
        {
            valueText.enabled = true;
        }

        if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == inputBox)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }
    }

    private void SetDisplayBoxClickable(bool clickable)
    {
        if (displayBox == null)
        {
            return;
        }

        Image image = displayBox.GetComponent<Image>();
        if (image != null)
        {
            image.raycastTarget = clickable;
        }

        Button button = displayBox.GetComponent<Button>();
        if (button != null)
        {
            button.interactable = clickable;
        }
    }

    private void ApplyTypedValue(float value, string typedText)
    {
        float normalized = NormalizeValue(value);
        ExpandLimitsFor(normalized);

        if (!Mathf.Approximately(slider.value, normalized))
        {
            SetSliderValue(normalized);
        }

        SetManualDisplayText(typedText);
        RefreshValueText(slider.value);
    }

    private void SetSliderValue(float value)
    {
        try
        {
            slider.value = value;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Unlocked Sliders] The game threw while applying a slider value: {exception}");
        }
    }

    private void RefreshValueText(float value)
    {
        if (valueText == null)
        {
            return;
        }

        valueText.text = hasManualDisplayText ? manualDisplayText : FormatDisplayValue(value);
    }

    private void SyncValueTextFromSlider()
    {
        if (editing || slider == null || valueText == null)
        {
            return;
        }

        if (hasManualDisplayText && !Mathf.Approximately(slider.value, manualDisplayValue))
        {
            hasManualDisplayText = false;
        }

        string expectedText = hasManualDisplayText ? manualDisplayText : FormatDisplayValue(slider.value);
        if (valueText.text != expectedText)
        {
            valueText.text = expectedText;
        }
    }

    private string FormatDisplayValue(float value)
    {
        return FormatInputNumber(value) + postfix;
    }

    private void SetManualDisplayText(string typedText)
    {
        if (slider == null)
        {
            return;
        }

        manualDisplayValue = slider.value;
        if (wholeNumbers)
        {
            manualDisplayText = FormatInputNumber(manualDisplayValue) + postfix;
        }
        else
        {
            manualDisplayText = NormalizeTypedNumberText(typedText) + postfix;
        }

        hasManualDisplayText = true;
    }

    private void ExpandLimitsFor(float value)
    {
        float min = slider.minValue;
        float max = slider.maxValue;
        bool changed = false;

        if (value < min)
        {
            min = value;
            changed = true;
        }

        if (value > max)
        {
            max = value;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        SetNativeLimits(min, max);
        limitConfig.Min.Value = min;
        limitConfig.Max.Value = max;
        plugin.SaveConfig();
    }

    private void SetNativeLimits(float min, float max)
    {
        if (max < min)
        {
            max = min;
        }

        WriteAssociatedLimits(min, max);
        slider.minValue = min;
        slider.maxValue = max;
    }

    private void WriteAssociatedLimits(float min, float max)
    {
        if (associatedSetting == null || limitsField == null || limitsMinField == null || limitsMaxField == null)
        {
            return;
        }

        object limits = limitsField.GetValue(associatedSetting);
        if (limits == null)
        {
            return;
        }

        limitsMinField.SetValue(limits, min);
        limitsMaxField.SetValue(limits, max);
        limitsField.SetValue(associatedSetting, limits);
    }

    private float NormalizeValue(float value)
    {
        return wholeNumbers ? Mathf.Round(value) : value;
    }

    private string FormatInputNumber(float value)
    {
        float normalized = NormalizeValue(value);
        if (wholeNumbers)
        {
            return Mathf.RoundToInt(normalized).ToString(CultureInfo.InvariantCulture);
        }

        float absolute = Mathf.Abs(normalized);
        string format = absolute >= 100f ? "0" : absolute >= 10f ? "0.#" : "0.##";
        return normalized.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string NormalizeTypedNumberText(string text)
    {
        string numeric = ExtractNumericPrefix(text).Replace(',', '.');
        if (string.IsNullOrWhiteSpace(numeric))
        {
            return "0";
        }

        if (numeric.IndexOf('.') >= 0)
        {
            numeric = numeric.TrimEnd('0').TrimEnd('.');
        }

        if (numeric == "-0" || numeric == "+0" || numeric == "-")
        {
            return "0";
        }

        return numeric;
    }

    private static string ExtractSuffix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmed = StripRichText(text).Trim();
        string numeric = ExtractNumericPrefix(trimmed);
        return numeric.Length < trimmed.Length ? trimmed.Substring(numeric.Length) : string.Empty;
    }

    private static bool TryReadFloat(FieldInfo field, object target, out float value)
    {
        value = 0f;
        if (field == null || target == null)
        {
            return false;
        }

        object raw = field.GetValue(target);
        if (raw is float floatValue)
        {
            value = floatValue;
            return true;
        }

        if (raw is int intValue)
        {
            value = intValue;
            return true;
        }

        return false;
    }

    private static bool TryParseValue(string text, out float value)
    {
        value = 0f;
        string numeric = ExtractNumericPrefix(text).Replace(',', '.');
        return !string.IsNullOrWhiteSpace(numeric)
            && float.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !float.IsNaN(value)
            && !float.IsInfinity(value);
    }

    private static string ExtractNumericPrefix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmed = StripRichText(text).Trim();
        bool sawDigit = false;
        bool sawDecimal = false;
        int length = 0;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (char.IsDigit(c))
            {
                sawDigit = true;
                length = i + 1;
                continue;
            }

            if ((c == '-' || c == '+') && i == 0)
            {
                length = i + 1;
                continue;
            }

            if ((c == '.' || c == ',') && !sawDecimal)
            {
                sawDecimal = true;
                length = i + 1;
                continue;
            }

            break;
        }

        return sawDigit ? trimmed.Substring(0, length) : string.Empty;
    }

    private static string StripRichText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int start = text.IndexOf('<');
        while (start >= 0)
        {
            int end = text.IndexOf('>', start);
            if (end < 0)
            {
                break;
            }

            text = text.Remove(start, end - start + 1);
            start = text.IndexOf('<');
        }

        return text;
    }
}
