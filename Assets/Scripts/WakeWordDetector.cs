using System;
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

/// <summary>
/// Fully local wake word detector using openWakeWord ONNX models + Unity Sentis.
/// Pipeline: VAD → Collect Full Utterance → Normalize → Mel → Embedding → Classifier
/// 
/// Architecture:
///   IDLE        – lightweight RMS monitoring, zero inference cost
///   COLLECTING  – sound detected, accumulate all audio into a single buffer
///   PROCESSING  – silence timeout hit, normalize entire buffer at once and run pipeline
///                 (matches Python's: audio / np.max(np.abs(audio)))
///
/// Zero API calls — runs entirely on-device.
/// </summary>
public class WakeWordDetector : MonoBehaviour
{
    // ─── State Machine ───────────────────────────────────────────────
    private enum DetectorState { Idle, Collecting, Processing }
    private DetectorState currentState = DetectorState.Idle;

    [Header("ONNX Models (drag .onnx assets here)")]
    [Tooltip("The melspectrogram ONNX model")]
    public ModelAsset melSpectrogramModelAsset;

    [Tooltip("The embedding ONNX model")]
    public ModelAsset embeddingModelAsset;

    [Tooltip("The wake word classifier ONNX model")]
    public ModelAsset classifierModelAsset;

    [Header("Detection Settings")]
    [Tooltip("Probability threshold to trigger wake word detection (0-1)")]
    [Range(0.1f, 0.99f)]
    public float detectionThreshold = 0.5f;

    [Tooltip("Minimum RMS energy to detect voice activity (skip silence)")]
    [Range(0.00001f, 0.05f)]
    public float energyThreshold = 0.0001f;

    [Tooltip("Multiplier applied to the raw microphone volume. For Meta Quest, try 10 - 50.")]
    [Range(1f, 100f)]
    public float volumeMultiplier = 1.0f;

    [Header("Collection Settings")]
    [Tooltip("Seconds of silence after speech before processing the collected audio")]
    [Range(0.5f, 5f)]
    public float silenceTimeout = 1.0f;

    [Tooltip("Maximum seconds of audio to collect (safety cap)")]
    [Range(2f, 10f)]
    public float maxCollectionDuration = 2.7f;

    [Header("Debugging")]
    [Tooltip("Enable to see real-time state transitions, RMS, and probability in ADB logcat")]
    public bool debugLogging = false;

    /// <summary>Fired when the wake word is detected.</summary>
    public event Action OnWakeWordDetected;

    // ─── Inference workers ───────────────────────────────────────────
    private Worker melWorker;
    private Worker embeddingWorker;
    private Worker classifierWorker;

    // ─── Audio state ─────────────────────────────────────────────────
    private AudioClip micClip;
    private string micDevice;
    private bool isListening;
    private int lastMicPos;

    // ─── Pipeline constants ──────────────────────────────────────────
    // openWakeWord processes audio in 1280-sample chunks (80ms at 16kHz)
    private const int SAMPLE_RATE = 16000;
    private const int CHUNK_SIZE = 1280;       // 80ms of audio
    private const int N_MELS = 32;             // Mel bands
    private const int MEL_FRAMES_NEEDED = 76;  // Frames needed for embedding model
    private const int EMBEDDING_SIZE = 96;     // Output size of embedding model
    private const int EMBEDDINGS_NEEDED = 16;  // Embeddings needed for classifier

    // ─── Collection buffers ──────────────────────────────────────────
    private Queue<float> audioBuffer = new Queue<float>();      // Raw mic samples
    private List<float> collectedAudio = new List<float>();     // Full utterance buffer
    private List<float[]> melFrameBuffer = new List<float[]>(); // Mel frames for pipeline
    private List<float[]> embeddingBuffer = new List<float[]>(); // Embeddings for pipeline

    // ─── VAD timing ──────────────────────────────────────────────────
    private float silenceTimer = 0f;
    private float collectionStartTime = 0f;
    private float lastReadTime;

    // ─── Public API ──────────────────────────────────────────────────

    /// <summary>Start listening for the wake word on the given microphone.</summary>
    public void StartListening(string device)
    {
        if (isListening) return;
        if (string.IsNullOrEmpty(device))
        {
            Debug.LogError("[VR] [WakeWord] No mic device provided.");
            return;
        }

        micDevice = device;

        // Initialize inference workers
        if (!InitializeWorkers()) return;

        // Start mic in loop mode — 10 second buffer at 16kHz
        micClip = Microphone.Start(micDevice, true, 10, SAMPLE_RATE);
        lastMicPos = 0;
        isListening = true;

        // Clear all state
        ResetState();

        Debug.Log("[VR] [WakeWord] Local ONNX wake word detection started (zero API calls).");
    }

    /// <summary>Stop listening and release resources.</summary>
    public void StopListening()
    {
        if (!isListening) return;
        isListening = false;

        if (Microphone.IsRecording(micDevice))
            Microphone.End(micDevice);

        if (micClip != null)
        {
            Destroy(micClip);
            micClip = null;
        }

        Debug.Log("[VR] [WakeWord] Wake word detection stopped.");
    }

    /// <summary>Check if the detector is currently listening.</summary>
    public bool IsListening => isListening;

    // ─── Lifecycle ───────────────────────────────────────────────────

    void OnDestroy()
    {
        StopListening();
        DisposeWorkers();
    }

    void Update()
    {
        if (!isListening) return;

        // Always read fresh mic samples
        ReadMicSamples();

        switch (currentState)
        {
            case DetectorState.Idle:
                UpdateIdle();
                break;
            case DetectorState.Collecting:
                UpdateCollecting();
                break;
            case DetectorState.Processing:
                ProcessCollectedAudio();
                break;
        }
    }

    // ─── State: IDLE ─────────────────────────────────────────────────
    // Lightweight RMS monitoring. No inference cost.
    // Dequeue chunks and check if any have sound.

    void UpdateIdle()
    {
        while (audioBuffer.Count >= CHUNK_SIZE)
        {
            float[] chunk = DequeueChunk();

            // Use raw RMS boosted by multiplier for the energy check only
            // Do NOT modify the stored audio — normalization handles volume later
            float rms = CalculateRMS(chunk) * volumeMultiplier;

            if (rms >= energyThreshold)
            {
                // Voice activity detected — start collecting
                currentState = DetectorState.Collecting;
                collectionStartTime = Time.time;
                silenceTimer = 0f;

                // Seed the collection buffer with RAW audio (not amplified)
                collectedAudio.Clear();
                for (int i = 0; i < chunk.Length; i++)
                    collectedAudio.Add(chunk[i]);

                Debug.Log($"[VR] [WakeWord] Voice detected (RMS: {rms:F5}) → COLLECTING");
                return;
            }
            else
            {
                if (debugLogging)
                    Debug.Log($"[VR] [WakeWord] Idle (RMS: {rms:F5} < {energyThreshold:F5})");
            }
        }
    }

    // ─── State: COLLECTING ───────────────────────────────────────────
    // Accumulate all audio. Track silence to know when speech ends.

    void UpdateCollecting()
    {
        float elapsed = Time.time - collectionStartTime;

        // Safety cap — don't collect forever
        if (elapsed >= maxCollectionDuration)
        {
            Debug.Log($"[VR] [WakeWord] Max collection duration ({maxCollectionDuration}s) reached → PROCESSING");
            currentState = DetectorState.Processing;
            return;
        }

        // Append all available audio to the collection buffer
        while (audioBuffer.Count >= CHUNK_SIZE)
        {
            float[] chunk = DequeueChunk();

            // Use raw RMS boosted by multiplier for silence detection only
            float rms = CalculateRMS(chunk) * volumeMultiplier;

            // Always store RAW audio (not amplified) — normalization handles volume
            for (int i = 0; i < chunk.Length; i++)
                collectedAudio.Add(chunk[i]);

            if (rms >= energyThreshold)
            {
                // Still hearing sound — reset silence timer
                silenceTimer = 0f;

                if (debugLogging)
                    Debug.Log($"[VR] [WakeWord] Collecting... (RMS: {rms:F5}, {collectedAudio.Count / (float)SAMPLE_RATE:F2}s buffered)");
            }
            else
            {
                // Silence chunk — increment timer
                silenceTimer += (float)CHUNK_SIZE / SAMPLE_RATE; // 80ms per chunk

                if (debugLogging)
                    Debug.Log($"[VR] [WakeWord] Silence: {silenceTimer:F2}s / {silenceTimeout:F2}s ({collectedAudio.Count / (float)SAMPLE_RATE:F2}s buffered)");

                if (silenceTimer >= silenceTimeout)
                {
                    Debug.Log($"[VR] [WakeWord] Silence timeout ({silenceTimeout}s) → PROCESSING ({collectedAudio.Count / (float)SAMPLE_RATE:F2}s of audio)");
                    currentState = DetectorState.Processing;
                    return;
                }
            }
        }
    }

    // ─── State: PROCESSING ───────────────────────────────────────────
    // Normalize the entire collected buffer, chunk it, run through pipeline.
    // This mirrors the Python script's whole-file processing.

    void ProcessCollectedAudio()
    {
        if (collectedAudio.Count < CHUNK_SIZE)
        {
            Debug.Log("[VR] [WakeWord] Collected audio too short, discarding → IDLE");
            ResetState();
            return;
        }

        float[] audio = collectedAudio.ToArray();
        int totalSamples = audio.Length;

        Debug.Log($"[VR] [WakeWord] Processing {totalSamples / (float)SAMPLE_RATE:F2}s of audio ({totalSamples} samples)");

        // ── Step 1: Global normalization (matches Python exactly) ─────
        // Python: audio = audio / np.max(np.abs(audio))
        float maxAbs = 0f;
        for (int i = 0; i < totalSamples; i++)
        {
            float abs = Mathf.Abs(audio[i]);
            if (abs > maxAbs) maxAbs = abs;
        }

        if (maxAbs > 0.00001f)
        {
            float scale = 1f / maxAbs;
            for (int i = 0; i < totalSamples; i++)
                audio[i] *= scale;

            if (debugLogging)
                Debug.Log($"[VR] [WakeWord] Normalized audio (maxAbs was {maxAbs:F6}, scaled by {scale:F2})");
        }

        // ── Step 2: Process chunks through the 3-stage pipeline ──────
        // Pre-fill buffers with zeros to match openWakeWord Python library:
        //   self.feature_buffer = np.zeros((1, 1, 76, 32))
        //   self.embedding_buffer = np.zeros((1, 16, 96))
        // This lets the classifier run from the very first chunk instead of
        // waiting for 31+ chunks to accumulate real data.
        melFrameBuffer.Clear();
        for (int i = 0; i < MEL_FRAMES_NEEDED; i++)
            melFrameBuffer.Add(new float[N_MELS]); // zeros

        embeddingBuffer.Clear();
        for (int i = 0; i < EMBEDDINGS_NEEDED; i++)
            embeddingBuffer.Add(new float[EMBEDDING_SIZE]); // zeros

        float highestProbability = 0f;
        bool detected = false;
        int chunksProcessed = 0;
        int totalMelFrames = 0;
        int totalEmbeddings = 0;
        int classifierRuns = 0;

        for (int offset = 0; offset + CHUNK_SIZE <= totalSamples; offset += CHUNK_SIZE)
        {
            // Extract chunk
            float[] chunk = new float[CHUNK_SIZE];
            Array.Copy(audio, offset, chunk, 0, CHUNK_SIZE);

            chunksProcessed++;

            // Stage 1: Audio → Mel Spectrogram
            List<float[]> melFrames = RunMelSpectrogram(chunk);
            if (melFrames == null || melFrames.Count == 0)
            {
                if (debugLogging)
                    Debug.Log($"[VR] [WakeWord] Chunk {chunksProcessed}: mel returned null/empty");
                continue;
            }

            totalMelFrames += melFrames.Count;
            melFrameBuffer.AddRange(melFrames);

            // Stage 2: When we have enough mel frames → Embedding
            if (melFrameBuffer.Count >= MEL_FRAMES_NEEDED)
            {
                float[] embedding = RunEmbedding();
                if (embedding == null)
                {
                    if (debugLogging)
                        Debug.Log($"[VR] [WakeWord] Chunk {chunksProcessed}: embedding returned null (melBuf={melFrameBuffer.Count})");
                }
                else
                {
                    totalEmbeddings++;
                    embeddingBuffer.Add(embedding);

                    // Keep only the last EMBEDDINGS_NEEDED embeddings
                    while (embeddingBuffer.Count > EMBEDDINGS_NEEDED)
                        embeddingBuffer.RemoveAt(0);

                    if (debugLogging)
                        Debug.Log($"[VR] [WakeWord] Chunk {chunksProcessed}: emb OK (embBuf={embeddingBuffer.Count}, need={EMBEDDINGS_NEEDED})");

                    // Stage 3: When we have enough embeddings → Classification
                    if (embeddingBuffer.Count >= EMBEDDINGS_NEEDED)
                    {
                        classifierRuns++;
                        float probability = RunClassifier();

                        if (probability > highestProbability)
                            highestProbability = probability;

                        Debug.Log($"[VR] [WakeWord] Classifier run {classifierRuns}: p={probability:F4}");

                        if (probability >= detectionThreshold)
                        {
                            detected = true;
                            float timestamp = offset / (float)SAMPLE_RATE;
                            Debug.Log($"[VR] [WakeWord] 🎯 WAKE WORD DETECTED at {timestamp:F2}s (p={probability:F4})");
                        }
                    }
                    else if (debugLogging)
                    {
                        Debug.Log($"[VR] [WakeWord] Chunk {chunksProcessed}: not enough embeddings ({embeddingBuffer.Count} < {EMBEDDINGS_NEEDED})");
                    }
                }

                // Keep only the most recent MEL_FRAMES_NEEDED frames
                if (melFrameBuffer.Count > MEL_FRAMES_NEEDED)
                {
                    int overflow = melFrameBuffer.Count - MEL_FRAMES_NEEDED;
                    melFrameBuffer.RemoveRange(0, overflow);
                }
            }
        }

        Debug.Log($"[VR] [WakeWord] Pipeline stats: {chunksProcessed} chunks → {totalMelFrames} mel frames → {totalEmbeddings} embeddings → {classifierRuns} classifier runs | best p={highestProbability:F4}");

        if (detected)
        {
            Debug.Log("[VR] [WakeWord] ✅ WAKE WORD CONFIRMED — firing event");
            ResetState();
            OnWakeWordDetected?.Invoke();
        }
        else
        {
            Debug.Log($"[VR] [WakeWord] ❌ No wake word (best: {highestProbability:F4} < {detectionThreshold:F4}) → IDLE");
            ResetState();
        }
    }

    // ─── Audio Reading ───────────────────────────────────────────────

    void ReadMicSamples()
    {
        if (micClip == null) return;

        int currentPos = Microphone.GetPosition(micDevice);
        if (currentPos == lastMicPos) return;

        int totalSamples = micClip.samples;
        int samplesToRead;

        if (currentPos > lastMicPos)
            samplesToRead = currentPos - lastMicPos;
        else
            samplesToRead = (totalSamples - lastMicPos) + currentPos;

        if (samplesToRead <= 0) return;

        float[] samples = new float[samplesToRead];

        if (currentPos > lastMicPos)
        {
            micClip.GetData(samples, lastMicPos);
        }
        else
        {
            // Wrap-around
            int tailCount = totalSamples - lastMicPos;
            float[] tail = new float[tailCount];
            float[] head = new float[currentPos];
            micClip.GetData(tail, lastMicPos);
            if (currentPos > 0) micClip.GetData(head, 0);

            Array.Copy(tail, 0, samples, 0, tailCount);
            if (currentPos > 0) Array.Copy(head, 0, samples, tailCount, currentPos);
        }

        // Enqueue samples
        for (int i = 0; i < samples.Length; i++)
            audioBuffer.Enqueue(samples[i]);

        lastMicPos = currentPos;
    }

    // ─── Stage 1: Mel Spectrogram ────────────────────────────────────

    // Track whether we've logged mel debug info (only log once to avoid spam)
    private bool melDebugLogged = false;

    List<float[]> RunMelSpectrogram(float[] audioChunk)
    {
        try
        {
            // OpenWakeWord expects 16-bit PCM scaled float32 [-32768, 32767]
            // Unity's Microphone.GetData returns [-1.0, 1.0]
            float[] scaledChunk = new float[CHUNK_SIZE];
            for (int i = 0; i < CHUNK_SIZE; i++)
            {
                scaledChunk[i] = audioChunk[i] * 32767f;
            }

            // Input: [1, 1280]
            using var inputTensor = new Tensor<float>(new TensorShape(1, CHUNK_SIZE), scaledChunk);
            melWorker.Schedule(inputTensor);

            var output = melWorker.PeekOutput() as Tensor<float>;
            if (output == null)
            {
                if (!melDebugLogged)
                {
                    Debug.LogWarning("[VR] [WakeWord] Mel output is NULL");
                    melDebugLogged = true;
                }
                return null;
            }

            output.ReadbackRequest();
            output.ReadbackAndClone();

            // Log the actual output shape (once) so we know what the model produces
            if (!melDebugLogged)
            {
                Debug.Log($"[VR] [WakeWord] Mel output shape: {output.shape} (rank={output.shape.rank}, length={output.shape.length})");
                for (int d = 0; d < output.shape.rank; d++)
                    Debug.Log($"[VR] [WakeWord] Mel shape dim[{d}] = {output.shape[d]}");
                melDebugLogged = true;
            }

            // Parse output shape dynamically
            int rank = output.shape.rank;
            int numFrames;
            int numMels;

            if (rank == 4)
            {
                // Expected: [1, 1, frames, 32]
                numFrames = output.shape[2];
                numMels = output.shape[3];
            }
            else if (rank == 3)
            {
                // Possible: [1, frames, 32]
                numFrames = output.shape[1];
                numMels = output.shape[2];
            }
            else if (rank == 2)
            {
                // Possible: [frames, 32]
                numFrames = output.shape[0];
                numMels = output.shape[1];
            }
            else
            {
                Debug.LogWarning($"[VR] [WakeWord] Unexpected mel output rank: {rank}");
                return null;
            }

            if (numFrames <= 0 || numMels <= 0)
            {
                Debug.LogWarning($"[VR] [WakeWord] Mel produced 0 frames or 0 mels: frames={numFrames}, mels={numMels}");
                return null;
            }

            List<float[]> generatedFrames = new List<float[]>();
            for (int f = 0; f < numFrames; f++)
            {
                float[] melFrame = new float[N_MELS];
                for (int i = 0; i < N_MELS && i < numMels; i++)
                {
                    float raw;
                    if (rank == 4)
                        raw = output[0, 0, f, i];
                    else if (rank == 3)
                        raw = output[0, f, i];
                    else
                        raw = output[f, i];

                    // ── Critical transform from openWakeWord Python library ──
                    // Python: melspec_transform = lambda x: x/10 + 2
                    // This rescales the ONNX mel output to match what Google's
                    // speech_embedding model expects as input.
                    melFrame[i] = raw / 10f + 2f;
                }
                generatedFrames.Add(melFrame);
            }

            return generatedFrames;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VR] [WakeWord] Mel EXCEPTION: {e.Message}\n{e.StackTrace}");
            return null;
        }
    }

    // ─── Stage 2: Embedding ──────────────────────────────────────────

    private bool embeddingDebugLogged = false;

    float[] RunEmbedding()
    {
        try
        {
            // Input: [1, 76, 32, 1] — 76 mel frames × 32 mel bands × 1 channel
            float[] inputData = new float[MEL_FRAMES_NEEDED * N_MELS];
            int startIdx = melFrameBuffer.Count - MEL_FRAMES_NEEDED;

            for (int f = 0; f < MEL_FRAMES_NEEDED; f++)
            {
                float[] frame = melFrameBuffer[startIdx + f];
                for (int m = 0; m < N_MELS; m++)
                {
                    inputData[f * N_MELS + m] = frame[m];
                }
            }

            using var inputTensor = new Tensor<float>(
                new TensorShape(1, MEL_FRAMES_NEEDED, N_MELS, 1), inputData);

            embeddingWorker.Schedule(inputTensor);

            var output = embeddingWorker.PeekOutput() as Tensor<float>;
            if (output == null)
            {
                if (!embeddingDebugLogged) Debug.LogWarning("[VR] [WakeWord] Embedding output is NULL");
                return null;
            }

            output.ReadbackRequest();
            output.ReadbackAndClone();

            // Log shape once
            if (!embeddingDebugLogged)
            {
                Debug.Log($"[VR] [WakeWord] Embedding output shape: {output.shape} (rank={output.shape.rank})");
                for (int d = 0; d < output.shape.rank; d++)
                    Debug.Log($"[VR] [WakeWord] Emb shape dim[{d}] = {output.shape[d]}");
                embeddingDebugLogged = true;
            }

            // Dynamically read output based on actual rank
            int rank = output.shape.rank;
            int embSize = output.shape[rank - 1]; // last dim should be 96
            float[] embedding = new float[EMBEDDING_SIZE];

            for (int i = 0; i < EMBEDDING_SIZE && i < embSize; i++)
            {
                if (rank == 4)
                    embedding[i] = output[0, 0, 0, i];
                else if (rank == 3)
                    embedding[i] = output[0, 0, i];
                else if (rank == 2)
                    embedding[i] = output[0, i];
                else
                    embedding[i] = output[i];
            }

            return embedding;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VR] [WakeWord] Embedding error: {e.Message}\n{e.StackTrace}");
            return null;
        }
    }

    // ─── Stage 3: Classification ─────────────────────────────────────

    private bool classifierDebugLogged = false;

    float RunClassifier()
    {
        try
        {
            // Input: [1, 16, 96]
            float[] inputData = new float[EMBEDDINGS_NEEDED * EMBEDDING_SIZE];
            int startIdx = embeddingBuffer.Count - EMBEDDINGS_NEEDED;

            for (int e = 0; e < EMBEDDINGS_NEEDED; e++)
            {
                float[] emb = embeddingBuffer[startIdx + e];
                for (int f = 0; f < EMBEDDING_SIZE; f++)
                {
                    inputData[e * EMBEDDING_SIZE + f] = emb[f];
                }
            }

            using var inputTensor = new Tensor<float>(
                new TensorShape(1, EMBEDDINGS_NEEDED, EMBEDDING_SIZE), inputData);

            classifierWorker.Schedule(inputTensor);

            var output = classifierWorker.PeekOutput() as Tensor<float>;
            if (output == null) return 0f;

            output.ReadbackRequest();
            output.ReadbackAndClone();

            // Log shape and raw values once
            if (!classifierDebugLogged)
            {
                Debug.Log($"[VR] [WakeWord] Classifier output shape: {output.shape} (rank={output.shape.rank})");
                for (int d = 0; d < output.shape.rank; d++)
                    Debug.Log($"[VR] [WakeWord] Cls shape dim[{d}] = {output.shape[d]}");

                // Log all output values
                for (int i = 0; i < output.shape.length && i < 10; i++)
                    Debug.Log($"[VR] [WakeWord] Cls output[{i}] = {output[i]:F6}");

                classifierDebugLogged = true;
            }

            // Read output dynamically based on rank
            int rank = output.shape.rank;
            if (rank == 2)
                return output[0, 0];
            else if (rank == 1)
                return output[0];
            else if (rank == 3)
                return output[0, 0, 0];
            else
                return output[0];
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VR] [WakeWord] Classifier error: {e.Message}\n{e.StackTrace}");
            return 0f;
        }
    }

    // ─── Initialization ──────────────────────────────────────────────

    bool InitializeWorkers()
    {
        try
        {
            if (melSpectrogramModelAsset == null || embeddingModelAsset == null || classifierModelAsset == null)
            {
                Debug.LogError("[WakeWord] Missing ONNX model assets. Assign them in the Inspector.");
                return false;
            }

            DisposeWorkers();

            var melModel = ModelLoader.Load(melSpectrogramModelAsset);
            var embModel = ModelLoader.Load(embeddingModelAsset);
            var clsModel = ModelLoader.Load(classifierModelAsset);

            // Use CPU backend for reliability on Quest
            melWorker = new Worker(melModel, BackendType.CPU);
            embeddingWorker = new Worker(embModel, BackendType.CPU);
            classifierWorker = new Worker(clsModel, BackendType.CPU);

            Debug.Log("[VR] [WakeWord] ONNX models loaded successfully.");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[VR] [WakeWord] Failed to load models: {e.Message}");
            return false;
        }
    }

    void DisposeWorkers()
    {
        melWorker?.Dispose();
        embeddingWorker?.Dispose();
        classifierWorker?.Dispose();
        melWorker = null;
        embeddingWorker = null;
        classifierWorker = null;
    }

    // ─── Utility ─────────────────────────────────────────────────────

    float[] DequeueChunk()
    {
        float[] chunk = new float[CHUNK_SIZE];
        for (int i = 0; i < CHUNK_SIZE; i++)
            chunk[i] = audioBuffer.Dequeue();
        return chunk;
    }

    void ApplyVolumeMultiplier(float[] chunk)
    {
        if (volumeMultiplier > 1.0f)
        {
            for (int i = 0; i < chunk.Length; i++)
                chunk[i] *= volumeMultiplier;
        }
    }

    float CalculateRMS(float[] samples)
    {
        float sum = 0f;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i] * samples[i];
        return Mathf.Sqrt(sum / samples.Length);
    }

    void ResetState()
    {
        currentState = DetectorState.Idle;
        audioBuffer.Clear();
        collectedAudio.Clear();
        melFrameBuffer.Clear();
        embeddingBuffer.Clear();
        silenceTimer = 0f;
        collectionStartTime = 0f;
        melDebugLogged = false;
        embeddingDebugLogged = false;
        classifierDebugLogged = false;
    }
}
