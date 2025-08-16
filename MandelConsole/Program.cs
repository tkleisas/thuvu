// See https://aka.ms/new-console-template for more information
using System;

namespace MandelConsole
{
    class Program
    {
        static void Main(string[] args)
        { 
            int width = 80;
            int height = 24;

            int maxIterations = 100;

            // Create a 2D array to store the iteration count for each pixel
            int[,] iterationCount = new int[height, width];

            // Calculate the bounds of the complex plane
            double minX = -2.0;
            double maxX = 1.0;
            double minY = -1.5;
            double maxY = 1.5;

            // Calculate the scale for each pixel
            double scaleX = (maxX - minX) / width;
            double scaleY = (maxY - minY) / height;

            // Render the Mandelbrot set
            for (int y = 0; y < height; y++)
            { 
                double yCoord = minY + y * scaleY;
                for (int x = 0; x < width; x++)
                {
                    double xCoord = minX + x * scaleX;

                    // Initialize the complex number z
                    double zReal = 0.0;
                    double zImag = 0.0;

                    // Initialize the complex number c
                    double cReal = xCoord;
                    double cImag = yCoord;

                    // Iterate to find the number of iterations before escaping
                    int iteration = 0;
                    while (iteration < maxIterations)
                    {
                        double zRealNew = zReal * zReal - zImag * zImag + cReal;
                        double zImagNew = 2.0 * zReal * zImag + cImag;

                        zReal = zRealNew;
                        zImag = zImagNew;

                        // Check if the magnitude of z exceeds 2
                        double magnitude = Math.Sqrt(zReal * zReal + zImag * zImag);
                        if (magnitude > 2.0)
                        {
                            break;
                        }

                        iteration++;
                    }

                    // Store the iteration count
                    iterationCount[y, x] = iteration;
                }
            }

            // Render the Mandelbrot set with 16 colors using ANSI escape codes
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int iteration = iterationCount[y, x];
                    
                    // Map iteration count to a color index
                    int colorIndex = iteration % 16;
                    
                    // Use ANSI escape codes to change the color
                    Console.BackgroundColor = GetColor(colorIndex);
                    Console.ForegroundColor = ConsoleColor.White;
                    
                    // Print a character to represent the pixel
                    Console.Write(" ");
                }
                Console.WriteLine();
            }
        }

        // Helper method to get a ConsoleColor from a color index
        private static ConsoleColor GetColor(int index)
        {
            switch (index)
            {
                case 0: return ConsoleColor.Black;
                case 1: return ConsoleColor.Blue;
                case 2: return ConsoleColor.Cyan;
                case 3: return ConsoleColor.Green;
                case 4: return ConsoleColor.Magenta;
                case 5: return ConsoleColor.Red;
                case 6: return ConsoleColor.Yellow;
                case 7: return ConsoleColor.Gray;
                case 8: return ConsoleColor.DarkBlue;
                case 9: return ConsoleColor.DarkCyan;
                case 10: return ConsoleColor.DarkGreen;
                case 11: return ConsoleColor.DarkMagenta;
                case 12: return ConsoleColor.DarkRed;
                case 13: return ConsoleColor.DarkYellow;
                case 14: return ConsoleColor.White;
                case 15: return ConsoleColor.Gray;
                default: return ConsoleColor.Black;
            }
        }
    }
}
