using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Architector.Editor
{
    public static class ArchitectorIntegrationUpdates
    {
        private static readonly Dictionary<string, IArchitectorIntegrationUpdateProvider> providers = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, ArchitectorIntegrationUpdateState> states = new(StringComparer.Ordinal);
        public static event Action Changed;
        public static bool IsBusy => states.Values.Any(state => state.Checking || state.Installing);

        private static string PendingKey(string id) => "Architector.IntegrationUpdate." + Application.dataPath + "." + id;

        public static void Register(IArchitectorIntegrationUpdateProvider provider)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.Id))
                throw new ArgumentException("An update provider needs a stable ID.");
            if (providers.TryGetValue(provider.Id, out var existing) && !ReferenceEquals(existing, provider))
                throw new InvalidOperationException("An update provider already uses ID '" + provider.Id + "'.");
            var added = !providers.ContainsKey(provider.Id);
            providers[provider.Id] = provider;
            if (added && EditorPrefs.HasKey(PendingKey(provider.Id)))
            {
                EditorApplication.delayCall -= ResumePending;
                EditorApplication.delayCall += ResumePending;
            }
        }

        public static ArchitectorIntegrationUpdateState State(string id)
        {
            if (!states.TryGetValue(id, out var state))
                states.Add(id, state = new ArchitectorIntegrationUpdateState());
            return state;
        }

        public static void Unregister(string id)
        {
            if (states.TryGetValue(id, out var state) && (state.Checking || state.Installing))
                throw new InvalidOperationException("Wait for this integration operation to finish before unregistering it.");
            providers.Remove(id);
            states.Remove(id);
        }

        public static async void Check(IArchitectorIntegrationUpdateProvider provider) => await CheckAsync(provider);

        public static async Task CheckAsync(IArchitectorIntegrationUpdateProvider provider)
        {
            Register(provider);
            var state = State(provider.Id);
            if (IsBusy || ArchitectorPackageUpdates.IsBusy)
                return;
            state.Checking = true;
            state.Error = null;
            Changed?.Invoke();
            try
            {
                state.Update = await provider.CheckAsync() ?? throw new InvalidOperationException("The provider returned no update information.");
                var pending = EditorPrefs.GetString(PendingKey(provider.Id), string.Empty);
                if (pending.Length > 0)
                {
                    if (state.Update.installedVersion == pending.TrimStart('v'))
                    {
                        EditorPrefs.DeleteKey(PendingKey(provider.Id));
                        state.Notice = "Update installed.";
                    }
                    else
                        state.Notice = "The previous update did not finish. Check its installation before trying again.";
                }
            }
            catch (Exception exception)
            {
                if (!ArchitectorReleaseAssets.IsReloading)
                    state.Error = exception.Message;
            }
            finally
            {
                state.Checking = false;
                if (!ArchitectorReleaseAssets.IsReloading)
                    Changed?.Invoke();
            }
        }

        public static async void Install(IArchitectorIntegrationUpdateProvider provider, ArchitectorAiWorkCoordinator work) =>
            await InstallAsync(provider, work);

        public static async Task InstallAsync(IArchitectorIntegrationUpdateProvider provider, ArchitectorAiWorkCoordinator work)
        {
            Register(provider);
            var state = State(provider.Id);
            var blocker = ArchitectorPackageUpdates.OperationBlocker(work);
            if (IsBusy || blocker.Length > 0 || state.Update?.Available != true)
            {
                if (blocker.Length > 0)
                    state.Error = blocker;
                Changed?.Invoke();
                return;
            }
            state.Installing = true;
            state.Error = null;
            state.Notice = null;
            Changed?.Invoke();
            try
            {
                var offer = await provider.CheckAsync();
                blocker = ArchitectorPackageUpdates.OperationBlocker(work);
                if (blocker.Length > 0)
                    throw new InvalidOperationException(blocker);
                if (offer?.Available != true)
                    throw new InvalidOperationException("The update is no longer available or compatible. Check again.");
                EditorPrefs.SetString(PendingKey(provider.Id), offer.availableVersion);
                await provider.UpdateAsync(offer, work);
                if (ArchitectorReleaseAssets.IsReloading)
                    return;
                state.Update = await provider.CheckAsync();
                if (state.Update?.installedVersion != offer.availableVersion.TrimStart('v'))
                    throw new InvalidOperationException("The provider has not confirmed the requested installed version.");
                EditorPrefs.DeleteKey(PendingKey(provider.Id));
                state.Notice = "Update installed.";
            }
            catch (Exception exception)
            {
                if (!ArchitectorReleaseAssets.IsReloading)
                    state.Error = exception.Message;
            }
            finally
            {
                state.Installing = false;
                if (!ArchitectorReleaseAssets.IsReloading)
                    Changed?.Invoke();
            }
        }

        private static async void ResumePending()
        {
            if (IsBusy || ArchitectorPackageUpdates.IsBusy || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall -= ResumePending;
                EditorApplication.delayCall += ResumePending;
                return;
            }
            foreach (var provider in providers.Values.ToArray())
                if (providers.ContainsKey(provider.Id) && EditorPrefs.HasKey(PendingKey(provider.Id)))
                    await CheckAsync(provider);
        }
    }
}
