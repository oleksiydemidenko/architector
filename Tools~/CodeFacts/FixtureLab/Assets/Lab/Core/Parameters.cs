namespace Lab.Core
{
    public abstract class ParameterBase
    {
    }

    public abstract class Parameter<T> : ParameterBase
    {
        public T Value;
        public void Set(T value) { Value = value; }
    }

    public sealed class Container
    {
        public void Add<T>() where T : ParameterBase, new() { }
        public void Add(ParameterBase parameter) { }
        public T Get<T>() where T : ParameterBase => default;
    }

    public interface IInstaller
    {
        void Install(Container container);
    }

    public struct Point
    {
        public float X;
        public float Y;
    }
}
