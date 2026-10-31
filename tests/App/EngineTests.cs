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
                EngineReadyTests();
                EngineTakeTests();
                EngineFailureTests();
                EngineLifecycleTests();
                EngineCancelQuietTests();
                EngineStopPhaseTests();
                EngineExportSafetyTests();
                EngineFaultTests();
                EnginePromptSafetyTests();
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
                Check(harness.WaitState(EngineState.Recording, 3000), "draft take records");
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
                harness.Engine.BeginTake();
                Check(harness.WaitState(EngineState.Recording, 3000), "stale handoff records");
                holding = false;
                FakeTerminal terminal = harness.TerminalAt(0);
                terminal.SetScreen(ReadyScreen("fresh words"));
                harness.Engine.EndTake();
                Check(harness.WaitFailure("Could not export the transcript", 3000),
                    "stale handoff fails export");
                Check(harness.TranscriptCount == 0, "stale handoff never raises transcript");
                Check(harness.WaitState(EngineState.Ready, 3000), "stale handoff returns ready");
            }

            // Failing to delete a stale ExportPath fails the export and recycles.
            List<FileStream> locks = new List<FileStream>();
            try
            {
                using (Harness harness = new Harness(delegate { return ReadyScreen("stale draft"); },
                    delegate(FakeTerminal terminal, FakeSession session)
                    {
                        locks.Add(new FileStream(session.ExportPath, FileMode.Create,
                            FileAccess.ReadWrite, FileShare.None));
                    }))
                {
                    harness.Engine.Warm();
                    Check(harness.WaitState(EngineState.Ready, 3000), "locked handoff setup ready");
                    harness.Engine.BeginTake();
                    Check(harness.WaitFailure("Could not export the transcript", 3000),
                        "locked stale handoff fails export");
                    Check(harness.WaitTerminalCount(2, 3000), "locked stale handoff recycles child");
                    Check(harness.WaitState(EngineState.Ready, 3000), "locked stale handoff settles ready");
                    Check(harness.TranscriptCount == 0, "locked stale handoff no transcript");
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
            const string Setup = "Claude needs one-time setup \u2014 choose Sign in from the tray menu";

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
            private int _discarded;

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
