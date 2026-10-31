using System;
using System.Collections.Generic;
using Murmur.Probe;

namespace Murmur.App.Engine
{
    public interface IEngineSession : IDisposable
    {
        string Workspace { get; }
        string ExportPath { get; }
        string[] Arguments { get; }
        IDictionary<string, string> Environment { get; }
    }

    public sealed class ProfileSession : IEngineSession
    {
        private readonly IsolatedProfile _profile;
        private bool _disposed;

        public ProfileSession()
        {
            _profile = new IsolatedProfile();
            _profile.EnvironmentOverrides["MURMUR_CLEAR_PROMPT"] = "1";
        }

        public string Workspace
        {
            get { return _profile.Workspace; }
        }

        public string ExportPath
        {
            get { return _profile.ExportPath; }
        }

        public string[] Arguments
        {
            get { return _profile.Arguments; }
        }

        public IDictionary<string, string> Environment
        {
            get { return _profile.EnvironmentOverrides; }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _profile.CleanupKnownFiles();
        }
    }
}
