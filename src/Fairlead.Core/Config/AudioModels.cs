using System;
using System.Collections.Generic;

namespace Fairlead.Core.Config
{
    /// <summary>
    /// Which models are audio models, from their names (providers do not say): speech synthesis ("tts", "speech") and
    /// transcription ("whisper", "transcribe", "stt"). Used to mark them in /v1/models; routing never depends on it.
    /// </summary>
    public static class AudioModels
    {
        public const string Speech = "speech";
        public const string Transcription = "transcription";

        public static List<string> KindsOf(string modelId)
        {
            var kinds = new List<string>();
            if (string.IsNullOrEmpty(modelId)) return kinds;
            var id = modelId.ToLowerInvariant();
            var toText = id.Contains("speech-to-text") || id.Contains("whisper") || id.Contains("transcribe") || id.Contains("stt");
            var toSpeech = !id.Contains("speech-to-text") && (id.Contains("tts") || id.Contains("speech"));
            if (toSpeech) kinds.Add(Speech);
            if (toText) kinds.Add(Transcription);
            return kinds;
        }
    }
}
