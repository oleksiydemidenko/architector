using System.Collections.Generic;

namespace Lab.Core
{
    public abstract class ModuleBase
    {
    }

    // Creates every module by reflection: no asset and no "new" names them.
    public static class ModuleLoader
    {
        public static List<ModuleBase> LoadAll()
        {
            var result = new List<ModuleBase>();
            foreach (var type in typeof(ModuleLoader).Assembly.GetTypes())
                if (!type.IsAbstract && type.IsSubclassOf(typeof(ModuleBase)))
                    result.Add((ModuleBase)System.Activator.CreateInstance(type));
            return result;
        }
    }
}
