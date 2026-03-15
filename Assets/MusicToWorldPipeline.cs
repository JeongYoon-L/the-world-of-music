using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Linq;

public class MusicToWorldPipeline : MonoBehaviour {
    [Header("OpenAI Config")]
    [Tooltip("OpenAI API Key (do not commit to version control)")]
    public string openAiApiKey = "Enter your OpenAI API key here";

    [Header("World Labs Config")]
    public string worldLabsApiKey = "Enter your World Labs API key here";
    public string worldLabsModel = "Marble 0.1-mini";

    [Header("Audio")]
    [Tooltip("Full path (e.g. .../music_test_file1.mp3) or filename under StreamingAssets")]
    public string audioPathOrName = "music_test_file1.mp3";

    private string status = "Ready";

    // OpenAI transcription response
    [System.Serializable]
    public class TranscriptionResponse {
        public string text;
    }

    // OpenAI chat response
    [System.Serializable]
    public class ChatMessage {
        public string role;
        public string content;
    }
    [System.Serializable]
    public class ChatChoice {
        public ChatMessage message;
    }
    [System.Serializable]
    public class ChatResponse {
        public List<ChatChoice> choices;
    }

    // Music analysis result (from GPT)
    [System.Serializable]
    public class MusicAnalysisResult {
        public string audio_summary;
        public string vibe;
        public string world_prompt;
        public string music_prompt;
    }

    // World Labs (same as WorldLabsIntegrator)
    [System.Serializable]
    public class OpResponse {
        public string operation_id;
        public bool done;
        public OperationError error;
        public WorldResponse response;
    }
    [System.Serializable]
    public class OperationError {
        public int? code;
        public string message;
    }
    [System.Serializable]
    public class WorldResponse {
        public WorldAssets assets;
    }
    [System.Serializable]
    public class WorldAssets {
        public string thumbnail_url;
        public ImageryAssets imagery;
        public MeshAssets mesh;
        public SplatAssets splats;
    }
    [System.Serializable]
    public class ImageryAssets { public string pano_url; }
    [System.Serializable]
    public class MeshAssets { public string collider_mesh_url; }
    [System.Serializable]
    public class SplatAssets {
        public Dictionary<string, string> spz_urls;
    }

    void OnGUI() {
        float width = 380;
        float height = 190;
        float x = (Screen.width - width) / 2;
        float y = (Screen.height - height) / 2;

        GUI.Box(new Rect(x, y, width, height), "Music → AI Analysis → World Generation");
        audioPathOrName = GUI.TextField(new Rect(x + 10, y + 35, width - 30, 22), audioPathOrName);
        GUI.Label(new Rect(x + 10, y + 60, width - 30, 18), "Audio path or filename under StreamingAssets");
        if (GUI.Button(new Rect(x + 10, y + 85, width - 30, 35), "Import Audio & Generate World")) {
            StartCoroutine(RunPipeline());
        }
        GUI.Label(new Rect(x + 10, y + 128, width - 30, 45), "Status: " + status);
    }

    string ResolveAudioPath() {
        if (string.IsNullOrEmpty(audioPathOrName)) return null;
        if (Path.IsPathRooted(audioPathOrName) && File.Exists(audioPathOrName))
            return audioPathOrName;
        string streaming = Path.Combine(Application.dataPath, "StreamingAssets", audioPathOrName);
        if (File.Exists(streaming)) return streaming;
        string pdream = Path.Combine("/Users/zanzan/Desktop/PDream", audioPathOrName);
        if (File.Exists(pdream)) return pdream;
        return null;
    }

    IEnumerator RunPipeline() {
        string resolvedPath = ResolveAudioPath();
        if (string.IsNullOrEmpty(resolvedPath)) {
            status = "Audio file not found. Enter a valid path.";
            yield break;
        }

        // ---- 1) OpenAI: Transcribe ----
        if (string.IsNullOrEmpty(openAiApiKey) || openAiApiKey.Contains("Enter your")) {
            status = "Please enter a valid OpenAI API key in the Inspector first.";
            yield break;
        }
        status = "Transcribing audio (OpenAI)...";
        string transcript = null;
        yield return TranscribeAudio(resolvedPath, (t) => transcript = t);
        if (string.IsNullOrEmpty(transcript)) {
            // When API returns empty (e.g. instrumental), use placeholder so AI infers world from "music" mood
            if (status != null && status.Contains("Transcription result empty")) {
                transcript = "[No speech in audio - instrumental or music-only file. The user wants a 3D world that matches the mood of this music. Infer an atmospheric, explorable environment and output a vivid world_prompt.]";
                status = "No speech detected. Generating world from music mood...";
            } else {
                if (status == "Transcribing audio (OpenAI)...") status = "Transcription failed or empty (check API key and Console).";
                yield break;
            }
        }

        // ---- 2) OpenAI: Analyze -> world_prompt ----
        status = "Analyzing music and generating world description (OpenAI)...";
        string worldPrompt = null;
        yield return AnalyzeMusicToWorldPrompt(transcript, (prompt) => worldPrompt = prompt);
        if (string.IsNullOrEmpty(worldPrompt)) {
            status = "Analysis failed or no world_prompt returned.";
            yield break;
        }

        // ---- 3) World Labs: Generate & Download ----
        status = "Generating world (World Labs)...";
        yield return GenerateAndDownloadWorld(worldPrompt);
    }

    IEnumerator TranscribeAudio(string filePath, System.Action<string> onDone) {
        byte[] fileBytes = File.ReadAllBytes(filePath);
        string fileName = Path.GetFileName(filePath);

        string url = "https://api.openai.com/v1/audio/transcriptions";
        List<IMultipartFormSection> form = new List<IMultipartFormSection> {
            new MultipartFormFileSection("file", fileBytes, fileName, "audio/mpeg"),
            new MultipartFormDataSection("model", "gpt-4o-transcribe"),
            new MultipartFormDataSection("response_format", "json")
        };

        using (UnityWebRequest req = UnityWebRequest.Post(url, form)) {
            req.SetRequestHeader("Authorization", "Bearer " + openAiApiKey);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success) {
                status = "Transcription request failed: " + req.error;
                string body = req.downloadHandler?.text ?? "";
                if (!string.IsNullOrEmpty(body)) {
                    status += " | " + (body.Length > 120 ? body.Substring(0, 120) + "..." : body);
                    Debug.LogWarning("[MusicToWorld] Transcription API response: " + body);
                }
                onDone?.Invoke(null);
                yield break;
            }
            int code = (int)req.responseCode;
            string raw = req.downloadHandler?.text ?? "";
            if (code < 200 || code >= 300) {
                status = "Transcription HTTP " + code + ": " + (raw.Length > 100 ? raw.Substring(0, 100) + "..." : raw);
                Debug.LogWarning("[MusicToWorld] Transcription HTTP " + code + ": " + raw);
                onDone?.Invoke(null);
                yield break;
            }
            Debug.Log("[MusicToWorld] Transcription API full response: " + raw);
            string text = null;
            try {
                var jobj = JObject.Parse(raw);
                text = jobj["text"]?.ToString();
                if (string.IsNullOrEmpty(text))
                    text = jobj["Text"]?.ToString();
            } catch { }
            if (string.IsNullOrEmpty(text)) {
                var trans = JsonConvert.DeserializeObject<TranscriptionResponse>(raw);
                text = trans?.text;
            }
            if (string.IsNullOrEmpty(text)) {
                status = "Transcription result empty. Response: " + (raw.Length > 80 ? raw.Substring(0, 80) + "..." : raw);
                Debug.LogWarning("[MusicToWorld] Transcription result empty; see full response in log above.");
                onDone?.Invoke(null);
                yield break;
            }
            onDone?.Invoke(text);
        }
    }

    IEnumerator AnalyzeMusicToWorldPrompt(string transcript, System.Action<string> onWorldPrompt) {
        string systemPrompt = @"You are helping build a world-model demo from an audio clip.
Return STRICT JSON only, no markdown, in this exact format:
{ ""audio_summary"": ""short paragraph"", ""vibe"": ""few words"", ""world_prompt"": ""one vivid spatial world prompt for a 3D environment"", ""music_prompt"": ""one short music description"" }
Focus on a spatial environment. Convert feelings into concrete visual details. Vivid but concise.";

        string userContent = "The following text comes from transcribed or interpreted audio:\n" + transcript;

        var messages = new object[] {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userContent }
        };
        var body = new { model = "gpt-4o-mini", messages };
        string jsonBody = JsonConvert.SerializeObject(body);

        string url = "https://api.openai.com/v1/chat/completions";
        using (UnityWebRequest req = new UnityWebRequest(url, "POST")) {
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + openAiApiKey);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success) {
                status = "Analysis request failed: " + req.error;
                onWorldPrompt?.Invoke(null);
                yield break;
            }
            var chat = JsonConvert.DeserializeObject<ChatResponse>(req.downloadHandler.text);
            string content = chat?.choices != null && chat.choices.Count > 0 ? chat.choices[0].message?.content : null;
            if (string.IsNullOrEmpty(content)) {
                onWorldPrompt?.Invoke(null);
                yield break;
            }
            content = content.Trim();
            if (content.StartsWith("```")) {
                int start = content.IndexOf('{');
                int end = content.LastIndexOf('}') + 1;
                if (start >= 0 && end > start) content = content.Substring(start, end - start);
            }
            try {
                var result = JsonConvert.DeserializeObject<MusicAnalysisResult>(content);
                onWorldPrompt?.Invoke(result?.world_prompt);
            } catch {
                status = "Failed to parse analysis result JSON.";
                onWorldPrompt?.Invoke(null);
            }
        }
    }

    IEnumerator GenerateAndDownloadWorld(string prompt) {
        string createUrl = "https://api.worldlabs.ai/marble/v1/worlds:generate";
        var body = new {
            model = worldLabsModel,
            world_prompt = new { type = "text", text_prompt = prompt }
        };
        string jsonBody = JsonConvert.SerializeObject(body);

        using (UnityWebRequest post = new UnityWebRequest(createUrl, "POST")) {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            post.uploadHandler = new UploadHandlerRaw(bodyRaw);
            post.downloadHandler = new DownloadHandlerBuffer();
            post.SetRequestHeader("Content-Type", "application/json");
            post.SetRequestHeader("WLT-Api-Key", worldLabsApiKey);
            yield return post.SendWebRequest();

            if (post.result != UnityWebRequest.Result.Success) {
                status = "World Labs request failed: " + post.error;
                yield break;
            }
            OpResponse res = JsonConvert.DeserializeObject<OpResponse>(post.downloadHandler.text);
            string opId = res.operation_id;

            bool isDone = false;
            string downloadUrl = "";
            while (!isDone) {
                status = "Building world in cloud... please wait.";
                yield return new WaitForSeconds(5);
                string pollUrl = "https://api.worldlabs.ai/marble/v1/operations/" + opId;
                using (UnityWebRequest poll = UnityWebRequest.Get(pollUrl)) {
                    poll.SetRequestHeader("WLT-Api-Key", worldLabsApiKey);
                    yield return poll.SendWebRequest();
                    string rawJson = poll.downloadHandler.text;
                    OpResponse pollRes = JsonConvert.DeserializeObject<OpResponse>(rawJson);
                    if (pollRes != null && pollRes.done) {
                        isDone = true;
                        if (pollRes.error != null && !string.IsNullOrEmpty(pollRes.error.message)) {
                            status = "Generation failed: " + pollRes.error.message;
                            yield break;
                        }
                        if (pollRes.response?.assets != null) {
                            var a = pollRes.response.assets;
                            if (a.splats?.spz_urls != null && a.splats.spz_urls.Count > 0)
                                downloadUrl = a.splats.spz_urls.Values.First();
                            else if (!string.IsNullOrEmpty(a.imagery?.pano_url)) downloadUrl = a.imagery.pano_url;
                            else if (!string.IsNullOrEmpty(a.thumbnail_url)) downloadUrl = a.thumbnail_url;
                            else if (!string.IsNullOrEmpty(a.mesh?.collider_mesh_url)) downloadUrl = a.mesh.collider_mesh_url;
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(downloadUrl)) {
                status = "Done but no download link received.";
                yield break;
            }

            status = "Downloading world model...";
            string fileExt = downloadUrl.IndexOf(".spz", System.StringComparison.OrdinalIgnoreCase) >= 0 ? ".spz" : ".ply";
            if (downloadUrl.IndexOf("pano", System.StringComparison.OrdinalIgnoreCase) >= 0 || downloadUrl.IndexOf("imagery", System.StringComparison.OrdinalIgnoreCase) >= 0) fileExt = ".jpg";
            if (downloadUrl.IndexOf("thumbnail", System.StringComparison.OrdinalIgnoreCase) >= 0) fileExt = ".jpg";
            if (downloadUrl.IndexOf("collider", System.StringComparison.OrdinalIgnoreCase) >= 0) fileExt = ".glb";

            using (UnityWebRequest dl = UnityWebRequest.Get(downloadUrl)) {
                yield return dl.SendWebRequest();
                if (dl.result == UnityWebRequest.Result.Success) {
                    string folderPath = Path.Combine(Application.dataPath, "GaussianAssets");
                    if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
                    string savePath = Path.Combine(folderPath, "AI_World_From_Music" + fileExt);
                    File.WriteAllBytes(savePath, dl.downloadHandler.data);
                    status = "Done! Saved: " + savePath;
                    Debug.Log("MusicToWorld file saved: " + savePath);
#if UNITY_EDITOR
                    UnityEditor.AssetDatabase.Refresh();
#endif
                } else {
                    status = "Download failed.";
                }
            }
        }
    }
}
