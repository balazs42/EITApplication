using Unity;
using Utility.Logger;

namespace Utility.Composition
{
    public static class Settings
    {
        public static IUnityContainer ApplyContainerRegistration()
        {
            return Container
                .RegisterType<ILogger, DBLogger>();
        }
    }
}
