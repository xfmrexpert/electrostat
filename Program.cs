using System.Collections.Generic;

namespace electrostat
{
    internal class Program
    {
        static void Main(string[] args)
        {
            System.Console.WriteLine("Hello, World!");
            System.Console.WriteLine("Building electrostatic models with domain clipping enabled...\n");

            foreach (bool withLVCornerAngle in new[] { true })
            {
                foreach (var ex in Examples.All(withLVCornerAngle))
                {
                    System.Console.WriteLine($"\n=== Building {ex.Name} ===");

                    string caseName = SafeName(ex.Name);
                    if (!withLVCornerAngle) caseName += "/noLVcorner";

                    GeometryBuilder.ResetGeometry();
                    GeometryBuilder.BuildModel(
                        ex,
                        lc: 5.0,
                        mshOut: $"{caseName}/geom.msh",
                        clipToDomain: true);
                    GeometryBuilder.RunGetDPAnalysis();
                    return;
                }
            }

            System.Console.WriteLine("\n=== All models built successfully with domain clipping! ===");
        }

        private static string SafeName(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.ToString();
        }
    }
}
