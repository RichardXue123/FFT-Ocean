using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Assets.Scripts
{
    class PhysicsVerify
    {
        public static void CalcRMS(Texture2D heightMap , bool isFFT)
        {
            Color[] pixels = heightMap.GetPixels();
            double sumSq = 0;
            int n = pixels.Length;

            if (isFFT)
            {
                for (int i = 0; i < n; i++)
                {
                    float h = pixels[i].g; // FFT高度在G通道
                    sumSq += h * h;
                }
                float rms = Mathf.Sqrt((float)(sumSq / n));
                Debug.Log($"FFT RMS: {rms}");
            }
            else {
                for (int i = 0; i < n; i++)
                {
                    float h = pixels[i].r; // 波粒子高度在R通道
                    sumSq += h * h;
                }
                float rms = Mathf.Sqrt((float)(sumSq / n));
                Debug.Log($"WP RMS: {rms}");
            }
        }
    }
}
