using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Murmur.App.Engine;
using Murmur.Probe;

namespace Murmur.Tests.App
{
    internal static class EngineTests
    {
        private static readonly string Rule = new string('\u2500', 40);
        internal static readonly object SafetyGate = new object();
        internal static int NewlineWrites;
        private static int _assertions;

        public static int Main()
        {
            try
            {
                ScreenParserTests();
                SessionTests();
                HostTests();
                EnginePollingPolicyTests();
                EngineReadyTests();
                EngineTakeTests();
                EngineFailureTests();
                EngineLifecycleTests();
                EngineCancelQuietTests();
                EngineStopPhaseTests();
                EngineExportSafetyTests();
                EngineFaultTests();
                EnginePromptSafetyTests();
                EngineTakeIdTests();
                EngineBusyBeginTests();
                EngineSupersedeRecordingTests();
                EngineHoldGuardTests();
                EngineCancelQuietWindowTests();
                EngineDiscardBeforeTakeTests();
                EngineClearWaitTests();
                EngineExportTimeoutTests();
                EngineSuspendTests();
                EngineExportEdgeTests();
                EngineWriteSafetyTests();
                Console.WriteLine("PASS: " + _assertions + " engine assertions.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error.Message);
                return 1;
            }
        }

        private static void EnginePollingPolicyTests()
        {
            // Inspect the real scheduling policy without starting a thread or timer.
            DictationEngine engine = (DictationEngine)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(DictationEngine));
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            EngineOptions options = new EngineOptions();
            typeof(DictationEngine).GetField("_options", flags).SetValue(engine, options);
            System.Reflection.FieldInfo phase = typeof(DictationEngine).GetField("_phase", flags);
            System.Reflection.FieldInfo purpose = typeof(DictationEngine).GetField("_exportPurpose", flags);
            System.Reflection.MethodInfo delay = typeof(DictationEngine).GetMethod("PollDelayMilliseconds", flags);
            foreach (object nextPhase in Enum.GetValues(phase.FieldType))
            {
                foreach (object nextPurpose in Enum.GetValues(purpose.FieldType))
                {
                    phase.SetValue(engine, nextPhase); purpose.SetValue(engine, nextPurpose);
                    bool completing = nextPhase.ToString() == "FinalWait"
                        || (nextPhase.ToString() == "ExportWait" && nextPurpose.ToString() == "Transcript");
                    Check((int)delay.Invoke(engine, null) == (completing ? 50 : 100),
                        "only finalization and transcript export poll faster: " + nextPhase + "/" + nextPurpose);
                }
            }
            phase.SetValue(engine, Enum.Parse(phase.FieldType, "FinalWait"));
            options.PollMs = 5;
            Check((int)delay.Invoke(engine, null) == 5, "completion polling preserves faster custom intervals");
            options.PollMs = 100; options.CompletionPollMs = 0;
            Check((int)delay.Invoke(engine, null) == 1, "completion polling cannot create a zero-interval timer");
            Check(options.StableMs == 700 && options.HoldRepeatMs == 50, "settling window and hold cadence remain unchanged");
            Console.WriteLine("PASS: phase-specific completion polling preserves recording and discard timing.");
        }

        private static void EngineExportEdgeTests()
        {
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate { if (holding) terminal.SetScreen(ListeningScreen("")); };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "  \r\n ";
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "empty export starts ready");
                int id = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "empty export records");
                holding = false;
                harness.TerminalAt(0).SetScreen(ReadyScreen("visible words"));
                harness.Engine.EndTake(id);
                Check(harness.WaitTakeFailure(id, 3000), "empty export reports a failure instead of silent discard");
                Check(harness.TakeFailureText(id) == "No speech detected", "empty export explains missing text");
                Check(harness.TakeDiscardCount == 0 && harness.TranscriptCount == 0, "empty export cannot copy or pretend cancel");
            }

            holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate { if (holding) terminal.SetScreen(ListeningScreen("")); };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "recovered words";
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Options.NoSpeechMs = 120;
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "redraw starts ready");
                int id = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "redraw records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(UnknownScreen());
                harness.Engine.EndTake(id);
                Check(harness.WaitState(EngineState.Finalizing, 1000), "redraw enters finalization");
                Thread.Sleep(260);
                Check(harness.Engine.State == EngineState.Finalizing && harness.TakeFailureCount == 0,
                    "missing composer is not proof of silence");
                terminal.SetScreen(ReadyScreen("recovered words"));
                Check(harness.WaitTranscriptCount(1, 3000), "redraw recovers complete transcript");
                Check(harness.TakeTranscriptText(id) == "recovered words", "redraw preserves recovered text");
            }

            foreach (bool cancel in new bool[] { false, true })
            {
                holding = true;
                using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                    delegate(FakeTerminal terminal, FakeSession session) {
                        terminal.OnSpace = delegate { if (holding) terminal.SetScreen(ListeningScreen("")); };
                    }))
                {
                    harness.Options.NoSpeechMs = 40;
                    harness.Options.ScreenUnavailableTimeoutMs = 150;
                    harness.Options.FinalizeTimeoutMs = 2500;
                    harness.Engine.Warm();
                    Check(harness.WaitState(EngineState.Ready, 3000), "missing composer starts ready");
                    int id = harness.Engine.BeginTake();
                    Check(harness.WaitState(EngineState.Recording, 3000), "missing composer records");
                    holding = false;
                    harness.TerminalAt(0).SetScreen(UnknownScreen());
                    if (cancel) harness.Engine.CancelTake(id); else harness.Engine.EndTake(id);
                    Check(cancel ? harness.WaitTakeDiscard(id, 1000) : harness.WaitTakeFailure(id, 1000),
                        "permanently missing composer has its own bounded recovery");
                    if (!cancel) Check(harness.TakeFailureText(id) == "Claude input became unavailable; try again",
                        "missing composer is not reported as silence");
                    Check(harness.WaitTerminalCount(2, 1000), "missing composer recycles owned session");
                    Check(harness.TranscriptCount == 0, "missing composer never delivers fabricated text");
                }
            }

            holding = true;
            List<FileStream> locks = new List<FileStream>();
            try
            {
                using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                    delegate(FakeTerminal terminal, FakeSession session)
                    {
                        terminal.OnSpace = delegate { if (holding) terminal.SetScreen(ListeningScreen("")); };
                        terminal.ExportOnCtrlG = true;
                        terminal.ExportText = "discard me";
                        terminal.ClearOnExport = true;
                        terminal.OnCtrlG = delegate {
                            locks.Add(new FileStream(terminal.ExportPath, FileMode.Open, FileAccess.Read, FileShare.Read));
                        };
                    }))
                {
                    int warnings = 0;
                    harness.Engine.Warning += delegate { Interlocked.Increment(ref warnings); };
                    harness.Engine.Warm();
                    Check(harness.WaitState(EngineState.Ready, 3000), "locked discard starts ready");
                    int id = harness.Engine.BeginTake();
                    Check(harness.WaitState(EngineState.Recording, 3000), "locked discard records");
                    holding = false;
                    FakeTerminal terminal = harness.TerminalAt(0);
                    terminal.SetScreen(ReadyScreen("discard me"));
                    harness.Engine.CancelTake(id);
                    Check(harness.WaitTakeDiscard(id, 3000), "locked discard ends take");
                    Check(harness.WaitTerminalCount(2, 3000), "locked discard recycles blocked session");
                    Check(terminal.IsDisposed, "locked discard closes owned child");
                    Check(harness.TranscriptCount == 0, "locked discard never delivers text");
                    Check(warnings == 1, "recoverable cleanup warning has a separate notification channel");
                    CloseAll(locks);
                }
            }
            finally { CloseAll(locks); }
            Console.WriteLine("PASS: Empty exports, transient composer redraws and locked discard handoffs.");
        }

        private static void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }
            _assertions++;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(5);
            }
            return condition();
        }

        private static string Fixture(string name)
        {
            return File.ReadAllText(Path.Combine("tests", "App", "fixtures", name));
        }

        private static string ReadyScreen(string composer)
        {
            return "working\n" + Rule + "\n\u276f " + composer + "\n" + Rule + "\n";
        }

        private static string ListeningScreen(string composer)
        {
            return "working\n  listening...\n" + Rule + "\n\u276f " +
                (composer.Length == 0 ? "\u2581" : composer) + "\n" + Rule + "\n";
        }

        private static string UnknownScreen()
        {
            return "starting claude\n";
        }

        private static void ScreenParserTests()
        {
            ScreenReading ready = ScreenParser.Read(Fixture("ready.txt"));
            Check(ready.Kind == ScreenKind.Ready, "ready fixture kind");
            Check(ready.HasComposer, "ready fixture composer");
            Check(ready.ComposerText == "", "ready fixture empty composer");

            ScreenReading plain = ScreenParser.Read(Fixture("ready-plain.txt"));
            Check(plain.Kind == ScreenKind.Ready, "ready-plain fixture kind");
            Check(plain.HasComposer, "ready-plain fixture composer");
            Check(plain.ComposerText == "", "ready-plain fixture empty composer");

            ScreenReading listening = ScreenParser.Read(Fixture("listening.txt"));
            Check(listening.Kind == ScreenKind.Listening, "listening fixture kind");
            Check(listening.HasComposer, "listening fixture composer");
            Check(listening.ComposerText == "", "listening cursor is empty");

            ScreenReading finalized = ScreenParser.Read(Fixture("finalized.txt"));
            Check(finalized.Kind == ScreenKind.Ready, "finalized fixture kind");
            Check(finalized.HasComposer, "finalized fixture composer");
            Check(finalized.ComposerText.StartsWith("This is the beginning", StringComparison.Ordinal),
                "finalized starts with opening sentence");
            Check(finalized.ComposerText.EndsWith("murmur dictation test.", StringComparison.Ordinal),
                "finalized ends with closing sentence");
            Check(finalized.ComposerText.IndexOf("listening", StringComparison.OrdinalIgnoreCase) < 0,
                "finalized is not listening");

            ScreenReading login = ScreenParser.Read("Welcome\nNot logged in. Please run claude /login again.\n");
            Check(login.Kind == ScreenKind.NeedsSignIn, "sign-in screen");
            Check(!login.HasComposer, "sign-in has no composer");
            Check(login.ComposerText == "", "sign-in composer text never null");

            ScreenReading apiKey = ScreenParser.Read("Invalid API key. Use /login.\n");
            Check(apiKey.Kind == ScreenKind.NeedsSignIn, "invalid API key screen");

            ScreenReading trust = ScreenParser.Read("Do you trust the files in this folder?\nYes, proceed\n");
            Check(trust.Kind == ScreenKind.NeedsSetup, "trust screen");

            ScreenReading newTrust = ScreenParser.Read(" Quick safety check: Is this a project you created or one you trust?\n\n ❯ No, exit\n   Yes, I trust this folder\n\n Enter to confirm · Esc to cancel\n");
            Check(newTrust.Kind == ScreenKind.NeedsSetup, "2.1.317+ trust screen");

            ScreenReading style = ScreenParser.Read("Choose the text style\n");
            Check(style.Kind == ScreenKind.NeedsSetup, "theme screen");

            ScreenReading enter = ScreenParser.Read("Press Enter to continue\n");
            Check(enter.Kind == ScreenKind.NeedsSetup, "continue screen");

            ScreenReading unknown = ScreenParser.Read("just some output\n");
            Check(unknown.Kind == ScreenKind.Unknown, "unknown screen");
            Check(!unknown.HasComposer, "unknown has no composer");
            Check(unknown.ComposerText == "", "unknown composer text never null");

            ScreenReading multiline = ScreenParser.Read(
                "header\n" + Rule + "   \n\u276f alpha\n  beta\n\n   gamma   \n" + Rule + "\n");
            Check(multiline.Kind == ScreenKind.Ready, "multiline kind");
            Check(multiline.HasComposer, "multiline composer");
            Check(multiline.ComposerText == "alpha beta gamma", "multiline composer joins lines");

            ScreenReading cursor = ScreenParser.Read("header\n" + Rule + "\n\u276f \u2581\n" + Rule + "\n");
            Check(cursor.ComposerText == "", "cursor glyph alone is empty");

            ScreenReading noRules = ScreenParser.Read("header\n\u276f draft\n");
            Check(!noRules.HasComposer, "composer needs rules");
            Check(noRules.Kind == ScreenKind.Unknown, "prompt without rules is unknown");

            // Dictated text may contain "listening"; only status lines outside
            // the composer block may confirm the listening state.
            ScreenReading dictated = ScreenParser.Read(ReadyScreen("I was listening..."));
            Check(dictated.Kind == ScreenKind.Ready, "dictated listening text stays ready");
            Check(dictated.ComposerText == "I was listening...", "dictated listening text preserved");

            ScreenReading dictatedEllipsis = ScreenParser.Read(ReadyScreen("I was listening\u2026"));
            Check(dictatedEllipsis.Kind == ScreenKind.Ready, "dictated listening ellipsis stays ready");

            ScreenReading dictatedMultiline = ScreenParser.Read(
                "working\n" + Rule + "\n\u276f I was\n  listening... to the radio\n" + Rule + "\n");
            Check(dictatedMultiline.Kind == ScreenKind.Ready, "multiline dictated listening stays ready");
            Check(dictatedMultiline.ComposerText == "I was listening... to the radio",
                "multiline dictated listening preserved");

            // Composer contents are ordinary dictation: they must not be
            // mistaken for sign-in or setup prompts.
            ScreenReading runTests = ScreenParser.Read(ReadyScreen("Please run the tests"));
            Check(runTests.Kind == ScreenKind.Ready, "composer Please run stays ready");
            Check(runTests.ComposerText == "Please run the tests", "composer Please run preserved");

            ScreenReading loginText = ScreenParser.Read(ReadyScreen("/login"));
            Check(loginText.Kind == ScreenKind.Ready, "composer /login stays ready");
            Check(loginText.ComposerText == "/login", "composer /login preserved");

            ScreenReading enterText = ScreenParser.Read(ReadyScreen("Press Enter to continue"));
            Check(enterText.Kind == ScreenKind.Ready, "composer Press Enter stays ready");
            Check(enterText.ComposerText == "Press Enter to continue",
                "composer Press Enter preserved");

            ScreenReading multilineSetup = ScreenParser.Read(
                "working\n" + Rule + "\n\u276f please run\n  /login and Press Enter to continue\n" + Rule + "\n");
            Check(multilineSetup.Kind == ScreenKind.Ready, "multiline composer prompt phrases stay ready");
            Check(multilineSetup.ComposerText == "please run /login and Press Enter to continue",
                "multiline composer prompt phrases preserved");

            Console.WriteLine("PASS: ScreenParser fixtures and synthetic screens.");
        }

        private static void SessionTests()
        {
            string root = Path.GetFullPath(Path.Combine(".test-output", "tmp",
                "engine-profile-" + Guid.NewGuid().ToString("N")));
            Func<string> previous = IsolatedProfile.RootOverride;
            IsolatedProfile.RootOverride = delegate { return root; };
            try
            {
                string sessionDirectory = "";
                using (ProfileSession session = new ProfileSession())
                {
                    Check(session.Workspace.Length > 0, "profile workspace");
                    Check(Path.IsPathRooted(session.ExportPath), "profile export path");
                    Check(session.Arguments != null && session.Arguments.Length > 0, "profile arguments");
                    Check(session.Environment["MURMUR_CLEAR_PROMPT"] == "1", "clear prompt override");
                    Check(session.Environment["MURMUR_HANDOFF_PATH"] == session.ExportPath, "handoff override");
                    sessionDirectory = Path.GetDirectoryName(session.ExportPath);
                }
                Check(sessionDirectory.Length > 0, "profile session directory");
                Check(!Directory.Exists(sessionDirectory), "profile disposes owned session files");
                Console.WriteLine("PASS: ProfileSession environment and cleanup.");
            }
            finally
            {
                IsolatedProfile.RootOverride = previous;
                TryDelete(root);
            }
        }

        private static void HostTests()
        {
            OwnedTerminalHost host = new OwnedTerminalHost();
            try
            {
                Action<string> handler = delegate(string value) { };
                host.Output += handler;
                host.Closed += handler;
                host.Output -= handler;
                host.Closed -= handler;
                Check(!host.TryWrite("x"), "never-started host rejects writes");
                Console.WriteLine("PASS: OwnedTerminalHost wrapper.");
            }
            finally
            {
                host.Dispose();
            }
        }

        private static void EngineReadyTests()
        {
            int testThread = Thread.CurrentThread.ManagedThreadId;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); }, null))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "warm reaches ready");
                Check(harness.TerminalCount == 1, "one terminal started");
                Check(harness.SessionCount == 1, "one session started");
                Check(harness.TerminalAt(0).WriteCount == 0, "startup writes no keys");
                Check(harness.HasEventOnOtherThread(testThread), "state events use the engine thread");
                Check(harness.DiagnosticCount == 0, "no diagnostics on clean start");
            }
            Console.WriteLine("PASS: Warm reaches Ready without input.");
        }

        private static void EngineTakeTests()
        {
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "\ufeff  Hello from Murmur.  \r\n";
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Options.StableMs = 40;
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "take setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "listening confirms recording");

                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                FakeSession session = harness.SessionAt(0);
                terminal.SetScreen(ReadyScreen("Hello from Murmur."));
                harness.Engine.EndTake();
                Check(harness.WaitState(EngineState.Finalizing, 2000), "end take finalizes");
                Check(harness.WaitTranscriptCount(1, 3000), "export raises transcript");
                Check(harness.TranscriptAt(0) == "Hello from Murmur.", "transcript is trimmed export");
                Check(harness.WaitState(EngineState.Ready, 3000), "clear composer returns ready");
                Check(!File.Exists(session.ExportPath), "export file deleted");
                Check(harness.FailureCount == 0, "successful take has no failure");

                holding = true;
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "second take records");
                holding = false;
                terminal.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake();
                Check(harness.WaitState(EngineState.Ready, 5000), "second take cancels");
                Check(harness.WaitDiscardedCount(1, 1000), "second take raises discarded once");
                Check(harness.TranscriptCount == 1, "cancel does not add transcript");
            }
            Console.WriteLine("PASS: Listening, finalize, export and second take.");
        }

        private static void EngineFailureTests()
        {
            bool speechHolding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (speechHolding) terminal.SetScreen(ListeningScreen(""));
                    };
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "no-speech setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "no-speech records");
                speechHolding = false;
                harness.TerminalAt(0).SetScreen(ReadyScreen(""));
                harness.Engine.EndTake();
                Check(harness.WaitFailure("No speech detected", 3000), "no speech failure");
                Check(harness.WaitState(EngineState.Ready, 2000), "no speech returns ready");
                Check(harness.TranscriptCount == 0, "no speech has no transcript");
            }

            using (Harness harness = new Harness(delegate { return ReadyScreen(""); }, null))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "listen timeout setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitFailure("Microphone did not start", 3000), "listen timeout failure");
                Check(harness.WaitState(EngineState.Ready, 2000), "listen timeout returns ready");
                FakeTerminal terminal = harness.TerminalAt(0);
                int writes = terminal.WriteCount;
                Thread.Sleep(100);
                Check(terminal.WriteCount == writes, "listen timeout stops held spaces");
                Check(!terminal.ContainsWrite("\x07"), "listen timeout does not export");
            }

            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "partial words";
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "cancel setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "cancel records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                FakeSession session = harness.SessionAt(0);
                harness.Engine.CancelTake();
                Check(harness.WaitState(EngineState.Finalizing, 1000),
                    "cancel enters quiet before late text");
                terminal.SetScreen(ReadyScreen("partial words"));
                Check(WaitUntil(delegate
                {
                    return terminal.ContainsWrite("\x07") && !File.Exists(session.ExportPath);
                }, 3000), "cancel discards through export");
                Check(harness.WaitState(EngineState.Ready, 2000), "cancel returns ready");
                Check(harness.WaitDiscardedCount(1, 2000), "cancel raises discarded once");
                Thread.Sleep(100);
                Check(harness.TranscriptCount == 0, "cancel never raises transcript");
            }

            Console.WriteLine("PASS: No speech, listen timeout and cancel.");
        }

        private static void EngineLifecycleTests()
        {
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate { terminal.SetScreen(ListeningScreen("")); };
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "closed setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "closed records");
                FakeTerminal terminal = harness.TerminalAt(0);
                FakeSession session = harness.SessionAt(0);
                terminal.RaiseClosed("terminal exited with code 1");
                Check(harness.WaitState(EngineState.Faulted, 3000), "closed faults");
                Check(harness.WaitFailure("Claude closed unexpectedly", 2000), "closed failure message");
                Check(terminal.IsDisposed, "closed disposes terminal");
                Check(session.Disposed, "closed disposes session");
                harness.Engine.Warm();
                Check(harness.WaitTerminalCount(2, 3000), "warm restarts after close");
                Check(harness.WaitState(EngineState.Ready, 3000), "restart reaches ready");
            }

            bool pendingHolding = true;
            int terminalStarts = 0;
            using (Harness harness = new Harness(
                delegate
                {
                    terminalStarts++;
                    return terminalStarts == 1 ? UnknownScreen() : ReadyScreen("");
                },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (pendingHolding) terminal.SetScreen(ListeningScreen(""));
                    };
                }))
            {
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Starting, 3000), "begin during starting");
                Check(harness.WaitTerminalCount(1, 2000), "starting terminal exists");
                FakeTerminal terminal = harness.TerminalAt(0);
                Thread.Sleep(100);
                Check(harness.Engine.State == EngineState.Starting, "no recording before ready");
                Check(terminal.WriteCount == 0, "no keys before ready");
                terminal.SetScreen(ReadyScreen(""));
                Check(harness.WaitState(EngineState.Recording, 3000), "pending take begins when ready");
                pendingHolding = false;
                terminal.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake();
                Check(harness.WaitState(EngineState.Ready, 5000), "pending take cancels");
            }

            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen("old draft"); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "old draft";
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "draft setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 15000), "draft take records");
                FakeTerminal terminal = harness.TerminalAt(0);
                Check(terminal.WriteAt(0) == "\x07", "existing composer exported first");
                Check(harness.TranscriptCount == 0, "draft export is not a transcript");
                holding = false;
                terminal.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake();
                Check(harness.WaitState(EngineState.Ready, 5000), "draft cancel returns ready");
                Check(harness.WaitDiscardedCount(1, 2000), "draft cancel raises discarded");
            }

            using (Harness harness = new Harness(delegate { return ReadyScreen(""); }, null))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "unload setup ready");
                FakeTerminal terminal = harness.TerminalAt(0);
                FakeSession session = harness.SessionAt(0);
                harness.Engine.Unload();
                Check(harness.WaitState(EngineState.Stopped, 2000), "unload stops");
                Check(terminal.IsDisposed, "unload disposes terminal");
                Check(session.Disposed, "unload disposes session");
                harness.Engine.Warm();
                Check(harness.WaitTerminalCount(2, 3000), "warm after unload");
                Check(harness.WaitState(EngineState.Ready, 3000), "warm after unload ready");
            }

            bool recycleHolding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (recycleHolding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "hello";
                    terminal.ClearOnExport = false;
                }))
            {
                harness.Options.StableMs = 40;
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "recycle setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "recycle records");
                recycleHolding = false;
                FakeTerminal first = harness.TerminalAt(0);
                FakeSession firstSession = harness.SessionAt(0);
                first.SetScreen(ReadyScreen("final words"));
                harness.Engine.EndTake();
                Check(harness.WaitTranscriptCount(1, 3000), "recycle export transcript");
                Check(harness.WaitTerminalCount(2, 7000), "uncleared composer recycles child");
                Check(first.IsDisposed, "recycle disposes old terminal");
                Check(firstSession.Disposed, "recycle disposes old session");
                Check(harness.DiagnosticCount > 0, "recycle emits diagnostic");
                Check(harness.WaitState(EngineState.Ready, 3000), "recycled child ready");
            }

            using (Harness harness = new Harness(delegate { return ReadyScreen("old draft"); }, null))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "closed discard setup ready");
                FakeTerminal terminal = harness.TerminalAt(0);
                harness.Engine.BeginTake();
                Check(WaitUntil(delegate { return terminal.ContainsWrite("\x07"); }, 2000),
                    "closed discard export requested");
                terminal.RaiseClosed("terminal exited with code 1");
                Check(harness.WaitState(EngineState.Faulted, 3000), "closed during discard faults");
                Check(harness.WaitFailure("Claude closed unexpectedly", 2000),
                    "closed during discard failure");
            }

            using (Harness harness = new Harness(delegate { return UnknownScreen(); }, null))
            {
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Starting, 2000), "closed pending setup starting");
                Check(harness.WaitTerminalCount(1, 2000), "closed pending terminal exists");
                harness.TerminalAt(0).RaiseClosed("terminal exited with code 1");
                Check(harness.WaitFailure("Claude closed unexpectedly", 2000),
                    "closed during pending take failure");
            }

            Console.WriteLine("PASS: Closed, starting take, stale composer, unload and recycle.");
        }

        private static void EngineCancelQuietTests()
        {
            // The cancelled audio may be finalized by the CLI only after CancelTake.
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "late finalized words";
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "cancel-quiet setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "cancel-quiet records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                FakeSession session = harness.SessionAt(0);
                harness.Engine.CancelTake();
                Check(harness.WaitState(EngineState.Finalizing, 1000),
                    "cancel stays finalizing while quiet");
                terminal.SetScreen(ReadyScreen("late finalized words"));
                Check(harness.WaitDiscardedCount(1, 3000), "late finalize raises discarded");
                Check(harness.WaitState(EngineState.Ready, 3000), "cancel quiet returns ready");
                Check(harness.TranscriptCount == 0, "late finalize never raises transcript");
                Check(harness.FailureCount == 0, "late finalize has no failure");
                Check(terminal.ContainsWrite("\x07"), "late finalize exported for discard");
                Check(!File.Exists(session.ExportPath), "late handoff deleted");
                Thread.Sleep(100);
                Check(harness.DiscardedCount == 1, "cancel discarded exactly once");
            }

            // A screen that never settles hits the FinalizeTimeoutMs deadline.
            bool stuck = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (stuck) terminal.SetScreen(ListeningScreen(""));
                    };
                }))
            {
                harness.Options.FinalizeTimeoutMs = 400;
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "cancel deadline setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "cancel deadline records");
                stuck = false;
                harness.Engine.CancelTake();
                Check(harness.WaitDiscardedCount(1, 3000), "cancel deadline raises discarded");
                Check(harness.WaitTerminalCount(2, 3000), "cancel deadline recycles child");
                Check(harness.WaitState(EngineState.Ready, 3000), "recycled child reaches ready");
                Check(harness.TranscriptCount == 0, "cancel deadline has no transcript");
            }

            Console.WriteLine("PASS: Cancel-quiet late finalization and deadline recycle.");
        }

        private static void EngineStopPhaseTests()
        {
            // EndTake during the Listening phase is a normal stop, not a cancel.
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "quick words";
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Options.ListenTimeoutMs = 5000;
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "listening stop setup ready");
                harness.Engine.BeginTake();
                FakeTerminal terminal = harness.TerminalAt(0);
                Check(WaitUntil(delegate { return terminal.WriteCount > 0; }, 2000),
                    "hold starts before listening");
                Check(harness.Engine.State == EngineState.Ready, "listening not observed yet");
                terminal.SetScreen(ReadyScreen("quick words"));
                harness.Engine.EndTake();
                Check(harness.WaitTranscriptCount(1, 3000), "listening stop finalizes normally");
                Check(harness.TranscriptAt(0) == "quick words", "listening stop export text");
                Check(harness.WaitState(EngineState.Ready, 3000), "listening stop returns ready");
                Check(harness.FailureCount == 0, "listening stop has no failure");
                Check(harness.DiscardedCount == 0, "listening stop is not discarded");
            }

            // EndTake during Starting cancels the pending take and raises Discarded.
            using (Harness harness = new Harness(delegate { return UnknownScreen(); }, null))
            {
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Starting, 2000), "start end goes starting");
                Check(harness.WaitTerminalCount(1, 2000), "start end has terminal");
                FakeTerminal terminal = harness.TerminalAt(0);
                harness.Engine.EndTake();
                Check(harness.WaitDiscardedCount(1, 2000), "end during starting raises discarded");
                terminal.SetScreen(ReadyScreen(""));
                Check(harness.WaitState(EngineState.Ready, 3000), "cancelled start settles ready");
                Thread.Sleep(100);
                Check(terminal.WriteCount == 0, "cancelled start writes no keys");
                Check(harness.TranscriptCount == 0, "cancelled start has no transcript");
                Check(harness.FailureCount == 0, "cancelled start has no failure");
                Check(harness.DiscardedCount == 1, "cancelled start discarded exactly once");
            }

            // EndTake during a stale-composer discard also raises Discarded once.
            using (Harness harness = new Harness(delegate { return ReadyScreen("stale draft"); }, null))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "stale discard setup ready");
                FakeTerminal terminal = harness.TerminalAt(0);
                harness.Engine.BeginTake();
                Check(WaitUntil(delegate { return terminal.ContainsWrite("\x07"); }, 2000),
                    "stale draft exported");
                harness.Engine.EndTake();
                Check(harness.WaitDiscardedCount(1, 2000), "end during stale discard raises discarded");
                Check(harness.WaitState(EngineState.Ready, 3000), "stale discard settles ready");
                Thread.Sleep(100);
                Check(harness.DiscardedCount == 1, "stale discard discarded exactly once");
                Check(terminal.WriteAt(0) == "\x07", "stale discard writes export first");
                Check(terminal.WriteCount == 1, "stale discard does not start hold");
                Check(harness.TranscriptCount == 0, "stale discard has no transcript");
            }

            Console.WriteLine("PASS: Stop during listening, starting and stale discard.");
        }

        private static void EngineExportSafetyTests()
        {
            // A handoff older than the request minus 2 s must never be used.
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "stale handoff";
                    terminal.ExportTimestampUtc = DateTime.UtcNow.AddSeconds(-30);
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "stale handoff setup ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "stale handoff records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("fresh words"));
                harness.Engine.EndTake(takeId);
                Check(harness.WaitFailure("Could not export the transcript", 3000),
                    "stale handoff fails export");
                Check(harness.WaitTakeFailure(takeId, 2000), "stale handoff fails the take");
                Check(harness.TakeFailureText(takeId) == "Could not export the transcript",
                    "stale handoff take failure text");
                Check(harness.TranscriptCount == 0, "stale handoff never raises transcript");
                Check(harness.WaitTerminalCount(2, 3000), "stale handoff recycles child");
                Check(harness.TerminalAt(0).IsDisposed, "stale handoff disposes old child");
                Check(harness.SessionAt(0).Disposed, "stale handoff disposes old session");
                Check(harness.WaitState(EngineState.Ready, 3000), "stale handoff returns ready");
            }

            // A failed discard-before-take export recycles the child and then
            // begins the take on the fresh child instead of failing it.
            List<FileStream> locks = new List<FileStream>();
            try
            {
                int screens = 0;
                using (Harness harness = new Harness(
                    delegate
                    {
                        screens++;
                        return screens == 1 ? ReadyScreen("stale draft") : ReadyScreen("");
                    },
                    delegate(FakeTerminal terminal, FakeSession session)
                    {
                        terminal.ExportOnCtrlG = true;
                        terminal.ClearOnExport = true;
                        terminal.OnSpace = delegate { terminal.SetScreen(ListeningScreen("")); };
                        if (screens == 1)
                        {
                            locks.Add(new FileStream(session.ExportPath, FileMode.Create,
                                FileAccess.ReadWrite, FileShare.None));
                        }
                    }))
                {
                    harness.Engine.Warm();
                    Check(harness.WaitState(EngineState.Ready, 3000), "locked handoff setup ready");
                    int takeId = harness.Engine.BeginTake();
                    Check(harness.WaitTerminalCount(2, 3000), "locked stale handoff recycles child");
                    Check(harness.WaitState(EngineState.Recording, 3000),
                        "locked stale handoff begins take on fresh child");
                    Check(harness.WaitTakeRecording(takeId, 2000),
                        "locked stale handoff fresh child records");
                    Check(harness.FailureCount == 0, "locked stale handoff does not fail the take");
                    Check(harness.CountTakeFailures(takeId) == 0,
                        "locked stale handoff no take failure");
                    Check(harness.TerminalAt(0).IsDisposed, "locked stale handoff disposes old child");
                    Check(harness.SessionAt(0).Disposed, "locked stale handoff disposes old session");
                    CloseAll(locks);
                }
            }
            finally
            {
                CloseAll(locks);
            }

            // Failing to delete the handoff after reading still raises the
            // transcript first, then recycles the child.
            bool readHolding = true;
            List<FileStream> readLocks = new List<FileStream>();
            try
            {
                using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                    delegate(FakeTerminal terminal, FakeSession session)
                    {
                        terminal.OnSpace = delegate
                        {
                            if (readHolding) terminal.SetScreen(ListeningScreen(""));
                        };
                        terminal.ExportOnCtrlG = true;
                        terminal.ExportText = "delete me";
                        terminal.ClearOnExport = true;
                        terminal.OnCtrlG = delegate
                        {
                            readLocks.Add(new FileStream(terminal.ExportPath, FileMode.Open,
                                FileAccess.Read, FileShare.Read));
                        };
                    }))
                {
                    harness.Engine.Warm();
                    Check(harness.WaitState(EngineState.Ready, 3000), "locked read setup ready");
                    harness.Engine.BeginTake();
                    Check(harness.WaitState(EngineState.Recording, 3000), "locked read records");
                    readHolding = false;
                    FakeTerminal terminal = harness.TerminalAt(0);
                    terminal.SetScreen(ReadyScreen("delete me"));
                    harness.Engine.EndTake();
                    Check(harness.WaitTranscriptCount(1, 3000), "locked read raises transcript");
                    Check(harness.TranscriptAt(0) == "delete me", "locked read transcript text");
                    Check(harness.WaitTerminalCount(2, 3000), "locked read recycles child");
                    Check(harness.WaitState(EngineState.Ready, 3000), "locked read recycled ready");
                    Check(harness.DiscardedCount == 0, "locked read is not discarded");
                    CloseAll(readLocks);
                }
            }
            finally
            {
                CloseAll(readLocks);
            }

            Console.WriteLine("PASS: Export freshness, stale delete failure and handoff cleanup.");
        }

        private static void EngineFaultTests()
        {
            bool throwing = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.ThrowOnWrite = throwing;
                    terminal.OnSpace = delegate { terminal.SetScreen(ListeningScreen("")); };
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "fault setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Faulted, 3000), "handler exception faults engine");
                Check(harness.WaitFailure("Murmur engine error", 2000), "handler exception failure");
                Check(harness.HasDiagnostic("InvalidOperationException"),
                    "handler exception diagnostic is the type");
                Check(harness.DiagnosticCount == 1, "handler exception one diagnostic");
                Check(!harness.HasDiagnostic("fake terminal write failure"),
                    "diagnostic never carries exception text");
                Check(harness.TerminalAt(0).IsDisposed, "handler exception disposes child");

                throwing = false;
                harness.Engine.Warm();
                Check(harness.WaitTerminalCount(2, 3000), "warm after handler exception restarts");
                Check(harness.WaitState(EngineState.Ready, 3000), "warm after handler exception ready");
                harness.Engine.BeginTake();
                Check(WaitUntil(delegate { return harness.TerminalAt(1).WriteCount > 0; }, 2000),
                    "begin after handler exception still works");
                harness.Engine.Unload();
                Check(harness.WaitState(EngineState.Stopped, 2000),
                    "unload after handler exception still works");
            }

            Console.WriteLine("PASS: Handler exceptions fault without stopping the engine thread.");
        }

        private static void EnginePromptSafetyTests()
        {
            const string SignIn = "Sign in needed \u2014 open the tray menu and choose Sign in";
            const string Setup = "Claude needs one-time setup \u2014 open the tray menu and choose Account, then Run Claude first-time setup";

            using (Harness harness = new Harness(delegate { return ReadyScreen(""); }, null))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "sign-in begin setup ready");
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen("Not logged in. Please run claude /login again.\n");
                Thread.Sleep(100);
                harness.Engine.BeginTake();
                Check(harness.WaitFailure(SignIn, 2000), "begin on sign-in raises sign-in failure");
                Check(harness.WaitState(EngineState.Faulted, 2000), "begin on sign-in faults");
                Check(terminal.WriteCount == 0, "begin on sign-in writes no keys");
                Check(harness.TranscriptCount == 0, "begin on sign-in has no transcript");
            }

            using (Harness harness = new Harness(delegate { return ReadyScreen(""); }, null))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "setup begin setup ready");
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen("Do you trust the files in this folder?\nYes, proceed\n");
                Thread.Sleep(100);
                harness.Engine.BeginTake();
                Check(harness.WaitFailure(Setup, 2000), "begin on setup raises setup failure");
                Check(harness.WaitState(EngineState.Faulted, 2000), "begin on setup faults");
                Check(terminal.WriteCount == 0, "begin on setup writes no keys");
            }

            using (Harness harness = new Harness(delegate { return ReadyScreen(""); }, null))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "unknown begin setup ready");
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen("starting claude\n");
                Thread.Sleep(100);
                harness.Engine.BeginTake();
                Check(harness.WaitFailure("Claude did not start", 2000),
                    "begin on unknown raises start failure");
                Check(harness.WaitState(EngineState.Faulted, 2000), "begin on unknown faults");
                Check(terminal.WriteCount == 0, "begin on unknown writes no keys");
            }

            // The post-export clear check must also see a safe ready screen.
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "hello";
                    terminal.ScreenAfterExport = "Do you trust the files in this folder?\nYes, proceed\n";
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "clear check setup ready");
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "clear check records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("hello"));
                harness.Engine.EndTake();
                Check(harness.WaitTranscriptCount(1, 3000), "clear check still exports transcript");
                Check(harness.WaitFailure(Setup, 3000), "uncleared safe prompt raises setup failure");
                Check(harness.WaitState(EngineState.Faulted, 3000), "uncleared safe prompt faults");
            }

            Console.WriteLine("PASS: Prompt safety before holds and after export.");
        }

        private static void EngineTakeIdTests()
        {
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "\ufeff  id words  \r\n";
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "take id setup ready");

                int first = harness.Engine.BeginTake();
                Check(first > 0, "begin take returns a positive id");
                Check(harness.WaitState(EngineState.Recording, 3000), "take id records");
                Check(harness.WaitTakeRecording(first, 2000), "take recording id");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("id words"));
                harness.Engine.EndTake(first);
                Check(harness.WaitTakeTranscript(first, 3000), "take transcript event");
                Check(harness.TakeTranscriptText(first) == "id words", "take transcript text trimmed");
                Check(harness.WaitState(EngineState.Ready, 3000), "take id returns ready");
                Check(harness.CountTakeTerminalEvents(first) == 1, "take has one terminal event");
                Check(harness.TakeFailureCount == 0, "successful take has no failure event");
                Check(harness.TakeDiscardCount == 0, "successful take is not discarded");

                holding = true;
                int second = harness.Engine.BeginTake();
                Check(second > first, "second take id increases");
                Check(harness.WaitState(EngineState.Recording, 3000), "second take records");
                terminal.SetScreen(ReadyScreen(""));
                holding = false;
                harness.Engine.CancelTake(second);
                Check(harness.WaitTakeDiscard(second, 3000), "cancelled take discards its id");
                Check(harness.WaitState(EngineState.Ready, 3000), "cancelled take returns ready");
                Check(harness.CountTakeTerminalEvents(second) == 1, "cancel one terminal event");

                holding = true;
                int third = harness.Engine.BeginTake();
                Check(third > second, "third take id increases");
                Check(harness.WaitState(EngineState.Recording, 3000), "third take records");
                terminal.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake(first);
                harness.Engine.EndTake(second);
                Thread.Sleep(100);
                Check(harness.Engine.State == EngineState.Recording, "stale take ids are ignored");
                holding = false;
                terminal.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake(third);
                Check(harness.WaitTakeDiscard(third, 3000), "third take discards");
                Check(harness.CountTakeTerminalEvents(third) == 1, "third take one terminal event");
                Check(harness.CountTakeDiscards(second) == 1, "second take discarded once");
            }

            // A warm-only startup failure is not tied to a take.
            using (Harness harness = new Harness(
                delegate { return "Not logged in. Please run claude /login again.\n"; }, null))
            {
                harness.Engine.Warm();
                Check(harness.WaitFailure(
                    "Sign in needed \u2014 open the tray menu and choose Sign in", 3000),
                    "warm-only sign-in failure");
                Thread.Sleep(100);
                Check(harness.TakeFailureCount == 0, "warm-only failure has no take event");
            }

            // A startup failure while a take is pending fails that take id.
            using (Harness harness = new Harness(
                delegate { return "Not logged in. Please run claude /login again.\n"; }, null))
            {
                int pending = harness.Engine.BeginTake();
                Check(harness.WaitTakeFailure(pending, 3000),
                    "startup failure fails the pending take");
                Check(harness.TakeFailureText(pending) ==
                    "Sign in needed \u2014 open the tray menu and choose Sign in",
                    "pending take startup failure message");
                Check(harness.CountTakeTerminalEvents(pending) == 1,
                    "pending take one terminal event");
            }

            Console.WriteLine("PASS: Take ids allocate, record, complete and ignore stale ids.");
        }

        private static void EngineBusyBeginTests()
        {
            // BeginTake during Finalizing is queued; the older take is discarded.
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "first words";
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "busy begin setup ready");
                int first = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "busy begin first records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("first words"));
                harness.Engine.EndTake(first);
                Check(harness.WaitState(EngineState.Finalizing, 2000), "busy begin first finalizing");
                int second = harness.Engine.BeginTake();
                Check(second > first, "busy begin allocates a new id");
                Check(harness.WaitTakeDiscard(first, 2000), "superseded finalizing take discards");
                holding = true;
                Check(harness.WaitState(EngineState.Recording, 4000),
                    "queued take starts after finalizing");
                Check(harness.WaitTakeRecording(second, 2000), "queued take records");
                Check(harness.CountTakeTranscripts(first) == 0, "superseded take has no transcript");
                holding = false;
                terminal.ExportText = "second words";
                terminal.SetScreen(ReadyScreen("second words"));
                harness.Engine.EndTake(second);
                Check(harness.WaitTakeTranscript(second, 3000), "queued take transcript");
                Check(harness.TakeTranscriptText(second) == "second words", "queued take text");
                Check(harness.CountTakeTerminalEvents(first) == 1, "superseded take one event");
            }

            // BeginTake during ExportWait is queued; the older id is discarded
            // and the export timeout recycles before the queued take starts.
            bool exportHolding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (exportHolding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = false;
                }))
            {
                harness.Options.ExportTimeoutMs = 400;
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "export-wait begin setup ready");
                int first = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "export-wait begin records");
                exportHolding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("export words"));
                harness.Engine.EndTake(first);
                Check(WaitUntil(delegate { return terminal.ContainsWrite("\x07"); }, 2000),
                    "export-wait begin export requested");
                int second = harness.Engine.BeginTake();
                Check(second > first, "export-wait begin allocates id");
                Check(harness.WaitTakeDiscard(first, 2000), "export-wait superseded take discards");
                exportHolding = true;
                Check(harness.WaitTerminalCount(2, 3000), "export-wait timeout recycles child");
                Check(harness.WaitTakeRecording(second, 3000),
                    "export-wait queued take records on fresh child");
                Check(harness.CountTakeFailures(first) == 0,
                    "export-wait superseded take has no failure");
                exportHolding = false;
                FakeTerminal secondTerminal = harness.TerminalAt(1);
                secondTerminal.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake(second);
                Check(harness.WaitTakeDiscard(second, 3000), "export-wait queued take cancels");
            }

            // BeginTake during ClearWait is queued, not dropped.
            bool clearHolding = true;            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (clearHolding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "clear words";
                    terminal.ClearOnExport = false;
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "clear-wait begin setup ready");
                int first = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "clear-wait begin records");
                clearHolding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("clear words"));
                harness.Engine.EndTake(first);
                Check(harness.WaitTakeTranscript(first, 3000), "clear-wait transcript");
                int second = harness.Engine.BeginTake();
                Check(second > first, "clear-wait begin allocates id");
                Check(harness.Engine.State == EngineState.Finalizing, "clear-wait still finalizing");
                clearHolding = true;
                terminal.SetScreen(ReadyScreen(""));
                Check(harness.WaitState(EngineState.Recording, 3000),
                    "begin during clear wait starts the new take");
                Check(harness.WaitTakeRecording(second, 2000), "clear-wait queued take records");
                clearHolding = false;
                terminal.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake(second);
                Check(harness.WaitTakeDiscard(second, 3000), "clear-wait queued take cancels");
            }

            Console.WriteLine("PASS: BeginTake queues during finalizing and clear wait.");
        }

        private static void EngineSupersedeRecordingTests()
        {
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "supersede recording setup ready");
                int first = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "supersede first records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen(""));
                int second = harness.Engine.BeginTake();
                Check(second > first, "supersede recording allocates a new id");
                Check(harness.WaitTakeDiscard(first, 2000), "superseded recording take discards");
                holding = true;
                Check(harness.WaitTakeRecording(second, 3000),
                    "supersede recording starts the new take");
                holding = false;
                terminal.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake(second);
                Check(harness.WaitTakeDiscard(second, 3000), "supersede new take cancels");
            }

            Console.WriteLine("PASS: A new BeginTake supersedes an active recording take.");
        }

        private static void EngineHoldGuardTests()
        {
            bool blocking = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (blocking) terminal.SetScreen(ListeningScreen(""));
                    };
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "sign-in hold guard ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "sign-in hold guard records");
                blocking = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen("Not logged in. Please run claude /login again.\n");
                Check(harness.WaitTakeFailure(takeId, 2000), "sign-in during hold fails take");
                Check(harness.TakeFailureText(takeId) ==
                    "Sign in needed \u2014 open the tray menu and choose Sign in",
                    "sign-in hold guard message");
                Check(harness.WaitState(EngineState.Faulted, 2000), "sign-in hold guard faults");
                Check(terminal.IsDisposed, "sign-in hold guard disposes child");
                Check(harness.SessionAt(0).Disposed, "sign-in hold guard disposes session");
                Check(!terminal.ContainsWrite("\x07"), "sign-in hold guard never exports");
                Check(harness.TakeDiscardCount == 0, "sign-in hold guard is not discarded");
            }

            bool setupBlocking = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (setupBlocking) terminal.SetScreen(ListeningScreen(""));
                    };
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "setup hold guard ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "setup hold guard records");
                setupBlocking = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen("Do you trust the files in this folder?\nYes, proceed\n");
                Check(harness.WaitTakeFailure(takeId, 2000), "setup during hold fails take");
                Check(harness.TakeFailureText(takeId) ==
                    "Claude needs one-time setup \u2014 open the tray menu and choose Account, then Run Claude first-time setup",
                    "setup hold guard message");
                Check(harness.WaitState(EngineState.Faulted, 2000), "setup hold guard faults");
                Check(terminal.IsDisposed, "setup hold guard disposes child");
                Check(!terminal.ContainsWrite("\x07"), "setup hold guard never exports");
            }

            Console.WriteLine("PASS: Hold ticks and polls stop on sign-in and setup screens.");
        }

        private static void EngineCancelQuietWindowTests()
        {
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "late words";
                    terminal.ClearOnExport = true;
                }))
            {
                harness.Options.NoSpeechMs = 500;
                harness.Options.FinalizeTimeoutMs = 3000;
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "cancel window setup ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "cancel window records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake(takeId);
                Check(harness.WaitState(EngineState.Finalizing, 1000),
                    "cancel window enters finalizing");
                Thread.Sleep(150);
                Check(harness.Engine.State == EngineState.Finalizing,
                    "cancel quiet waits at least NoSpeechMs");
                terminal.SetScreen(ReadyScreen("late words"));
                Check(WaitUntil(delegate { return terminal.ContainsWrite("\x07"); }, 2000),
                    "late cancel text exports for discard");
                Check(harness.WaitTakeDiscard(takeId, 3000), "late cancel text discards the take");
                Check(harness.WaitState(EngineState.Ready, 3000), "cancel window returns ready");
                Check(harness.TranscriptCount == 0, "late cancel text never raises transcript");
                Check(harness.FailureCount == 0, "late cancel text has no failure");
                Check(harness.DiscardedCount == 1, "late cancel text discards once");
            }

            Console.WriteLine("PASS: Cancel quiet uses the NoSpeechMs window and exports late text.");
        }

        private static void EngineDiscardBeforeTakeTests()
        {
            // A timed-out pre-take discard recycles the child and starts the take.
            int timeoutScreens = 0;
            bool timeoutHolding = true;
            using (Harness harness = new Harness(
                delegate
                {
                    timeoutScreens++;
                    return timeoutScreens == 1 ? ReadyScreen("stale draft") : ReadyScreen("");
                },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.ExportOnCtrlG = false;
                    terminal.OnSpace = delegate
                    {
                        if (timeoutHolding) terminal.SetScreen(ListeningScreen(""));
                    };
                }))
            {
                harness.Options.ExportTimeoutMs = 150;
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "discard timeout setup ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitTerminalCount(2, 3000), "discard timeout recycles child");
                Check(harness.WaitState(EngineState.Recording, 3000),
                    "discard timeout begins take on fresh child");
                Check(harness.WaitTakeRecording(takeId, 2000), "discard timeout fresh take records");
                Check(harness.FailureCount == 0, "discard timeout does not fail the take");
                Check(harness.CountTakeFailures(takeId) == 0, "discard timeout no take failure");
                Check(harness.TerminalAt(0).IsDisposed, "discard timeout disposes old child");
                Check(harness.SessionAt(0).Disposed, "discard timeout disposes old session");
                Check(harness.TerminalAt(0).WriteCount == 1,
                    "discard timeout never holds on the old draft");
                timeoutHolding = false;
                FakeTerminal second = harness.TerminalAt(1);
                second.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake(takeId);
                Check(harness.WaitTakeDiscard(takeId, 3000), "discard timeout take cancels");
            }

            // A composer that stays non-empty after the discard is recycled
            // before the take begins on the fresh child.
            int clearScreens = 0;
            bool clearHolding = true;
            using (Harness harness = new Harness(
                delegate
                {
                    clearScreens++;
                    return clearScreens == 1 ? ReadyScreen("stale draft") : ReadyScreen("");
                },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "stale draft";
                    terminal.ClearOnExport = clearScreens != 1;
                    terminal.OnSpace = delegate
                    {
                        if (clearHolding) terminal.SetScreen(ListeningScreen(""));
                    };
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "discard clear setup ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitTerminalCount(2, 5000),
                    "non-empty composer recycles before the take");
                Check(harness.WaitState(EngineState.Recording, 3000),
                    "non-empty composer begins take on fresh child");
                Check(harness.WaitTakeRecording(takeId, 2000),
                    "non-empty composer fresh take records");
                Check(harness.FailureCount == 0, "non-empty composer does not fail the take");
                Check(harness.CountTakeFailures(takeId) == 0,
                    "non-empty composer no take failure");
                Check(harness.TerminalAt(0).IsDisposed, "non-empty composer disposes old child");
                Check(harness.TerminalAt(0).WriteCount == 1,
                    "non-empty composer never holds on the old draft");
                clearHolding = false;
                FakeTerminal second = harness.TerminalAt(1);
                second.SetScreen(ReadyScreen(""));
                harness.Engine.CancelTake(takeId);
                Check(harness.WaitTakeDiscard(takeId, 3000), "non-empty composer take cancels");
            }

            Console.WriteLine("PASS: Discard-before-take failures recycle the child and keep the take.");
        }

        private static void EngineClearWaitTests()
        {
            // Unknown frames are transient during the post-export clear wait.
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "clear me";
                    terminal.ScreenAfterExport = "starting claude\n";
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "clear wait setup ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "clear wait records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("clear me"));
                harness.Engine.EndTake(takeId);
                Check(harness.WaitTakeTranscript(takeId, 3000), "clear wait transcript");
                Thread.Sleep(150);
                Check(harness.Engine.State == EngineState.Finalizing,
                    "unknown clear frame keeps waiting");
                Check(harness.FailureCount == 0, "unknown clear frame has no failure");
                terminal.SetScreen(ReadyScreen(""));
                Check(harness.WaitState(EngineState.Ready, 3000),
                    "transient clear frame recovers to ready");
            }

            // A clear wait that stays unknown recycles at the 3 s deadline.
            bool stuckHolding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (stuckHolding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "stuck";
                    terminal.ScreenAfterExport = "starting claude\n";
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "stuck clear setup ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "stuck clear records");
                stuckHolding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("stuck"));
                harness.Engine.EndTake(takeId);
                Check(harness.WaitTakeTranscript(takeId, 3000), "stuck clear transcript");
                Check(harness.WaitTerminalCount(2, 5000), "stuck clear recycles at deadline");
                Check(harness.TerminalAt(0).IsDisposed, "stuck clear disposes old child");
                Check(harness.FailureCount == 0, "stuck clear does not fault");
                Check(harness.WaitState(EngineState.Ready, 3000), "stuck clear recycled ready");
            }

            // Sign-in frames still fail fast during the clear wait.
            bool signInHolding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (signInHolding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = true;
                    terminal.ExportText = "sign in";
                    terminal.ScreenAfterExport = "Not logged in. Please run claude /login again.\n";
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "clear sign-in setup ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "clear sign-in records");
                signInHolding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("sign in"));
                harness.Engine.EndTake(takeId);
                Check(harness.WaitTakeTranscript(takeId, 3000), "clear sign-in transcript");
                Check(harness.WaitFailure(
                    "Sign in needed \u2014 open the tray menu and choose Sign in", 2000),
                    "clear sign-in fails fast");
                Check(harness.WaitState(EngineState.Faulted, 2000), "clear sign-in faults");
            }

            Console.WriteLine("PASS: Clear wait tolerates transient frames and fails fast on prompts.");
        }

        private static void EngineExportTimeoutTests()
        {
            // Transcript timeout recycles the child and session before failing.
            bool holding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (holding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = false;
                }))
            {
                harness.Options.ExportTimeoutMs = 150;
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "export timeout setup ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "export timeout records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("lost words"));
                harness.Engine.EndTake(takeId);
                Check(harness.WaitTakeFailure(takeId, 3000), "export timeout fails the take");
                Check(harness.WaitTerminalCount(2, 3000), "export timeout recycles child");
                Check(harness.TerminalAt(0).IsDisposed, "export timeout disposes old child");
                Check(harness.SessionAt(0).Disposed, "export timeout disposes old session");
                Check(harness.WaitState(EngineState.Ready, 3000), "export timeout returns ready");
                Check(harness.TranscriptCount == 0, "export timeout has no transcript");
            }

            // Discard export timeout also recycles before the take is discarded.
            bool cancelHolding = true;
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate
                    {
                        if (cancelHolding) terminal.SetScreen(ListeningScreen(""));
                    };
                    terminal.ExportOnCtrlG = false;
                }))
            {
                harness.Options.ExportTimeoutMs = 150;
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "discard timeout setup ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "discard timeout records");
                cancelHolding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("cancel words"));
                harness.Engine.CancelTake(takeId);
                Check(harness.WaitTakeDiscard(takeId, 3000), "discard timeout discards the take");
                Check(harness.WaitTerminalCount(2, 3000), "discard timeout recycles child");
                Check(harness.TerminalAt(0).IsDisposed, "discard timeout disposes old child");
                Check(harness.SessionAt(0).Disposed, "discard timeout disposes old session");
                Check(harness.TranscriptCount == 0, "discard timeout has no transcript");
            }

            Console.WriteLine("PASS: Export timeouts recycle the child and session.");
        }

        private static void EngineSuspendTests()
        {
            // A Warm queued before Suspend cannot leave a child running.
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); }, null))
            {
                harness.Engine.Warm();
                harness.Engine.Suspend();
                Check(harness.WaitSuspended(3000), "suspend raises suspended");
                Check(harness.Engine.State == EngineState.Stopped, "suspend stops the engine");
                Check(harness.TerminalCount >= 1, "warm before suspend created a child");
                Check(harness.AllTerminalsDisposed(), "suspended event follows child disposal");
                Check(harness.AllSessionsDisposed(), "suspended event follows session disposal");

                int count = harness.TerminalCount;
                harness.Engine.Warm();
                Thread.Sleep(150);
                Check(harness.TerminalCount == count, "warm is ignored while suspended");

                int failedTake = harness.Engine.BeginTake();
                Check(harness.WaitTakeFailure(failedTake, 2000),
                    "begin while suspended fails the take");
                Check(harness.TakeFailureText(failedTake) ==
                    "Finish the Claude sign-in window first",
                    "suspended take failure message");
                Check(harness.CountTakeDiscards(failedTake) == 0,
                    "suspended take is not discarded");
                Thread.Sleep(100);
                Check(harness.TerminalCount == count, "suspended begin starts no child");

                harness.Engine.Resume();
                harness.Engine.Warm();
                Check(harness.WaitTerminalCount(count + 1, 3000), "resume allows warm");
                Check(harness.WaitState(EngineState.Ready, 3000), "resumed child ready");
            }

            // An active take is discarded by Suspend.
            using (Harness harness = new Harness(delegate { return ReadyScreen(""); },
                delegate(FakeTerminal terminal, FakeSession session)
                {
                    terminal.OnSpace = delegate { terminal.SetScreen(ListeningScreen("")); };
                }))
            {
                harness.Engine.Warm();
                Check(harness.WaitState(EngineState.Ready, 3000), "suspend take setup ready");
                int takeId = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "suspend take records");
                harness.Engine.Suspend();
                Check(harness.WaitTakeDiscard(takeId, 2000), "suspend discards the active take");
                Check(harness.WaitSuspended(2000), "suspend raises suspended after take");
                Check(harness.Engine.State == EngineState.Stopped, "suspend take stops");
                Check(harness.TerminalAt(0).IsDisposed, "suspend take disposes child");
                Check(harness.SessionAt(0).Disposed, "suspend take disposes session");
                Check(harness.FailureCount == 0, "suspend is not a failure");
                Check(harness.DiscardedCount == 1, "suspend discards once");
            }

            // A pending take in Starting is discarded by Suspend.
            using (Harness harness = new Harness(delegate { return UnknownScreen(); }, null))
            {
                int pending = harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Starting, 2000), "suspend pending starting");
                harness.Engine.Suspend();
                Check(harness.WaitTakeDiscard(pending, 2000), "suspend discards the pending take");
                Check(harness.WaitSuspended(2000), "suspend pending raises suspended");
                Check(harness.Engine.State == EngineState.Stopped, "suspend pending stops");
                Check(harness.TerminalAt(0).IsDisposed, "suspend pending disposes child");
            }

            Console.WriteLine("PASS: Suspend cancels takes, disposes the child and blocks Warm.");
        }
        private static void EngineWriteSafetyTests()
        {
            lock (SafetyGate)
            {
                Check(NewlineWrites == 0, "engine never wrote CR or LF to the terminal");
            }
            Console.WriteLine("PASS: Engine write safety invariant.");
        }

        private static void CloseAll(List<FileStream> streams)
        {
            for (int i = 0; i < streams.Count; i++)
            {
                try
                {
                    streams[i].Dispose();
                }
                catch (IOException)
                {
                }
            }
            streams.Clear();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private sealed class Harness : IDisposable
        {
            private readonly object _gate = new object();
            private readonly Func<string> _initialScreen;
            private readonly Action<FakeTerminal, FakeSession> _configureTerminal;
            private readonly string _root;
            private readonly List<FakeTerminal> _terminals = new List<FakeTerminal>();
            private readonly List<FakeSession> _sessions = new List<FakeSession>();
            private readonly List<string> _failures = new List<string>();
            private readonly List<string> _transcripts = new List<string>();
            private readonly List<string> _diagnostics = new List<string>();
            private readonly List<int> _eventThreads = new List<int>();
            private readonly List<int> _takeRecordings = new List<int>();
            private readonly List<int> _takeTranscriptIds = new List<int>();
            private readonly List<string> _takeTranscriptTexts = new List<string>();
            private readonly List<int> _takeFailureIds = new List<int>();
            private readonly List<string> _takeFailureMessages = new List<string>();
            private readonly List<int> _takeDiscardIds = new List<int>();
            private int _discarded;
            private int _suspended;

            public readonly EngineOptions Options;
            public readonly DictationEngine Engine;

            public Harness(Func<string> initialScreen, Action<FakeTerminal, FakeSession> configureTerminal)
            {
                _initialScreen = initialScreen;
                _configureTerminal = configureTerminal;
                _root = Path.GetFullPath(Path.Combine(".test-output", "tmp",
                    "engine-" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(_root);

                Options = new EngineOptions();
                Options.ClaudePath = Path.Combine(_root, "claude.exe");
                Options.PollMs = 5;
                Options.HoldRepeatMs = 5;
                Options.ReadyTimeoutMs = 1500;
                Options.ListenTimeoutMs = 250;
                Options.FinalizeTimeoutMs = 2500;
                Options.ExportTimeoutMs = 500;
                Options.StableMs = 40;
                Options.NoSpeechMs = 150;

                Engine = new DictationEngine(Options, CreateTerminal, CreateSession);
                Engine.StateChanged += delegate(EngineState state)
                {
                    lock (_gate)
                    {
                        _eventThreads.Add(Thread.CurrentThread.ManagedThreadId);
                    }
                };
                Engine.Failed += delegate(string message)
                {
                    lock (_gate)
                    {
                        _failures.Add(message);
                    }
                };
                Engine.TranscriptReady += delegate(string text)
                {
                    lock (_gate)
                    {
                        _transcripts.Add(text);
                    }
                };
                Engine.Diagnostic += delegate(string message)
                {
                    lock (_gate)
                    {
                        _diagnostics.Add(message);
                    }
                };
                Engine.Discarded += delegate
                {
                    lock (_gate)
                    {
                        _discarded++;
                    }
                };
                Engine.TakeRecording += delegate(int id)
                {
                    lock (_gate)
                    {
                        _takeRecordings.Add(id);
                    }
                };
                Engine.TakeTranscript += delegate(int id, string text)
                {
                    lock (_gate)
                    {
                        _takeTranscriptIds.Add(id);
                        _takeTranscriptTexts.Add(text);
                    }
                };
                Engine.TakeFailed += delegate(int id, string message)
                {
                    lock (_gate)
                    {
                        _takeFailureIds.Add(id);
                        _takeFailureMessages.Add(message);
                    }
                };
                Engine.TakeDiscarded += delegate(int id)
                {
                    lock (_gate)
                    {
                        _takeDiscardIds.Add(id);
                    }
                };
                Engine.Suspended += delegate
                {
                    lock (_gate)
                    {
                        _suspended++;
                    }
                };
            }

            public int TerminalCount
            {
                get { lock (_gate) { return _terminals.Count; } }
            }

            public int SessionCount
            {
                get { lock (_gate) { return _sessions.Count; } }
            }

            public int FailureCount
            {
                get { lock (_gate) { return _failures.Count; } }
            }

            public int TranscriptCount
            {
                get { lock (_gate) { return _transcripts.Count; } }
            }

            public int DiagnosticCount
            {
                get { lock (_gate) { return _diagnostics.Count; } }
            }

            public int DiscardedCount
            {
                get { lock (_gate) { return _discarded; } }
            }

            public int SuspendedCount
            {
                get { lock (_gate) { return _suspended; } }
            }

            public int TakeRecordingCount
            {
                get { lock (_gate) { return _takeRecordings.Count; } }
            }

            public int TakeTranscriptCount
            {
                get { lock (_gate) { return _takeTranscriptIds.Count; } }
            }

            public int TakeFailureCount
            {
                get { lock (_gate) { return _takeFailureIds.Count; } }
            }

            public int TakeDiscardCount
            {
                get { lock (_gate) { return _takeDiscardIds.Count; } }
            }

            public bool HasTakeRecording(int id)
            {
                lock (_gate) { return _takeRecordings.Contains(id); }
            }

            public int CountTakeTranscripts(int id)
            {
                lock (_gate)
                {
                    int count = 0;
                    for (int i = 0; i < _takeTranscriptIds.Count; i++)
                    {
                        if (_takeTranscriptIds[i] == id)
                        {
                            count++;
                        }
                    }
                    return count;
                }
            }

            public string TakeTranscriptText(int id)
            {
                lock (_gate)
                {
                    for (int i = 0; i < _takeTranscriptIds.Count; i++)
                    {
                        if (_takeTranscriptIds[i] == id)
                        {
                            return _takeTranscriptTexts[i];
                        }
                    }
                }
                return null;
            }

            public int CountTakeFailures(int id)
            {
                lock (_gate)
                {
                    int count = 0;
                    for (int i = 0; i < _takeFailureIds.Count; i++)
                    {
                        if (_takeFailureIds[i] == id)
                        {
                            count++;
                        }
                    }
                    return count;
                }
            }

            public string TakeFailureText(int id)
            {
                lock (_gate)
                {
                    for (int i = 0; i < _takeFailureIds.Count; i++)
                    {
                        if (_takeFailureIds[i] == id)
                        {
                            return _takeFailureMessages[i];
                        }
                    }
                }
                return null;
            }

            public int CountTakeDiscards(int id)
            {
                lock (_gate)
                {
                    int count = 0;
                    for (int i = 0; i < _takeDiscardIds.Count; i++)
                    {
                        if (_takeDiscardIds[i] == id)
                        {
                            count++;
                        }
                    }
                    return count;
                }
            }

            public int CountTakeTerminalEvents(int id)
            {
                return CountTakeTranscripts(id) + CountTakeFailures(id) + CountTakeDiscards(id);
            }

            public bool AllTerminalsDisposed()
            {
                lock (_gate)
                {
                    for (int i = 0; i < _terminals.Count; i++)
                    {
                        if (!_terminals[i].IsDisposed)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }

            public bool AllSessionsDisposed()
            {
                lock (_gate)
                {
                    for (int i = 0; i < _sessions.Count; i++)
                    {
                        if (!_sessions[i].Disposed)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }

            public bool HasDiagnostic(string message)
            {
                lock (_gate) { return _diagnostics.Contains(message); }
            }

            public FakeTerminal TerminalAt(int index)
            {
                lock (_gate) { return _terminals[index]; }
            }

            public FakeSession SessionAt(int index)
            {
                lock (_gate) { return _sessions[index]; }
            }

            public bool HasFailure(string message)
            {
                lock (_gate) { return _failures.Contains(message); }
            }

            public string TranscriptAt(int index)
            {
                lock (_gate) { return _transcripts[index]; }
            }

            public bool HasEventOnOtherThread(int threadId)
            {
                lock (_gate)
                {
                    for (int i = 0; i < _eventThreads.Count; i++)
                    {
                        if (_eventThreads[i] != threadId)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            public bool WaitState(EngineState state, int timeoutMs)
            {
                return EngineTests.WaitUntil(delegate { return Engine.State == state; }, timeoutMs);
            }

            public bool WaitTerminalCount(int count, int timeoutMs)
            {
                return EngineTests.WaitUntil(delegate { return TerminalCount >= count; }, timeoutMs);
            }

            public bool WaitFailure(string message, int timeoutMs)
            {
                return EngineTests.WaitUntil(delegate { return HasFailure(message); }, timeoutMs);
            }

            public bool WaitTranscriptCount(int count, int timeoutMs)
            {
                return EngineTests.WaitUntil(delegate { return TranscriptCount >= count; }, timeoutMs);
            }

            public bool WaitDiscardedCount(int count, int timeoutMs)
            {
                return EngineTests.WaitUntil(delegate { return DiscardedCount >= count; }, timeoutMs);
            }

            public bool WaitTakeRecording(int id, int timeoutMs)
            {
                return EngineTests.WaitUntil(delegate { return HasTakeRecording(id); }, timeoutMs);
            }

            public bool WaitTakeTranscript(int id, int timeoutMs)
            {
                return EngineTests.WaitUntil(delegate { return CountTakeTranscripts(id) > 0; }, timeoutMs);
            }

            public bool WaitTakeFailure(int id, int timeoutMs)
            {
                return EngineTests.WaitUntil(delegate { return CountTakeFailures(id) > 0; }, timeoutMs);
            }

            public bool WaitTakeDiscard(int id, int timeoutMs)
            {
                return EngineTests.WaitUntil(delegate { return CountTakeDiscards(id) > 0; }, timeoutMs);
            }

            public bool WaitSuspended(int timeoutMs)
            {
                return EngineTests.WaitUntil(delegate { return SuspendedCount > 0; }, timeoutMs);
            }

            public void Dispose()
            {
                try
                {
                    Engine.Dispose();
                }
                catch (Exception)
                {
                }
                TryDelete(_root);
            }

            private ITerminalHost CreateTerminal()
            {
                FakeSession session;
                lock (_gate)
                {
                    session = _sessions[_sessions.Count - 1];
                }
                FakeTerminal terminal = new FakeTerminal(_initialScreen());
                terminal.ExportPath = session.ExportPath;
                if (_configureTerminal != null)
                {
                    _configureTerminal(terminal, session);
                }
                lock (_gate)
                {
                    _terminals.Add(terminal);
                }
                return terminal;
            }

            private IEngineSession CreateSession()
            {
                FakeSession session = new FakeSession(_root);
                lock (_gate)
                {
                    _sessions.Add(session);
                }
                return session;
            }
        }

        private sealed class FakeTerminal : ITerminalHost
        {
            private readonly object _gate = new object();
            private readonly List<string> _writes = new List<string>();
            private string _screen;
            private bool _disposed;

            public event Action<string> Output;
            public event Action<string> Closed;

            public string ExportPath;
            public string ExportText = "";
            public bool ExportOnCtrlG;
            public bool ClearOnExport;
            public bool ThrowOnWrite;
            public string ScreenAfterExport;
            public DateTime? ExportTimestampUtc;
            public Action OnCtrlG;
            public Action OnSpace;

            public FakeTerminal(string screen)
            {
                _screen = screen == null ? "" : screen;
            }

            public int WriteCount
            {
                get { lock (_gate) { return _writes.Count; } }
            }

            public bool IsDisposed
            {
                get { lock (_gate) { return _disposed; } }
            }

            public string WriteAt(int index)
            {
                lock (_gate) { return _writes[index]; }
            }

            public bool ContainsWrite(string text)
            {
                lock (_gate) { return _writes.Contains(text); }
            }

            public void SetScreen(string screen)
            {
                Action<string> handler;
                string value = screen == null ? "" : screen;
                lock (_gate)
                {
                    _screen = value;
                    handler = Output;
                }
                if (handler != null)
                {
                    handler(Decorate(value));
                }
            }

            public void Start(string executable, string[] arguments, string workingDirectory,
                IDictionary<string, string> environment)
            {
                Action<string> handler;
                string screen;
                lock (_gate)
                {
                    screen = _screen;
                    handler = Output;
                }
                if (handler != null && screen.Length > 0)
                {
                    handler(Decorate(screen));
                }
            }

            public bool TryWrite(string text)
            {
                Action space = null;
                Action ctrlG = null;
                string screenAfter = null;
                bool clear = false;
                bool export = false;
                bool throwOnWrite;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }
                    if (text != null && (text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0))
                    {
                        lock (EngineTests.SafetyGate)
                        {
                            EngineTests.NewlineWrites++;
                        }
                    }
                    _writes.Add(text);
                    throwOnWrite = ThrowOnWrite;
                    if (text == "\x07")
                    {
                        export = true;
                        ctrlG = OnCtrlG;
                        screenAfter = ScreenAfterExport;
                        clear = ClearOnExport;
                    }
                    else if (text == " ")
                    {
                        space = OnSpace;
                    }
                }

                if (throwOnWrite)
                {
                    throw new InvalidOperationException("fake terminal write failure");
                }
                if (export)
                {
                    if (ExportOnCtrlG && ExportPath != null)
                    {
                        try
                        {
                            File.WriteAllText(ExportPath, ExportText, new UTF8Encoding(false));
                            if (ExportTimestampUtc.HasValue)
                            {
                                File.SetLastWriteTimeUtc(ExportPath, ExportTimestampUtc.Value);
                            }
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                    if (screenAfter != null)
                    {
                        SetScreen(screenAfter);
                    }
                    else if (clear)
                    {
                        SetScreen(ReadyScreen(""));
                    }
                    if (ctrlG != null)
                    {
                        ctrlG();
                    }
                }
                if (space != null)
                {
                    space();
                }
                return true;
            }

            public void RaiseClosed(string reason)
            {
                Action<string> handler;
                lock (_gate)
                {
                    handler = Closed;
                }
                if (handler != null)
                {
                    handler(reason);
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                }
            }

            private static string Decorate(string snapshot)
            {
                return "\x1b[2J\x1b[H" + snapshot.Replace("\r\n", "\n").Replace("\n", "\r\n");
            }
        }

        private sealed class FakeSession : IEngineSession
        {
            public readonly string SessionDirectory;
            public readonly string ExportPathValue;
            public bool Disposed;

            public FakeSession(string root)
            {
                SessionDirectory = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(SessionDirectory);
                ExportPathValue = Path.Combine(SessionDirectory, "handoff.txt");
                Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Environment["MURMUR_CLEAR_PROMPT"] = "1";
                Environment["MURMUR_SESSION_DIRECTORY"] = SessionDirectory;
                Environment["MURMUR_HANDOFF_PATH"] = ExportPathValue;
                Arguments = new string[] { "--safe-mode" };
            }

            public string Workspace
            {
                get { return SessionDirectory; }
            }

            public string ExportPath
            {
                get { return ExportPathValue; }
            }

            public string[] Arguments { get; private set; }
            public IDictionary<string, string> Environment { get; private set; }

            public void Dispose()
            {
                Disposed = true;
            }
        }
    }
}
