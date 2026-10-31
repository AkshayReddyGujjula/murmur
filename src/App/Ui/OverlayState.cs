using System;

namespace Murmur.App.Ui
{
    public sealed class OverlayState
    {
        public const double DoneHideSeconds = 1.1;
        public const double ErrorHideSeconds = 3.5;
        public const double WorkingHideSeconds = 120.0;

        private OverlayMode mode;
        private DateTime shownAt;

        public OverlayState()
        {
            mode = OverlayMode.Hidden;
            shownAt = DateTime.UtcNow;
        }

        public OverlayMode Mode
        {
            get { return mode; }
        }

        public bool IsShowing
        {
            get { return mode != OverlayMode.Hidden; }
        }

        public DateTime ShownAt
        {
            get { return shownAt; }
        }

        public void Enter(OverlayMode next, DateTime now)
        {
            mode = next;
            shownAt = now;
        }

        public bool ShouldHide(DateTime now)
        {
            TimeSpan elapsed = now - shownAt;
            if (mode == OverlayMode.Done)
            {
                return elapsed >= TimeSpan.FromSeconds(DoneHideSeconds);
            }
            if (mode == OverlayMode.Error)
            {
                return elapsed >= TimeSpan.FromSeconds(ErrorHideSeconds);
            }
            if (mode == OverlayMode.Working)
            {
                return elapsed >= TimeSpan.FromSeconds(WorkingHideSeconds);
            }
            return false;
        }
    }
}
