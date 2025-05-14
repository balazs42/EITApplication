using Unity;
using Unity.Injection;
using Unity.Resolution;

namespace Utility.Composition
{
    public class Container
    {
        private static IUnityContainer? unityContainer;

        public static IUnityContainer InitializeContainer()
        {
            unityContainer = new UnityContainer();
            return unityContainer;
        }

        public static IUnityContainer RegisterType<Type, RType>() where RType : Type
        {
            return unityContainer.RegisterType<Type, RType>();
        }

        public static T ResolveObject<T>()
        {
            return unityContainer.Resolve<T>();
        }

        public static T ResolvePageWithViewModel<T,Z>(Z viewModel)
        {
            var func = GetFunc<Z, T>();
            return func(viewModel);
        }

        public static Z ResolveViewModelWithParam<Z,T>(T param)
        {
            var viewModelFunc = GetFunc<T, Z>();
            return viewModelFunc(param);
        }

        public static Z ResolveViewModelWithParams<Z, T, U>(T param1, U param2)
        {
            var viewModelFunc = GetFunc<T, U, Z>();
            return viewModelFunc(param1, param2);
        }

        private static Func<T, Z> GetFunc<T, Z>()
        {
            unityContainer.RegisterType<Func<T, Z>>(new InjectionFactory(_ =>
                    new Func<T, Z>(x =>
                    {
                        return _.Resolve<Z>(new DependencyOverride<T>(x));
                    })));

            return unityContainer.Resolve<Func<T, Z>>();
        }

        private static Func<T, U, Z> GetFunc<T, U, Z>()
        {
            unityContainer.RegisterType<Func<T, U, Z>>(new InjectionFactory(_ =>
                    new Func<T, U, Z>((x, y) =>
                    {
                        return _.Resolve<Z>(new DependencyOverride<T>(x), new DependencyOverride<U>(y));
                    })));

            return unityContainer.Resolve<Func<T, U, Z>>();
        }
    }
}
