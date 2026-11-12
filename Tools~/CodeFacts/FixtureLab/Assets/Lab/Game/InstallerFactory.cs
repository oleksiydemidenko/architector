using System;
using Lab.Core;

namespace Lab.Game
{
    // [SerializeReference]-only types that code creates without "new X()": a generic method that creates its type argument
    // (directly, through another generic method, or through an inferred call), Activator with typeof, and a type that is
    // only named by typeof. None of them is in an asset, so only ReadOnlyInstaller is unwired.
    public static class InstallerFactory
    {
        public static T Create<T>() where T : IInstaller, new() => new T();

        public static T Ensure<T>() where T : IInstaller, new() => Create<T>();

        public static T Spawn<T>() where T : IInstaller => Activator.CreateInstance<T>();

        public static bool TryMake<T>(out T made) where T : IInstaller
        {
            made = Spawn<T>();
            return true;
        }

        public static T Find<T>() where T : IInstaller => default;

        public static T Install<T>(this Container container) where T : IInstaller, new() => Ensure<T>();

        public static void Build()
        {
            Create<FactoryInstaller>();
            Ensure<ForwardedInstaller>();
            TryMake(out InferredInstaller inferred);
            Activator.CreateInstance(typeof(TypeOfInstaller));
            Find<ReadOnlyInstaller>();
        }

        public static void Extend(Container container) => container.Install<ExtensionInstaller>();
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HandlesAttribute : Attribute
    {
        public HandlesAttribute(Type handled) { }
    }

    [Handles(typeof(NamedInstaller))]
    public sealed class NamedInstallerTool { }

    [Serializable]
    public sealed class FactoryInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [Serializable]
    public sealed class ForwardedInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [Serializable]
    public sealed class InferredInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [Serializable]
    public sealed class TypeOfInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [Serializable]
    public sealed class NamedInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [Serializable]
    public sealed class ExtensionInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    [Serializable]
    public sealed class ReadOnlyInstaller : IInstaller
    {
        public void Install(Container container) { }
    }

    // A registry lists types for serialization and creates none: ReadOnlyInstaller and UnusedInstaller stay unwired.
    [AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
    public sealed class KnownInstallerAttribute : Attribute
    {
        public KnownInstallerAttribute(int tag, Type known) { }
    }

    [KnownInstaller(0, typeof(ReadOnlyInstaller))]
    [KnownInstaller(1, typeof(UnusedInstaller))]
    public interface IKnownInstallers
    {
    }
}
