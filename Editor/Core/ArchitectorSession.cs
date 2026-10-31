using System;
using UnityEditor;

namespace Architector.Editor
{
    public sealed class ArchitectorSession
    {
        private const string HistorySessionKey = "Architector.UndoHistory";
        private const string KeptUnlocksSessionKey = "Architector.KeptUnlocks";
        private static ArchitectorSession instance;

        private ArchitectorSession()
        {
            Repository = new ArchitectorRepository(ArchitectorPaths.ForCurrentProject());
            UserState = new ArchitectorUserStateStore(Repository.Paths);
            NodeClipboard = new ArchitectorNodeClipboard();
            TypeIndex = new ArchitectorTypeIndex(Repository);
            AiWork = new ArchitectorAiWorkCoordinator(
                true,
                recoveryPath: Repository.Paths.AiWorkRecoveryPath,
                locksWindows: () => ArchitectorAgentPreferences.Current.LockWindowsDuringAgentWork);
            ArchitectorAgentPreferences.Changed += AiWork.NotifyPreferencesChanged;
        }

        public static ArchitectorSession Instance
        {
            get
            {
                if (instance != null)
                    return instance;
                instance = new ArchitectorSession();
                RestoreHistory(instance.Repository);
                instance.Repository.RestoreKeptUnlocks(SessionState.GetString(KeptUnlocksSessionKey, string.Empty));
                EditorApplication.focusChanged += OnEditorFocusChanged;
                AssemblyReloadEvents.beforeAssemblyReload += PreserveHistory;
                return instance;
            }
        }

        public ArchitectorRepository Repository { get; }
        public ArchitectorUserStateStore UserState { get; }
        public ArchitectorNodeClipboard NodeClipboard { get; }
        public ArchitectorTypeIndex TypeIndex { get; }
        public ArchitectorAiWorkCoordinator AiWork { get; }

        private static void PreserveHistory()
        {
            if (instance == null)
                return;
            SessionState.SetString(HistorySessionKey, instance.Repository.ExportHistory());
            SessionState.SetString(KeptUnlocksSessionKey, instance.Repository.ExportKeptUnlocks());
        }

        private static void RestoreHistory(ArchitectorRepository repository)
        {
            var history = SessionState.GetString(HistorySessionKey, string.Empty);
            SessionState.EraseString(HistorySessionKey);
            try
            {
                repository.ImportHistory(history);
            }
            catch (ArchitectorDataException)
            {
                repository.ImportHistory(string.Empty);
            }
        }

        private static void OnEditorFocusChanged(bool focused)
        {
            if (focused)
                instance?.Repository.RefreshExternalChanges();
        }

        public static bool TryGet(out ArchitectorSession session, out string error)
        {
            try
            {
                session = Instance;
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                session = null;
                error = exception.Message;
                return false;
            }
        }
    }
}
