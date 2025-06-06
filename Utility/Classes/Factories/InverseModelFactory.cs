namespace Utility.Classes.Factories
{
    public static class InverseModelFactory
    {
        public interface IInverseModel
        {
            public InverseSolution Solve();
        }

        public static InverseModel Create()
        {

        }


    }
}
