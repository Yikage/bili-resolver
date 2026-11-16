// VRChat in-world Bilibili search panel for VizVid.
//
// How it works:
//   1. The player types keywords after the prefilled "https://<domain>/search?q=" in a VRC Url Input Field.
//   2. VRCStringDownloader fetches that URL; the worker returns JSON with a slot number and up to 10 results.
//   3. The titles are shown on the result rows.
//   4. Clicking row i plays the pre-baked URL "https://<domain>/p/<slot>/<i>" through VizVid,
//      which syncs it to everyone. Each viewer's client asks the worker for that slot/index and
//      gets the same video.
// Udon cannot turn a string into a VRCUrl at runtime, which is why the URLs are baked into the
// world with the "Generate URL Pool" context-menu command.

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components;
using VRC.SDK3.Data;
using VRC.SDK3.StringLoading;
using VRC.Udon.Common.Interfaces;
using TMPro;
using JLChnToZ.VRC.VVMW;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class BiliSearch : UdonSharpBehaviour
{
    [Header("Worker (must match the worker's SEARCH_SLOTS / SEARCH_RESULTS_PER_SLOT)")]
    [Tooltip("Your worker domain, without a trailing slash")]
    public string baseUrl = "https://bs.lyceta.pp.ua";
    public int slots = 100;
    public int perSlot = 10;

    [Header("UI")]
    public VRCUrlInputField searchField;
    public TextMeshProUGUI statusText;
    [Tooltip("One GameObject per result row (the row's Button). Hidden when empty.")]
    public GameObject[] resultRows;
    [Tooltip("Optional. Leave empty: each row's text is found automatically (the first TextMeshPro text inside the button).")]
    public TextMeshProUGUI[] resultLabels;

    [Header("VizVid")]
    public FrontendHandler frontend;
    [Tooltip("VizVid player backend index, 1-based, in the order listed on VizVid's Core (e.g. 1 = first player)")]
    public byte playerIndex = 1;

    [Tooltip("Filled by the 'Generate URL Pool' context-menu command; do not edit by hand")]
    public VRCUrl[] urlPool;
    [Tooltip("Filled by 'Generate URL Pool': <baseUrl>/search?q= , put into the search field at start")]
    public VRCUrl searchPrefix;

    [Header("Debug")]
    [Tooltip("Show each step under the status text (and always write it to VRChat's output log). Turn off before publishing.")]
    public bool debugMode = true;

    const float RateLimitSeconds = 5.5f; // VRChat allows one string download per ~5 s
    float lastRequestTime = -100f;
    int currentSlot = -1;
    string[] currentTitles;
    string statusMain = "";
    string debugLines = "";
    bool waitingForResponse;
    const float ResponseTimeoutSeconds = 15f;

    void Start()
    {
        currentTitles = new string[perSlot];
        FindLabels();
        // VRChat doesn't keep text typed into a URL field in the editor, so prefill it here.
        if (!VRCUrl.IsNullOrEmpty(searchPrefix)) searchField.SetUrl(searchPrefix);
        ClearResults();
        SetStatus("Type keywords after q= and press Search\n在 q= 后输入关键词，然后点击搜索");
        Dbg($"Start: rows={LengthOf(resultRows)} labels={(resultLabels == null ? 0 : resultLabels.Length)} pool={(urlPool == null ? 0 : urlPool.Length)} prefix={(VRCUrl.IsNullOrEmpty(searchPrefix) ? "MISSING" : searchPrefix.Get())} frontend={(frontend == null ? "MISSING" : "ok")}");
    }

    // Hook this to the Search button and/or the input field's On End Edit.
    public void _Search()
    {
        VRCUrl url = searchField.GetUrl();
        if (VRCUrl.IsNullOrEmpty(url)) { Dbg("Search: field is empty"); RestorePrefix(); return; }

        string prefix = baseUrl + "/search?q=";
        string text = url.Get();
        if (!text.StartsWith(prefix) || text.Length <= prefix.Length)
        {
            SetStatus("Keep the link and type keywords after q=\n请保留前面的链接，在 q= 后输入关键词");
            RestorePrefix();
            return;
        }

        float wait = lastRequestTime + RateLimitSeconds - Time.time;
        if (wait > 0f)
        {
            SetStatus($"Please wait {Mathf.CeilToInt(wait)} s\n请等待 {Mathf.CeilToInt(wait)} 秒");
            return;
        }

        lastRequestTime = Time.time;
        SetStatus("Searching…\n搜索中…");
        Dbg("Request: " + text);
        waitingForResponse = true;
        VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
        SendCustomEventDelayedSeconds("_CheckTimeout", ResponseTimeoutSeconds);
    }

    public void _CheckTimeout()
    {
        if (!waitingForResponse) return;
        if (Time.time - lastRequestTime < ResponseTimeoutSeconds - 0.5f) return; // an older timer from a previous search
        waitingForResponse = false;
        SetStatus($"No response after {(int)ResponseTimeoutSeconds} s\n{(int)ResponseTimeoutSeconds} 秒内没有响应");
        Dbg("Timeout: no success/error callback. Check Allow Untrusted URLs and the output log.");
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        waitingForResponse = false;
        string raw = result.Result;
        Dbg($"Response {(raw == null ? 0 : raw.Length)} chars: {Preview(raw, 160)}");
        DataToken root;
        if (!VRCJson.TryDeserializeFromJson(result.Result, out root) || root.TokenType != TokenType.DataDictionary)
        {
            ClearResults();
            SetStatus("Could not read the search response\n无法解析搜索结果");
            return;
        }
        DataDictionary data = root.DataDictionary;

        DataToken status;
        if (!data.TryGetValue("status", TokenType.String, out status) || status.String != "success")
        {
            DataToken message;
            string text = "Search failed\n搜索失败";
            if (data.TryGetValue("message", TokenType.String, out message)) text = message.String;
            ClearResults();
            SetStatus(text);
            return;
        }

        DataToken slotToken, listToken;
        if (!data.TryGetValue("slot", TokenType.Double, out slotToken) ||
            !data.TryGetValue("results", TokenType.DataList, out listToken))
        {
            ClearResults();
            SetStatus("Unexpected search response\n搜索结果格式错误");
            return;
        }

        currentSlot = (int)slotToken.Double;
        DataList list = listToken.DataList;
        Dbg($"Parsed: slot={currentSlot} results={list.Count}");
        int shown = 0;
        for (int i = 0; i < LengthOf(resultRows); i++)
        {
            if (resultRows[i] == null) { Dbg($"Result Rows element {i} is empty — skipped"); continue; }
            if (resultLabels == null || i >= resultLabels.Length || resultLabels[i] == null) { Dbg($"Result Labels element {i} is missing — skipped"); resultRows[i].SetActive(false); continue; }
            if (i >= currentTitles.Length) { Dbg($"Row {i} is beyond Per Slot ({perSlot}) — skipped"); resultRows[i].SetActive(false); continue; }
            bool has = i < list.Count && i < perSlot && list[i].TokenType == TokenType.DataDictionary;
            if (has)
            {
                DataDictionary item = list[i].DataDictionary;
                string title = GetString(item, "title");
                string author = GetString(item, "author");
                string duration = GetString(item, "duration");
                currentTitles[i] = title;
                // <noparse> keeps characters like '<' in titles from being read as rich text.
                resultLabels[i].text = $"<noparse>{title}</noparse>\n<size=70%><noparse>{author}</noparse>  {duration}</size>";
                shown++;
            }
            else
            {
                currentTitles[i] = "";
            }
            resultRows[i].SetActive(has);
        }
        SetStatus($"{shown} results — click one to play\n共 {shown} 条结果，点击播放");
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        waitingForResponse = false;
        Dbg($"Error {result.ErrorCode}: {result.Error}");
        ClearResults();
        SetStatus($"Request failed ({result.ErrorCode}): {result.Error}\nCheck that Allow Untrusted URLs is on\n请求失败，请确认已开启 Allow Untrusted URLs");
    }

    public void _Play0() { Play(0); }
    public void _Play1() { Play(1); }
    public void _Play2() { Play(2); }
    public void _Play3() { Play(3); }
    public void _Play4() { Play(4); }
    public void _Play5() { Play(5); }
    public void _Play6() { Play(6); }
    public void _Play7() { Play(7); }
    public void _Play8() { Play(8); }
    public void _Play9() { Play(9); }

    void Play(int index)
    {
        if (currentSlot < 0 || index >= perSlot) return;
        int poolIndex = currentSlot * perSlot + index;
        if (urlPool == null || poolIndex >= urlPool.Length)
        {
            SetStatus("URL pool not generated — run 'Generate URL Pool' in the editor");
            return;
        }
        string title = currentTitles[index];
        Dbg($"Play: index={index} pool[{poolIndex}]={(VRCUrl.IsNullOrEmpty(urlPool[poolIndex]) ? "EMPTY" : urlPool[poolIndex].Get())} player={playerIndex}");
        if (frontend == null) { SetStatus("Frontend (VizVid FrontendHandler) is not assigned"); return; }
        frontend.PlayUrl(urlPool[poolIndex], null, title, playerIndex);
        SetStatus($"Playing / 播放: {title}");
    }

    // Fill any missing label from the TextMeshPro text inside the matching row (button).
    void FindLabels()
    {
        int rows = LengthOf(resultRows);
        if (resultLabels == null || resultLabels.Length != rows)
        {
            TextMeshProUGUI[] labels = new TextMeshProUGUI[rows];
            for (int i = 0; i < rows && resultLabels != null && i < resultLabels.Length; i++) labels[i] = resultLabels[i];
            resultLabels = labels;
        }
        for (int i = 0; i < rows; i++)
        {
            if (resultLabels[i] == null && resultRows[i] != null)
                resultLabels[i] = resultRows[i].GetComponentInChildren<TextMeshProUGUI>(true);
        }
    }

    // Put "<baseUrl>/search?q=" back into the field (e.g. after the link part was deleted).
    void RestorePrefix()
    {
        if (!VRCUrl.IsNullOrEmpty(searchPrefix)) searchField.SetUrl(searchPrefix);
    }

    void ClearResults()
    {
        currentSlot = -1;
        for (int i = 0; i < LengthOf(resultRows); i++)
            if (resultRows[i] != null) resultRows[i].SetActive(false);
    }

    void SetStatus(string text)
    {
        statusMain = text;
        RefreshStatus();
    }

    void RefreshStatus()
    {
        if (statusText == null) return;
        if (debugMode && debugLines.Length > 0)
            statusText.text = statusMain + "\n<size=55%><color=#9aa0a6><noparse>" + debugLines + "</noparse></color></size>";
        else
            statusText.text = statusMain;
    }

    // Debug line: always goes to VRChat's output log; shown under the status when Debug Mode is on.
    void Dbg(string line)
    {
        Debug.Log("[BiliSearch] " + line);
        debugLines = debugLines.Length == 0 ? line : debugLines + "\n" + line;
        // Keep only the last 6 lines.
        int count = 1;
        for (int i = 0; i < debugLines.Length; i++) if (debugLines[i] == '\n') count++;
        while (count > 6)
        {
            debugLines = debugLines.Substring(debugLines.IndexOf('\n') + 1);
            count--;
        }
        RefreshStatus();
    }

    string Preview(string text, int max)
    {
        if (text == null) return "(null)";
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }

    int LengthOf(GameObject[] array)
    {
        return array == null ? 0 : array.Length;
    }

    string GetString(DataDictionary dict, string key)
    {
        DataToken token;
        if (dict.TryGetValue(key, TokenType.String, out token)) return token.String;
        return "";
    }

#if !COMPILER_UDONSHARP && UNITY_EDITOR
    // Right-click the component header (or its ⋮ menu) → "Generate URL Pool".
    // Run it again whenever you change baseUrl, slots or perSlot.
    [ContextMenu("Generate URL Pool")]
    void GenerateUrlPool()
    {
        UnityEditor.Undo.RecordObject(this, "Generate URL Pool");
        urlPool = new VRCUrl[slots * perSlot];
        for (int s = 0; s < slots; s++)
            for (int i = 0; i < perSlot; i++)
                urlPool[s * perSlot + i] = new VRCUrl($"{baseUrl}/p/{s}/{i}");
        searchPrefix = new VRCUrl($"{baseUrl}/search?q=");
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log($"[BiliSearch] Generated {urlPool.Length} URLs from {baseUrl}/p/0/0 to {baseUrl}/p/{slots - 1}/{perSlot - 1}");
    }
#endif
}
