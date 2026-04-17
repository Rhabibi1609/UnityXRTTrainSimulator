using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class BypassCertificate : CertificateHandler
{
    protected override bool ValidateCertificate(byte[] certificateData) => true;
}

[Serializable]
public class ChatResponseWrapper
{
    public string response;
    public SourceEntry[] sources;
}

[Serializable]
public class SourceEntry
{
    public int id;
    public int page;
}

[Serializable]
public class SourceArrayHelper
{
    public SourceEntry[] items;
}

public class VRVoiceHandler : MonoBehaviour
{
    // ─── Inspector ───────────────────────────────────────────────────
    [Header("Network Settings")]
    public string serverUrl = "https://cutrdnt.ddns.net";
    [Tooltip("Set your backend API key in the Inspector — do NOT hardcode here")]
    public string apiKey = "";
    [Tooltip("Set your Deepgram API key in the Inspector — do NOT hardcode here")]
    public string deepgramApiKey = "";

    [Header("Input")]
    public InputActionProperty recordButton;
    public InputActionProperty nextButton;
    public InputActionProperty prevButton;

    [Header("UI")]
    public TextMeshProUGUI subtitleDisplay;
    public TextMeshProUGUI pageIndicator;
    public GameObject subtitlePanel;

    [Header("Audio")]
    public AudioSource radioSpeaker;

    [Header("Wake Word")]
    [Tooltip("Enable hands-free 'Hey Agent' voice activation")]
    public bool enableWakeWord = true;
    [Tooltip("Reference to the WakeWordDetector component (local ONNX detection)")]
    public WakeWordDetector wakeWordDetector;

    // ─── Private state ───────────────────────────────────────────────
    private AudioClip micClip;
    private string micDevice;
    private bool isRecording = false;
    private int lastKnownPosition = 0;
    private bool isProcessing = false;

    private string[] currentLines;
    private int currentLineIndex = -1;
    private bool isSpeaking = false;
    private bool linesActive = false;

    private bool nextWasPressedLastFrame = false;
    private bool prevWasPressedLastFrame = false;

    // Cached audio clips for each line (enables instant replay on back-nav)
    private AudioClip[] cachedAudioClips;

    // Tracks which lines are currently being prefetched (to avoid duplicate requests)
    private bool[] isFetchingLine;

    // Active speech coroutine reference (for cancellation on skip)
    private Coroutine activeSpeechCoroutine;

    // Wake recording state (hands-free auto-record after wake word)
    private bool wakeRecordingActive = false;

    // Deepgram API endpoints
    private const string DEEPGRAM_STT_URL =
        "https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true&punctuate=true";
    private const string DEEPGRAM_TTS_URL =
        "https://api.deepgram.com/v1/speak?model=aura-2-thalia-en&encoding=linear16&container=wav&sample_rate=24000";

    private string VraiUrl => serverUrl.TrimEnd('/') + "/vrai";

    // ─── Lifecycle ───────────────────────────────────────────────────
    void Start()
    {
        ClearUI();
        StartCoroutine(InitMicrophoneAndStart());
    }

    /// <summary>
    /// Handles Android runtime permission request, then initializes mic and wake word.
    /// </summary>
    IEnumerator InitMicrophoneAndStart()
    {
#if UNITY_ANDROID
        // Android/Quest requires runtime permission for microphone
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Debug.Log("[VR] Requesting microphone permission...");
            Permission.RequestUserPermission(Permission.Microphone);

            // Wait for the permission dialog to resolve
            float timeout = 10f;
            while (!Permission.HasUserAuthorizedPermission(Permission.Microphone) && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Debug.LogError("[VR] Microphone permission DENIED. Voice features disabled.");
                yield break;
            }
            Debug.Log("[VR] Microphone permission GRANTED.");

            // Wait a frame for device list to populate after permission grant
            yield return null;
        }
#endif

        if (Microphone.devices.Length > 0)
        {
            micDevice = Microphone.devices[0];
            Debug.Log($"[VR] Mic device: {micDevice}");
        }
        else
        {
            Debug.LogError("[VR] No microphone found!");
            yield break;
        }

        StartCoroutine(WarmUpConnection());

        // Start wake word listener if enabled
        if (enableWakeWord)
            StartWakeWordListening();
    }

    void OnDisable()
    {
        StopWakeWordListening();
    }

    IEnumerator WarmUpConnection()
    {
        using (UnityWebRequest www = UnityWebRequest.Get(serverUrl.TrimEnd('/') + "/"))
        {
            www.timeout = 15;
            yield return www.SendWebRequest();
            Debug.Log($"[VR] Server warmup: {www.responseCode} ({www.result})");
        }
    }

    void Update()
    {
        // ── Record button (grip / space) ──
        bool vrGrip = recordButton.action != null && recordButton.action.ReadValue<float>() > 0.5f;
        bool kbSpace = Keyboard.current != null && Keyboard.current.spaceKey.isPressed;
        bool recordPressed = vrGrip || kbSpace;

        if (recordPressed && !isRecording && !isProcessing && !linesActive)
            StartRecording();

        if (isRecording && micDevice != null)
        {
            int pos = Microphone.GetPosition(micDevice);
            if (pos > 0) lastKnownPosition = pos;
        }

        if (!recordPressed && isRecording && !wakeRecordingActive)
            StopAndProcess();

        // ── Next button (A / Enter) — works even while speaking ──
        bool vrA = nextButton.action != null && nextButton.action.ReadValue<float>() > 0.5f;
        bool kbEnter = Keyboard.current != null && Keyboard.current.enterKey.isPressed;
        bool nextPressed = vrA || kbEnter;

        if (nextPressed && !nextWasPressedLastFrame && linesActive)
            SkipToLine(currentLineIndex + 1);

        nextWasPressedLastFrame = nextPressed;

        // ── Previous button (B / Backspace) — works even while speaking ──
        bool vrB = prevButton.action != null && prevButton.action.ReadValue<float>() > 0.5f;
        bool kbBack = Keyboard.current != null && Keyboard.current.backspaceKey.isPressed;
        bool prevPressed = vrB || kbBack;

        if (prevPressed && !prevWasPressedLastFrame && linesActive)
            SkipToLine(currentLineIndex - 1);

        prevWasPressedLastFrame = prevPressed;
    }

    // ─── Recording ───────────────────────────────────────────────────
    void StartRecording()
    {
        if (micDevice == null) return;

        // Pause wake word listener while manually recording
        StopWakeWordListening();

        isRecording = true;
        lastKnownPosition = 0;

        if (radioSpeaker != null && radioSpeaker.isPlaying)
            radioSpeaker.Stop();
        isSpeaking = false;

        micClip = Microphone.Start(micDevice, false, 10, 16000);
        if (subtitlePanel != null) subtitlePanel.SetActive(true);
        SetSubtitle("🎙️ Listening...");
        SetPageIndicator("");
    }

    void StopAndProcess()
    {
        int lastPos = Microphone.GetPosition(micDevice);
        if (lastPos <= 0) lastPos = lastKnownPosition;

        Microphone.End(micDevice);
        isRecording = false;

        if (lastPos <= 0)
        {
            SetSubtitle("No audio captured. Try again.");
            return;
        }

        byte[] wavData = WavUtility.FromAudioClip(micClip, lastPos);
        Destroy(micClip);
        micClip = null;

        StartCoroutine(ProcessVoiceQuery(wavData));
        if (subtitlePanel != null) subtitlePanel.SetActive(true);
        SetSubtitle("⏳ Transcribing...");
        SetPageIndicator("");
    }

    // ─── Full pipeline ───────────────────────────────────────────────
    IEnumerator ProcessVoiceQuery(byte[] wavData)
    {
        isProcessing = true;

        // STEP 1: Deepgram STT
        string transcribedText = null;
        yield return StartCoroutine(CallDeepgramSTT(wavData, result => transcribedText = result));

        if (string.IsNullOrEmpty(transcribedText))
        {
            SetSubtitle("Could not understand audio. Try again.");
            isProcessing = false;
            yield break;
        }

        Debug.Log($"[VR] Transcribed: {transcribedText}");
        SetSubtitle($"You: \"{transcribedText}\"\n⏳ Thinking...");

        // STEP 2: Send text to /vrai
        ChatResponseWrapper chatResponse = null;
        yield return StartCoroutine(CallVraiEndpoint(transcribedText, result => chatResponse = result));

        if (chatResponse == null || string.IsNullOrEmpty(chatResponse.response))
        {
            SetSubtitle("Error: no response from server.");
            isProcessing = false;
            yield break;
        }

        string fullText = chatResponse.response;
        Debug.Log($"[VR] AI Response: {fullText}");

        // STEP 3: Split response into lines locally
        List<string> allLines = new List<string>(SplitIntoLines(fullText, 120));

        // STEP 4: Append first 3 source pages as a final line
        if (chatResponse.sources != null && chatResponse.sources.Length > 0)
        {
            int count = Mathf.Min(chatResponse.sources.Length, 3);
            StringBuilder sb = new StringBuilder("📖 Sources: ");
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"Page {chatResponse.sources[i].page}");
            }
            allLines.Add(sb.ToString());
        }

        // STEP 5: Begin line cycling (TTS via Deepgram)
        BeginLineCycling(allLines.ToArray());

        isProcessing = false;

        // Restart wake word after processing completes (it restarts again after FinishCycling too)
    }

    // ─── Deepgram STT ────────────────────────────────────────────────
    IEnumerator CallDeepgramSTT(byte[] wavData, Action<string> onResult)
    {
        using (UnityWebRequest www = new UnityWebRequest(DEEPGRAM_STT_URL, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(wavData);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Authorization", $"Token {deepgramApiKey}");
            www.SetRequestHeader("Content-Type", "audio/wav");
            www.timeout = 30;

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                string json = www.downloadHandler.text;
                Debug.Log($"[VR] Deepgram STT response: {json}");
                string transcript = ExtractDeepgramTranscript(json);
                onResult?.Invoke(transcript);
            }
            else
            {
                Debug.LogError($"[VR] Deepgram STT error: {www.error}");
                onResult?.Invoke(null);
            }
        }
    }

    /// <summary>
    /// Extracts the transcript from Deepgram's pre-recorded STT JSON response.
    /// Path: results.channels[0].alternatives[0].transcript
    /// </summary>
    string ExtractDeepgramTranscript(string json)
    {
        // Navigate to "transcript" inside the nested structure.
        // We look for the first occurrence of "transcript":"..." which is the
        // top-level alternative transcript (channels[0].alternatives[0].transcript).
        int idx = json.IndexOf("\"transcript\"");
        if (idx < 0) return null;

        int colonIdx = json.IndexOf(':', idx + 12);
        if (colonIdx < 0) return null;

        int cursor = colonIdx + 1;
        while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;

        if (cursor < json.Length && json[cursor] == '"')
        {
            string val = ExtractJsonString(json, cursor);
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        return null;
    }

    // ─── VRAI endpoint ───────────────────────────────────────────────
    IEnumerator CallVraiEndpoint(string text, Action<ChatResponseWrapper> onResult)
    {
        string jsonBody = "{\"text\":\"" + EscapeJson(text) + "\"}";
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);

        using (UnityWebRequest www = new UnityWebRequest(VraiUrl, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("x-api-key", apiKey);
            www.timeout = 60;

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                string json = www.downloadHandler.text;
                Debug.Log($"[VR] VRAI JSON: {json}");
                ChatResponseWrapper resp = ParseChatResponse(json);
                onResult?.Invoke(resp);
            }
            else
            {
                Debug.LogError($"[VR] VRAI error: {www.error}");
                onResult?.Invoke(null);
            }
        }
    }

    /// <summary>
    /// Parses the /vrai JSON response, handling both formats:
    ///   - "response": "plain string"                      (older Gemini)
    ///   - "response": [{"type":"text","text":"..."}]       (newer Gemini 3.1+)
    /// </summary>
    ChatResponseWrapper ParseChatResponse(string json)
    {
        ChatResponseWrapper wrapper = new ChatResponseWrapper();

        // --- Extract "response" (could be a string or an array) ---
        int respIdx = json.IndexOf("\"response\"");
        if (respIdx >= 0)
        {
            int colonIdx = json.IndexOf(':', respIdx + 10);
            if (colonIdx >= 0)
            {
                // Skip whitespace after the colon
                int cursor = colonIdx + 1;
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;

                if (cursor < json.Length && json[cursor] == '[')
                {
                    // response is an ARRAY of content parts — extract "text" fields
                    int bracketEnd = FindMatchingBracket(json, cursor);
                    string arrayStr = json.Substring(cursor, bracketEnd - cursor + 1);
                    wrapper.response = ExtractTextFromContentParts(arrayStr);
                }
                else if (cursor < json.Length && json[cursor] == '"')
                {
                    // response is a plain string
                    wrapper.response = ExtractJsonString(json, cursor);
                }
            }
        }

        // --- Extract "sources" array using JsonUtility on a trimmed sub-object ---
        int srcIdx = json.IndexOf("\"sources\"");
        if (srcIdx >= 0)
        {
            int srcColon = json.IndexOf(':', srcIdx + 9);
            if (srcColon >= 0)
            {
                int srcCursor = srcColon + 1;
                while (srcCursor < json.Length && char.IsWhiteSpace(json[srcCursor])) srcCursor++;

                if (srcCursor < json.Length && json[srcCursor] == '[')
                {
                    int srcEnd = FindMatchingBracket(json, srcCursor);
                    string srcArray = json.Substring(srcCursor, srcEnd - srcCursor + 1);
                    // Wrap in an object so JsonUtility can parse it
                    string srcJson = "{\"items\":" + srcArray + "}";
                    SourceArrayHelper helper = JsonUtility.FromJson<SourceArrayHelper>(srcJson);
                    wrapper.sources = helper != null ? helper.items : null;
                }
            }
        }

        Debug.Log($"[VR] Parsed response: {wrapper.response?.Substring(0, Mathf.Min(wrapper.response?.Length ?? 0, 80))}");
        return wrapper;
    }

    /// <summary>
    /// Given a JSON array string like [{"type":"text","text":"Hello"}, ...],
    /// extracts and joins all "text" KEY values (skipping "type":"text" values).
    /// </summary>
    string ExtractTextFromContentParts(string arrayJson)
    {
        StringBuilder sb = new StringBuilder();
        int searchFrom = 0;

        while (true)
        {
            int textKeyIdx = arrayJson.IndexOf("\"text\"", searchFrom);
            if (textKeyIdx < 0) break;

            // Verify this "text" is a JSON KEY by checking that a colon follows
            // immediately (with optional whitespace). If not, it's a VALUE
            // (e.g. the value in "type":"text") and should be skipped.
            int afterQuote = textKeyIdx + 6; // position right after closing "
            int peek = afterQuote;
            while (peek < arrayJson.Length && char.IsWhiteSpace(arrayJson[peek])) peek++;

            if (peek >= arrayJson.Length || arrayJson[peek] != ':')
            {
                // No colon after "text" — this is a value, not a key. Skip.
                searchFrom = afterQuote;
                continue;
            }

            // It's a key — extract the string value after the colon
            int valStart = peek + 1;
            while (valStart < arrayJson.Length && char.IsWhiteSpace(arrayJson[valStart])) valStart++;

            if (valStart < arrayJson.Length && arrayJson[valStart] == '"')
            {
                string val = ExtractJsonString(arrayJson, valStart);
                if (!string.IsNullOrEmpty(val))
                {
                    if (sb.Length > 0) sb.Append(" ");
                    sb.Append(val);
                }
            }

            searchFrom = afterQuote;
        }

        return sb.ToString();
    }

    /// <summary>Extracts a JSON string value starting at the opening quote.</summary>
    string ExtractJsonString(string json, int openQuote)
    {
        StringBuilder sb = new StringBuilder();
        int i = openQuote + 1;
        while (i < json.Length)
        {
            if (json[i] == '\\' && i + 1 < json.Length)
            {
                char next = json[i + 1];
                if (next == '"') sb.Append('"');
                else if (next == '\\') sb.Append('\\');
                else if (next == 'n') sb.Append('\n');
                else if (next == 'r') sb.Append('\r');
                else if (next == 't') sb.Append('\t');
                else { sb.Append('\\'); sb.Append(next); }
                i += 2;
            }
            else if (json[i] == '"')
            {
                break;
            }
            else
            {
                sb.Append(json[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Finds the matching ] or } for an opening [ or {.</summary>
    int FindMatchingBracket(string json, int openPos)
    {
        char open = json[openPos];
        char close = open == '[' ? ']' : '}';
        int depth = 1;
        bool inString = false;

        for (int i = openPos + 1; i < json.Length; i++)
        {
            if (json[i] == '\\' && inString) { i++; continue; }
            if (json[i] == '"') inString = !inString;
            if (!inString)
            {
                if (json[i] == open) depth++;
                else if (json[i] == close) { depth--; if (depth == 0) return i; }
            }
        }
        return json.Length - 1;
    }

    // ─── Line splitting (moved from server to client) ────────────────
    static string[] SplitIntoLines(string text, int maxChars = 120)
    {
        string[] sentences = Regex.Split(text.Trim(), @"(?<=[.!?])\s+");
        List<string> lines = new List<string>();
        string current = "";

        foreach (string sentence in sentences)
        {
            if (string.IsNullOrEmpty(sentence)) continue;

            string candidate = string.IsNullOrEmpty(current)
                ? sentence
                : current + " " + sentence;

            if (candidate.Length <= maxChars)
            {
                current = candidate;
            }
            else
            {
                if (!string.IsNullOrEmpty(current))
                    lines.Add(current);
                current = sentence;
            }
        }
        if (!string.IsNullOrEmpty(current))
            lines.Add(current);

        return lines.Count > 0 ? lines.ToArray() : new string[] { text.Trim() };
    }

    // ─── Text Cycling with Bi-directional Navigation ─────────────────
    void BeginLineCycling(string[] lines)
    {
        currentLines = lines;
        currentLineIndex = 0;
        linesActive = true;

        // Allocate cache array for audio clips
        cachedAudioClips = new AudioClip[lines.Length];
        isFetchingLine = new bool[lines.Length];

        // Pre-fetch ALL lines' TTS audio in parallel (eliminates wait on advance)
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("📖")) // Skip source lines
                StartCoroutine(PrefetchTTSForLine(i, lines[i]));
        }

        if (subtitlePanel != null) subtitlePanel.SetActive(true);
        ShowAndSpeakCurrentLine();
    }

    /// <summary>
    /// Background coroutine: fetches TTS audio for a specific line index
    /// and stores it in the cache. Does NOT play the audio.
    /// </summary>
    IEnumerator PrefetchTTSForLine(int lineIndex, string text)
    {
        isFetchingLine[lineIndex] = true;

        string jsonBody = "{\"text\":\"" + EscapeJson(text) + "\"}";
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);

        using (UnityWebRequest www = new UnityWebRequest(DEEPGRAM_TTS_URL, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Authorization", $"Token {deepgramApiKey}");
            www.SetRequestHeader("Content-Type", "application/json");
            www.timeout = 30;

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                byte[] wavBytes = www.downloadHandler.data;
                AudioClip clip = WavUtility.ToAudioClip(wavBytes);
                if (clip != null && cachedAudioClips != null)
                {
                    cachedAudioClips[lineIndex] = clip;
                    Debug.Log($"[VR] Prefetched TTS for line {lineIndex} ({wavBytes.Length} bytes)");
                }
            }
            else
            {
                Debug.LogWarning($"[VR] Prefetch TTS failed for line {lineIndex}: {www.error}");
            }
        }

        if (isFetchingLine != null)
            isFetchingLine[lineIndex] = false;
    }

    /// <summary>
    /// Skip to any line index, interrupting current audio if needed.
    /// Works even while audio is still playing.
    /// </summary>
    void SkipToLine(int targetIndex)
    {
        if (targetIndex < 0) return;                    // Already at first line
        if (targetIndex >= currentLines.Length)          // Past last line
        {
            InterruptCurrentSpeech();
            FinishCycling();
            return;
        }

        InterruptCurrentSpeech();
        currentLineIndex = targetIndex;
        ShowAndSpeakCurrentLine();
    }

    /// <summary>
    /// Stops audio playback and cancels the active TTS coroutine so a new
    /// line can begin immediately.
    /// </summary>
    void InterruptCurrentSpeech()
    {
        if (activeSpeechCoroutine != null)
        {
            StopCoroutine(activeSpeechCoroutine);
            activeSpeechCoroutine = null;
        }

        if (radioSpeaker != null && radioSpeaker.isPlaying)
            radioSpeaker.Stop();

        isSpeaking = false;
    }

    void ShowAndSpeakCurrentLine()
    {
        string line = currentLines[currentLineIndex];
        int total = currentLines.Length;
        int display = currentLineIndex + 1;

        SetSubtitle(line);
        SetPageIndicator($"{display} / {total}  [Speaking...]");

        isSpeaking = true;

        // Sources line (last line starting with 📖) — don't TTS it, just display
        if (line.StartsWith("📖"))
        {
            activeSpeechCoroutine = StartCoroutine(PauseThenDone(1.5f));
        }
        else
        {
            activeSpeechCoroutine = StartCoroutine(FetchAndPlayTTS(line));
        }
    }

    IEnumerator PauseThenDone(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        HandleSpeechDone();
    }

    // ─── TTS — Deepgram Aura ─────────────────────────────────────────
    IEnumerator FetchAndPlayTTS(string text)
    {
        int playIndex = currentLineIndex; // Capture index at call time

        // Wait for prefetch to finish if it's still in-flight
        if (cachedAudioClips != null && cachedAudioClips[playIndex] == null
            && isFetchingLine != null && isFetchingLine[playIndex])
        {
            Debug.Log($"[VR] Waiting for prefetch of line {playIndex}...");
            SetPageIndicator($"{playIndex + 1} / {currentLines.Length}  [Loading...]");
            yield return new WaitUntil(() =>
                cachedAudioClips == null ||
                cachedAudioClips[playIndex] != null ||
                (isFetchingLine != null && !isFetchingLine[playIndex]));
        }

        // Play from cache (prefetch should have filled it)
        if (cachedAudioClips != null && cachedAudioClips[playIndex] != null)
        {
            AudioClip cached = cachedAudioClips[playIndex];
            Debug.Log($"[VR] Playing cached audio for line {playIndex}");

            if (radioSpeaker != null)
            {
                radioSpeaker.clip = cached;
                radioSpeaker.Play();
                yield return new WaitWhile(() => radioSpeaker.isPlaying);
                yield return new WaitForSeconds(0.3f);
            }

            HandleSpeechDone();
            yield break;
        }

        // Fallback: prefetch failed — fetch directly
        Debug.LogWarning($"[VR] Prefetch missed for line {playIndex}, fetching directly");
        string jsonBody = "{\"text\":\"" + EscapeJson(text) + "\"}";
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);

        using (UnityWebRequest www = new UnityWebRequest(DEEPGRAM_TTS_URL, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Authorization", $"Token {deepgramApiKey}");
            www.SetRequestHeader("Content-Type", "application/json");
            www.timeout = 30;

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                byte[] wavBytes = www.downloadHandler.data;
                AudioClip ttsClip = WavUtility.ToAudioClip(wavBytes);

                if (ttsClip != null)
                {
                    if (cachedAudioClips != null)
                        cachedAudioClips[playIndex] = ttsClip;

                    if (radioSpeaker != null)
                    {
                        radioSpeaker.clip = ttsClip;
                        radioSpeaker.Play();
                        yield return new WaitWhile(() => radioSpeaker.isPlaying);
                        yield return new WaitForSeconds(0.3f);
                    }
                }
                else
                {
                    yield return StartCoroutine(SimulateSpeech(text));
                }
            }
            else
            {
                Debug.LogError($"[VR] Deepgram TTS error: {www.error}");
                yield return StartCoroutine(SimulateSpeech(text));
            }
        }

        HandleSpeechDone();
    }

    IEnumerator SimulateSpeech(string text)
    {
        float duration = Mathf.Max(1f, text.Split(' ').Length * 0.35f);
        yield return new WaitForSeconds(duration);
    }

    void HandleSpeechDone()
    {
        isSpeaking = false;
        if (linesActive && currentLineIndex < currentLines.Length - 1)
        {
            string nav = currentLineIndex > 0 ? "[B ◀]  " : "";
            SetPageIndicator($"{currentLineIndex + 1} / {currentLines.Length}  {nav}[Press A ▶]");
        }
        else if (linesActive && currentLineIndex > 0)
        {
            SetPageIndicator($"{currentLines.Length} / {currentLines.Length}  [B ◀]  [A to finish]");
        }
        else if (linesActive)
        {
            SetPageIndicator($"{currentLines.Length} / {currentLines.Length}  [Press A to finish]");
        }
    }

    void FinishCycling()
    {
        linesActive = false;

        // Destroy all cached audio clips to free memory
        if (cachedAudioClips != null)
        {
            for (int i = 0; i < cachedAudioClips.Length; i++)
            {
                if (cachedAudioClips[i] != null)
                {
                    Destroy(cachedAudioClips[i]);
                    cachedAudioClips[i] = null;
                }
            }
            cachedAudioClips = null;
        }

        isFetchingLine = null;
        currentLines = null;
        currentLineIndex = -1;

        if (radioSpeaker != null && radioSpeaker.isPlaying) radioSpeaker.Stop();
        if (subtitlePanel != null) subtitlePanel.SetActive(false);
        SetSubtitle("");
        SetPageIndicator("");

        Debug.Log("[VR] Session finished — all cached audio and text cleared.");

        // Restart wake word listener after session ends
        if (enableWakeWord && micDevice != null)
            StartWakeWordListening();
    }

    // ─── Wake Word Detection (Local ONNX) ──────────────────────────────
    void StartWakeWordListening()
    {
        if (wakeWordDetector == null || micDevice == null) return;

        wakeWordDetector.OnWakeWordDetected -= OnWakeWordTriggered; // Prevent double-subscribe
        wakeWordDetector.OnWakeWordDetected += OnWakeWordTriggered;
        wakeWordDetector.StartListening(micDevice);

        Debug.Log("[VR] Wake word listening started (local ONNX — zero API calls).");
    }

    void StopWakeWordListening()
    {
        if (wakeWordDetector == null) return;

        wakeWordDetector.OnWakeWordDetected -= OnWakeWordTriggered;
        wakeWordDetector.StopListening();

        Debug.Log("[VR] Wake word listening stopped.");
    }

    void OnWakeWordTriggered()
    {
        Debug.Log("[VR] 🎯 Wake word detected (local ONNX)!");

        // Stop the detector (it will be restarted after the session ends)
        StopWakeWordListening();

        // Show UI and enter recording mode
        if (subtitlePanel != null) subtitlePanel.SetActive(true);
        SetSubtitle("🎙️ I'm listening... ask your question.");
        SetPageIndicator("");

        // Small delay then auto-start recording
        StartCoroutine(AutoRecordAfterWake());
    }

    IEnumerator AutoRecordAfterWake()
    {
        yield return new WaitForSeconds(0.3f);

        // Start recording in hands-free mode
        wakeRecordingActive = true;
        StartRecording();

        Debug.Log("[VR] Auto-recording started (hands-free). Speak your question...");

        // Wait for mic to initialize
        yield return new WaitForSeconds(0.5f);

        // Monitor for speech with silence-based auto-stop
        const float MAX_RECORD_TIME = 8.0f;    // Max recording duration
        const float SILENCE_TIMEOUT = 2.0f;    // Stop after 2s of silence
        const float INITIAL_TIMEOUT = 3.0f;    // If no speech at all within 3s, cancel
        const float ENERGY_THRESHOLD = 0.005f;
        const int ENERGY_WINDOW = 1600;        // 100ms at 16kHz

        float elapsed = 0f;
        float silenceTimer = 0f;
        bool speechDetected = false;

        while (isRecording && elapsed < MAX_RECORD_TIME)
        {
            yield return new WaitForSeconds(0.1f);
            elapsed += 0.1f;

            if (micClip == null) break;

            // Check energy
            int micPos = Microphone.GetPosition(micDevice);
            if (micPos <= 0) continue;

            int windowSize = Mathf.Min(ENERGY_WINDOW, micClip.samples);
            int windowStart = micPos - windowSize;
            if (windowStart < 0) windowStart += micClip.samples;

            float[] window = new float[windowSize];
            micClip.GetData(window, windowStart);

            float rms = 0f;
            for (int i = 0; i < window.Length; i++) rms += window[i] * window[i];
            rms = Mathf.Sqrt(rms / window.Length);

            if (rms >= ENERGY_THRESHOLD)
            {
                speechDetected = true;
                silenceTimer = 0f;
                SetSubtitle("🎙️ Listening...");
            }
            else
            {
                silenceTimer += 0.1f;
            }

            // If no speech detected at all within initial timeout, cancel
            if (!speechDetected && elapsed >= INITIAL_TIMEOUT)
            {
                Debug.Log("[VR] No speech detected within 3s — cancelling auto-record.");
                break;
            }

            // If speech was detected and then silence for SILENCE_TIMEOUT, stop
            if (speechDetected && silenceTimer >= SILENCE_TIMEOUT)
            {
                Debug.Log($"[VR] Speech ended ({silenceTimer:F1}s silence) — processing.");
                break;
            }
        }

        wakeRecordingActive = false;

        if (isRecording)
        {
            if (speechDetected)
            {
                // Process the recording
                StopAndProcess();
            }
            else
            {
                // No speech — clean up and restart wake word
                Microphone.End(micDevice);
                isRecording = false;
                if (micClip != null) { Destroy(micClip); micClip = null; }
                SetSubtitle("No speech detected. Say 'Hey Agent' to try again.");
                yield return new WaitForSeconds(2.0f);
                ClearUI();

                if (enableWakeWord && micDevice != null)
                    StartWakeWordListening();
            }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────
    static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"")
         .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    void SetSubtitle(string text) { if (subtitleDisplay != null) subtitleDisplay.text = text; }
    void SetPageIndicator(string text) { if (pageIndicator != null) pageIndicator.text = text; }
    void ClearUI()
    {
        if (subtitlePanel != null) subtitlePanel.SetActive(false);
        SetSubtitle("");
        SetPageIndicator("");
    }
}