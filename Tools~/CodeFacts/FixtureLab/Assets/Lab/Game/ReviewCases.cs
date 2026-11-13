using System;
using Lab.Core;

namespace Lab.Game
{
    // Creation paths found in review: each type below is created by code and stays not_tracked; BaseReviewInstaller is the
    // base of a created type.
    public sealed class StaticCache<T> where T : new()
    {
        public static T Make() => new T();
    }

    public interface IInstallerMaker
    {
        T Make<T>() where T : IInstaller;
    }

    public sealed class ActivatorMaker : IInstallerMaker
    {
        public T Make<T>() where T : IInstaller => Activator.CreateInstance<T>();
    }

    public static class ReviewCreations
    {
        private static readonly InstallerPool<TargetTypedInstaller> Pool = new();

        public static T ViaTypeOf<T>() => (T)Activator.CreateInstance(typeof(T));

        public static void Build(IInstallerMaker maker)
        {
            ViaTypeOf<TypeOfGenericInstaller>();
            StaticCache<StaticCacheInstaller>.Make();
            maker.Make<InterfaceInstaller>();
            Func<GroupInstaller> make = InstallerFactory.Create<GroupInstaller>;
            RentedInstaller.Rent();
            new DerivedReviewInstaller();
            UnityEngine.ScriptableObject.CreateInstance<SharedAsset>();
        }
    }

    [Serializable] public sealed class TargetTypedInstaller : IInstaller { public void Install(Container container) { } }
    [Serializable] public sealed class TypeOfGenericInstaller : IInstaller { public void Install(Container container) { } }
    [Serializable] public sealed class StaticCacheInstaller : IInstaller { public void Install(Container container) { } }
    [Serializable] public sealed class InterfaceInstaller : IInstaller { public void Install(Container container) { } }
    [Serializable] public sealed class GroupInstaller : IInstaller { public void Install(Container container) { } }

    [Serializable]
    public sealed class RentedInstaller : IInstaller
    {
        public static RentedInstaller Rent() => InstallerFactory.Create<RentedInstaller>();

        public void Install(Container container) { }
    }

    [Serializable] public class BaseReviewInstaller : IInstaller { public void Install(Container container) { } }
    [Serializable] public sealed class DerivedReviewInstaller : BaseReviewInstaller { }

    // Created by Editor code and by run-time code: the run-time site is the reason.
    public sealed class SharedAsset : UnityEngine.ScriptableObject { }

    // OnEnable that adds and removes the same handler: no unpaired subscription.
    public sealed class ToggleListener : UnityEngine.MonoBehaviour
    {
        private void OnEnable()
        {
            LeakyListener.Ticked += Handle;
            LeakyListener.Ticked -= Handle;
        }

        private void Handle() { }
    }
}
