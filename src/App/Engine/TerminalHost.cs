using System;
using System.Collections.Generic;
using Murmur.Transport;

namespace Murmur.App.Engine
{
    public interface ITerminalHost : IDisposable
    {
        event Action<string> Output;
        event Action<string> Closed;

        void Start(string executable, string[] arguments, string workingDirectory,
            IDictionary<string, string> environment);

        bool TryWrite(string text);
    }

    public sealed class OwnedTerminalHost : ITerminalHost
    {
        private readonly OwnedTerminal _terminal = new OwnedTerminal();

        public event Action<string> Output
        {
            add { _terminal.Output += value; }
            remove { _terminal.Output -= value; }
        }

        public event Action<string> Closed
        {
            add { _terminal.Closed += value; }
            remove { _terminal.Closed -= value; }
        }

        public void Start(string executable, string[] arguments, string workingDirectory,
            IDictionary<string, string> environment)
        {
            _terminal.Start(executable, arguments, workingDirectory, environment);
        }

        public bool TryWrite(string text)
        {
            return _terminal.TryWrite(text);
        }

        public void Dispose()
        {
            _terminal.Dispose();
        }
    }
}
