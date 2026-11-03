using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Murmur.App;

namespace Murmur.Tests.App
{
    internal static class WiringTests
    {
        [STAThread]
        public static int Main()
        {
            try
            {
                RejectLateTranscript(0, 7, "cancelled take");
                RejectLateTranscript(8, 7, "superseded take");
                RejectLateTranscript(0, 0, "invalid take");
                CancelDelayedPaste();
                Console.WriteLine("PASS: cancelled, superseded and invalid transcript callbacks have no side effects.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error);
                return 1;
            }
        }

        private static void RejectLateTranscript(int current, int delivered, string scenario)
        {
            // Deliberately leave every dependency null. A stale callback must return
            // before touching the meter, history, clipboard, overlay, or engine.
            // No real app, audio device, clipboard, or provider is created here.
            TrayApp app = (TrayApp)FormatterServices.GetUninitializedObject(typeof(TrayApp));
            typeof(TrayApp).GetField("_takeId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, current);
            MethodInfo callback = typeof(TrayApp).GetMethod("OnTakeTranscript", BindingFlags.Instance | BindingFlags.NonPublic);
            try { callback.Invoke(app, new object[] { delivered, "late transcript" }); }
            catch (TargetInvocationException error)
            {
                throw new Exception(scenario + " reached side effects", error.InnerException);
            }
            int remaining = (int)typeof(TrayApp).GetField("_takeId", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(app);
            if (remaining != current) throw new Exception(scenario + " changed the active take");
        }

        private static void CancelDelayedPaste()
        {
            TrayApp app = (TrayApp)FormatterServices.GetUninitializedObject(typeof(TrayApp));
            Timer pending = new Timer();
            FieldInfo field = typeof(TrayApp).GetField("_pasteTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(app, pending);
            typeof(TrayApp).GetMethod("CancelPendingPaste", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null);
            if (field.GetValue(app) != null || pending.Enabled) throw new Exception("delayed paste was not cancelled");
            // Repeated cancellation is safe and must not need a real window or clipboard.
            typeof(TrayApp).GetMethod("CancelPendingPaste", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null);
        }
    }
}
