namespace Murmur.App.Engine
{
    public sealed class EngineOptions
    {
        public string ClaudePath;
        public int ReadyTimeoutMs = 45000;
        public int ListenTimeoutMs = 8000;
        public int FinalizeTimeoutMs = 90000;
        public int ExportTimeoutMs = 15000;
        public int StableMs = 700;
        public int NoSpeechMs = 5000;
        public int PollMs = 100;
        public int HoldRepeatMs = 50;
    }
}
