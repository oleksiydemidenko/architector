using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Architector.Editor
{
    public sealed class ArchitectorAiWorkCoordinator
    {
        private const string DefaultPersistenceKey = "Architector.AiWork";

        private readonly bool persistAcrossDomainReload;
        private readonly string persistenceKey;
        private readonly string recoveryPath;
        private readonly Func<bool> locksWindows;
        private string workToken = string.Empty;
        private string label = string.Empty;
        private int startedRevision = -1;
        private string[] knownEntries = Array.Empty<string>();
        private bool workspacesUnlockedByUser;
        private bool settingsUnlockedByUser;
        private bool awaitingAgentResume;

        public ArchitectorAiWorkCoordinator(
            bool persistAcrossDomainReload = false,
            string persistenceKey = DefaultPersistenceKey,
            string recoveryPath = null,
            Func<bool> locksWindows = null)
        {
            this.locksWindows = locksWindows;
            this.persistAcrossDomainReload = persistAcrossDomainReload;
            this.persistenceKey = string.IsNullOrWhiteSpace(persistenceKey)
                ? DefaultPersistenceKey
                : persistenceKey.Trim();
            this.recoveryPath = string.IsNullOrWhiteSpace(recoveryPath) ? string.Empty : Path.GetFullPath(recoveryPath);
            if (!persistAcrossDomainReload)
                return;

            RestorePersistedState();
            awaitingAgentResume = workToken.Length > 0;
        }

        public event Action Changed;

        public bool IsActive => workToken.Length > 0;
        public bool IsAwaitingAgentResume => IsActive && awaitingAgentResume;
        public string WorkToken => workToken;
        public string Label => label;
        public int StartedRevision => startedRevision;
        public IReadOnlyCollection<string> KnownEntries => knownEntries;
        public bool LocksWindows => locksWindows?.Invoke() ?? true;
        public bool WorkspacesLocked => IsActive && LocksWindows && !workspacesUnlockedByUser;
        public bool SettingsLocked => IsActive && LocksWindows && !settingsUnlockedByUser;

        public void NotifyPreferencesChanged()
        {
            Changed?.Invoke();
        }
        public bool WorkspacesUnlockedByUser => IsActive && workspacesUnlockedByUser;
        public bool SettingsUnlockedByUser => IsActive && settingsUnlockedByUser;

        public string Begin(string requestedLabel, int repositoryRevision = -1, IEnumerable<string> existingEntries = null)
        {
            if (IsActive)
                throw new ArchitectorDataException("An AI work session is already active.");
            workToken = $"ai-work_{Guid.NewGuid():N}";
            label = string.IsNullOrWhiteSpace(requestedLabel) ? "AI Working" : requestedLabel.Trim();
            startedRevision = repositoryRevision;
            knownEntries = existingEntries?.ToArray() ?? Array.Empty<string>();
            workspacesUnlockedByUser = false;
            settingsUnlockedByUser = false;
            awaitingAgentResume = false;
            try
            {
                Persist();
            }
            catch
            {
                workToken = string.Empty;
                label = string.Empty;
                startedRevision = -1;
                knownEntries = Array.Empty<string>();
                throw;
            }
            Changed?.Invoke();
            return workToken;
        }

        public void End(string token)
        {
            RequireToken(token, false);
            ClearSession();
        }

        public void RequireMutationToken(string token)
        {
            if (IsActive)
                RequireToken(token, true);
            else if (!string.IsNullOrWhiteSpace(token))
                throw new ArchitectorDataException("The supplied AI work token belongs to a session that is no longer active.");
        }

        public void Resume(string token)
        {
            RequireToken(token, true);
        }

        public void AbandonInterruptedSessionByUser()
        {
            if (IsAwaitingAgentResume)
                ClearSession();
        }

        public void UnlockWorkspacesByUser()
        {
            if (!IsActive || workspacesUnlockedByUser)
                return;
            workspacesUnlockedByUser = true;
            Changed?.Invoke();
        }

        public void RelockWorkspaces()
        {
            if (!IsActive || !workspacesUnlockedByUser)
                return;
            workspacesUnlockedByUser = false;
            Changed?.Invoke();
        }

        public void UnlockSettingsByUser()
        {
            if (!IsActive || settingsUnlockedByUser)
                return;
            settingsUnlockedByUser = true;
            Changed?.Invoke();
        }

        public void RelockSettings()
        {
            if (!IsActive || !settingsUnlockedByUser)
                return;
            settingsUnlockedByUser = false;
            Changed?.Invoke();
        }

        private string TokenKey => persistenceKey + ".Token";
        private string LabelKey => persistenceKey + ".Label";
        private string RevisionKey => persistenceKey + ".Revision";
        private string EntriesKey => persistenceKey + ".Entries";

        private void RequireToken(string token, bool confirmsAgentResume)
        {
            if (!IsActive)
                throw new ArchitectorDataException("No AI work session is active.");
            if (!string.Equals(workToken, token?.Trim(), StringComparison.Ordinal))
                throw new ArchitectorDataException("The AI work token is missing or does not own the active session.");
            if (!confirmsAgentResume || !awaitingAgentResume)
                return;
            awaitingAgentResume = false;
            Changed?.Invoke();
        }

        private void ClearSession()
        {
            ClearPersistedState();
            workToken = string.Empty;
            label = string.Empty;
            startedRevision = -1;
            knownEntries = Array.Empty<string>();
            workspacesUnlockedByUser = false;
            settingsUnlockedByUser = false;
            awaitingAgentResume = false;
            Changed?.Invoke();
        }

        private void Persist()
        {
            if (!persistAcrossDomainReload)
                return;
            if (recoveryPath.Length > 0)
            {
                ArchitectorJson.WriteAtomic(recoveryPath, new RecoveryMarker
                {
                    workToken = workToken,
                    label = label,
                    startedRevision = startedRevision,
                    knownEntries = knownEntries.ToList()
                });
            }
            SessionState.SetString(TokenKey, workToken);
            SessionState.SetString(LabelKey, label);
            SessionState.SetInt(RevisionKey, startedRevision);
            SessionState.SetString(EntriesKey, string.Join("\n", knownEntries));
        }

        private void RestorePersistedState()
        {
            if (recoveryPath.Length > 0 && File.Exists(recoveryPath))
            {
                var marker = ArchitectorJson.Read<RecoveryMarker>(recoveryPath);
                if (marker.schemaVersion != 1 || string.IsNullOrWhiteSpace(marker.workToken))
                    throw new ArchitectorDataException($"AI work recovery marker '{recoveryPath}' is invalid.");
                workToken = marker.workToken.Trim();
                label = string.IsNullOrWhiteSpace(marker.label) ? "AI Working" : marker.label.Trim();
                startedRevision = marker.startedRevision;
                knownEntries = marker.knownEntries?.ToArray() ?? Array.Empty<string>();
                SessionState.SetString(TokenKey, workToken);
                SessionState.SetString(LabelKey, label);
                SessionState.SetInt(RevisionKey, startedRevision);
                SessionState.SetString(EntriesKey, string.Join("\n", knownEntries));
                return;
            }

            workToken = SessionState.GetString(TokenKey, string.Empty);
            label = SessionState.GetString(LabelKey, string.Empty);
            startedRevision = SessionState.GetInt(RevisionKey, -1);
            knownEntries = SessionState.GetString(EntriesKey, string.Empty)
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void ClearPersistedState()
        {
            if (!persistAcrossDomainReload)
                return;
            if (recoveryPath.Length > 0 && File.Exists(recoveryPath))
                File.Delete(recoveryPath);
            SessionState.EraseString(TokenKey);
            SessionState.EraseString(LabelKey);
            SessionState.EraseInt(RevisionKey);
            SessionState.EraseString(EntriesKey);
        }

        [Serializable]
        private sealed class RecoveryMarker
        {
            public int schemaVersion = 1;
            public string workToken = string.Empty;
            public string label = string.Empty;
            public int startedRevision = -1;
            public List<string> knownEntries = new();
        }
    }
}
