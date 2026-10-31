namespace Murmur.App.Audio
{
    internal enum UnprepareOutcome
    {
        Free,
        Retry,
        Leak
    }

    internal static class MeterTeardown
    {
        public const int StillPlaying = 33;
        public const int MaxUnprepareRetries = 10;
        public const int RetryDelayMs = 10;

        public static UnprepareOutcome ClassifyUnprepare(int result)
        {
            if (result == 0)
            {
                return UnprepareOutcome.Free;
            }
            if (result == StillPlaying)
            {
                return UnprepareOutcome.Retry;
            }
            return UnprepareOutcome.Leak;
        }

        public static bool MayFreeBuffer(int unprepareResult)
        {
            return ClassifyUnprepare(unprepareResult) == UnprepareOutcome.Free;
        }

        public static bool ShouldRetryUnprepare(int result, int retries)
        {
            return ClassifyUnprepare(result) == UnprepareOutcome.Retry && retries < MaxUnprepareRetries;
        }
    }
}
