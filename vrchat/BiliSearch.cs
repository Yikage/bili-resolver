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
    [Tooltip("The title text on each row, same order as Result Rows")]
    public TextMeshProUGUI[] resultLabels;

    [Header("VizVid")]
    public FrontendHandler frontend;
    [Tooltip("VizVid player backend index, 1-based, in the order listed on VizVid's Core (e.g. 1 = first player)")]
    public byte playerIndex = 1;

    [Tooltip("Filled by the 'Generate URL Pool' context-menu command; do not edit by hand")]
    public VRCUrl[] urlPool;

    const float RateLimitSeconds = 5.5f; // VRChat allows one string download per ~5 s
    float lastRequestTime = -100f;
    int currentSlot = -1;
    string[] currentTitles;

    void Start()
    {
        currentTitles = new string[perSlot];
        ClearResults();
        SetStatus("Type keywords after q= and press Search\n在 q= 后输入关键词，然后点击搜索");
    }

    // Hook this to the Search button and/or the input field's On End Edit.
    public void _Search()
    {
        VRCUrl url = searchField.GetUrl();
        if (VRCUrl.IsNullOrEmpty(url)) return;

        string prefix = baseUrl + "/search?q=";
        string text = url.Get();
        if (!text.StartsWith(prefix) || text.Length <= prefix.Length)
        {
            SetStatus("Keep the link and type keywords after q=\n请保留前面的链接，在 q= 后输入关键词");
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
        VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
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
        int shown = 0;
        for (int i = 0; i < resultRows.Length; i++)
        {
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
        frontend.PlayUrl(urlPool[poolIndex], null, title, playerIndex);
        SetStatus($"Playing / 播放: {title}");
    }

    void ClearResults()
    {
        currentSlot = -1;
        for (int i = 0; i < resultRows.Length; i++) resultRows[i].SetActive(false);
    }

    void SetStatus(string text)
    {
        if (statusText != null) statusText.text = text;
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
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log($"[BiliSearch] Generated {urlPool.Length} URLs from {baseUrl}/p/0/0 to {baseUrl}/p/{slots - 1}/{perSlot - 1}");
    }
#endif
}
