namespace Utility.Classes.Meshing
{
    public class FEMElement
    {
        public int Id { get; set; }
        public Vertex V1 { get; set; }
        public Vertex V2 { get; set; }
        public Vertex V3 { get; set; }
        public double Area { get; set; }
        public FEMElement(int id, Vertex v1, Vertex v2, Vertex v3)
        {
            Id = id;
            V1 = v1;
            V2 = v2;
            V3 = v3;

            CalculateArea();
        }

        private void CalculateArea()
        {
            Area = Math.Abs(0.5 *
                            (V1.X * (V2.Y - V3.Y) +
                             V2.X * (V3.Y - V1.Y) +
                             V3.X * (V1.Y - V2.Y)));
        }
    }
}
